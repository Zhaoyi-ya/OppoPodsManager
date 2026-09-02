using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Assets.UserSettings;
namespace OppoPodsManager.Control.Abstractions;
// 协调当前品牌管理器，并把设备状态桥接到前端状态容器。
public sealed class ControlManager : IAsyncDisposable
{
    private readonly FrontendState _frontendState;
    private readonly DeviceScanner? _deviceScanner;
    private readonly IReadOnlyDictionary<string, IBrandManagerFactory> _managerFactories;
    private readonly SettingsStore? _settings;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly SemaphoreSlim _reconnectWake = new(0, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _availableDevicesLock = new();
    private IReadOnlyDictionary<string, DeviceConnectionPlan> _availableDevices = new Dictionary<string, DeviceConnectionPlan>();
    private readonly HashSet<string> _confirmedDeviceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _probingDeviceIds = new(StringComparer.Ordinal);
    // 品牌探测负结果缓存：StableId → (Brand → 失败时间戳 TickCount64)。TTL 内 Discovery
    // 确认循环跳过该品牌，防止 DeviceWatcher 每次重新上报设备都重跑 10-20 秒的全品牌探测。
    // 手动/自动连接（ConnectPlanAsync）不受缓存限制，用户显式重试总是全品牌再试一遍。
    private readonly Dictionary<string, Dictionary<string, long>> _brandProbeFailures = new(StringComparer.Ordinal);
    private static readonly TimeSpan BrandProbeFailureTtl = TimeSpan.FromMinutes(10);
    // 单设备单轮品牌探测总预算：所有品牌加起来超过该时限即终止本轮（避免极端情况下 UI 长时间无反馈）。
    private const int ProbeBudgetMs = 30_000;
    // 会话看门狗参数：最后一次发送已超过单请求超时窗口（4s）仍无接收，且距最后接收超过阈值 ⇒ 判死。
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionDeadAfter = TimeSpan.FromSeconds(15);
    private const int SendGraceMs = 5_000;
    private IBrandManager? _activeManager;
    private string? _activeDeviceId;
    private string? _manualDisconnectDeviceId;
    private int _autoConnectInProgress;
    private int _disposed;
    private long _lastDiscoveryGeneration;
    private CancellationTokenSource? _selectionCancellation;
    private Task? _reconnectTask;
    public ControlManager(
        FrontendState frontendState,
        DeviceScanner? deviceScanner = null,
        IEnumerable<IBrandManagerFactory>? managerFactories = null,
        SettingsStore? settings = null)
    {
        _frontendState = frontendState;
        _deviceScanner = deviceScanner;
        _managerFactories = (managerFactories ?? [])
            .ToDictionary(factory => factory.Brand, StringComparer.OrdinalIgnoreCase);
        _settings = settings;
        if (_deviceScanner is not null)
            _deviceScanner.PlansChanged += OnPlansChanged;
        _frontendState.InteractivePollingChanged += OnInteractivePollingChanged;
    }
    public IBrandManager? ActiveManager => _activeManager;
    public string? ActiveDeviceId => _activeDeviceId;
    public void StartMonitoring()
    {
        _deviceScanner?.StartMonitoring();
        _reconnectTask ??= RunReconnectLoopAsync();
        SignalReconnect("控制层启动");
    }
    public event EventHandler<DeviceOptionsChangedEventArgs>? AvailableDevicesChanged;
    private async void OnPlansChanged(object? sender, DevicePlansChangedEventArgs args)
    {
        try
        {
            await ApplyPlansAsync(args.Plans, CancellationToken.None, args.Generation);
            SignalReconnect("蓝牙设备列表变化");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Discovery", "处理蓝牙设备变化失败。", exception);
        }
    }
    private async Task ApplyPlansAsync(
        IReadOnlyList<DeviceConnectionPlan> plans,
        CancellationToken cancellationToken,
        long generation = 0)
    {
        if (generation > 0)
        {
            var previous = Interlocked.Read(ref _lastDiscoveryGeneration);
            if (generation <= previous)
                return;
            Interlocked.Exchange(ref _lastDiscoveryGeneration, generation);
        }
        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
            await ApplyPlansCoreAsync(plans, cancellationToken);
        }
        finally
        {
            _discoveryGate.Release();
        }
    }
    private async Task ApplyPlansCoreAsync(
        IReadOnlyList<DeviceConnectionPlan> plans,
        CancellationToken cancellationToken)
    {
        plans = ResolveSupportedPlans(plans);
        var next = plans.ToDictionary(plan => plan.Candidate.StableId, StringComparer.Ordinal);
        string? removedActiveId;
        lock (_availableDevicesLock)
        {
            removedActiveId = _activeDeviceId is not null && !next.ContainsKey(_activeDeviceId)
                ? _activeDeviceId
                : null;
            _availableDevices = next;
            _confirmedDeviceIds.RemoveWhere(id => !next.ContainsKey(id));
            // 设备已从蓝牙列表消失：清除其品牌探测负结果缓存，重新出现时从完整品牌序列重新探测。
            foreach (var removedId in _brandProbeFailures.Keys.Where(id => !next.ContainsKey(id)).ToArray())
                _brandProbeFailures.Remove(removedId);
            if (_manualDisconnectDeviceId is not null && !next.ContainsKey(_manualDisconnectDeviceId))
                _manualDisconnectDeviceId = null;
        }
        // 跳过信息不完整（配对/连接进行中）的设备，避免在设备名/服务 UUID 尚未就绪时用错误品牌抢连。
        var candidatesToConfirm = plans
            .Where(plan => !plan.InfoIncomplete)
            .Where(plan => !_confirmedDeviceIds.Contains(plan.Candidate.StableId))
            .Where(plan => !_probingDeviceIds.Contains(plan.Candidate.StableId))
            .ToArray();
        foreach (var plan in candidatesToConfirm)
            await ConfirmDeviceAsync(plan, cancellationToken);
        var options = GetAvailableDeviceOptions();
        AvailableDevicesChanged?.Invoke(this, new DeviceOptionsChangedEventArgs(options));
        if (removedActiveId is not null)
        {
            ApplicationLog.Current?.Info("Discovery", $"当前设备已从蓝牙设备列表移除：id={removedActiveId}。");
            await ClearActiveManagerAsync();
            DeviceConnectionPlan? replacement;
            lock (_availableDevicesLock)
                replacement = _availableDevices.Values.FirstOrDefault(plan =>
                    !plan.InfoIncomplete
                    && _confirmedDeviceIds.Contains(plan.Candidate.StableId)
                    && _managerFactories.ContainsKey(plan.Brand));
            if (replacement is not null)
                await ConnectAsync(replacement.Candidate.StableId, CancellationToken.None);
            return;
        }
    }
    // 合并启动、设备变更与手动请求产生的重连信号，避免同一轮变化重复建立连接。
    private void SignalReconnect(string reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            if (_reconnectWake.CurrentCount == 0)
            {
                _reconnectWake.Release();
                ApplicationLog.Current?.Debug("Control", $"已投递自动连接信号：reason={reason}。");
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }
    // Windows 与原项目一致：连接失败后不按固定间隔盲重试，只等待下一次设备状态变化或用户请求；
    // 循环同时以 5 秒周期醒来做会话活性检查（看门狗）。发送后持续无接收的死会话会被拆除并
    // 清空负结果缓存、触发重新探测——这是“会话已建立但设备永不再响应”（误锁/被手机抢连/
    // 耳机异常休眠）的最后防线。空闲但健康的会话（无发送）不会被误判。
    private async Task RunReconnectLoopAsync()
    {
        try
        {
            while (!Volatile.Read(ref _disposed).Equals(1))
            {
                var signaled = await _reconnectWake.WaitAsync(WatchdogInterval, _lifetimeCancellation.Token);
                while (_reconnectWake.CurrentCount > 0)
                    await _reconnectWake.WaitAsync(_lifetimeCancellation.Token);
                if (ActiveManager is { } manager && IsSessionDead(manager))
                {
                    var deadDeviceId = _activeDeviceId;
                    ApplicationLog.Current?.Info("Control",
                        $"会话看门狗：活动会话长时间无协议响应，已拆除并触发重新探测。device={deadDeviceId}，manager={manager.GetType().Name}。");
                    await ClearActiveManagerAsync();
                    if (deadDeviceId is not null)
                        ClearProbeFailures(deadDeviceId);
                    SignalReconnect("会话活性超时");
                    continue;
                }
                // 仅由真实信号（设备变化/用户请求/看门狗拆除）驱动自动连接，周期性醒来只做看门狗检查。
                if (!signaled)
                    continue;
                if (ActiveManager is not null || _manualDisconnectDeviceId is not null)
                    continue;
                await ConnectFirstAvailableAsync(_lifetimeCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Control", "自动连接循环异常终止。", exception);
        }
    }

    // 死会话判定：最后一次活动是发送（存在未得到响应的轮询），最后一次发送已超过单请求
    // 4s 超时窗口仍无任何接收，且距最后接收已超过 SessionDeadAfter。不发送的空闲会话
    // （LastSend <= LastReceive）以及不适用链路活性的品牌（如 BLE 广播型 Apple，返回 null）
    // 一律不判死，避免误拆健康会话。
    private static bool IsSessionDead(IBrandManager manager)
    {
        if (manager.SessionLiveness is not { } liveness)
            return false;
        var now = Environment.TickCount64;
        return liveness.LastSendTicks > liveness.LastReceiveTicks
            && now - liveness.LastSendTicks > SendGraceMs
            && now - liveness.LastReceiveTicks > SessionDeadAfter.TotalMilliseconds;
    }
    // 所有已连接蓝牙设备都进入控制层；名称命中的品牌优先验证，其余品牌仅在前者失败后尝试。
    private IReadOnlyList<DeviceConnectionPlan> ResolveSupportedPlans(IEnumerable<DeviceConnectionPlan> plans)
    {
        var resolved = new List<DeviceConnectionPlan>();
        foreach (var plan in plans)
        {
            // 配对/连接进行中 Windows 无法读取设备名时会回落成 "耳机 XXXXXXXXXXXX"（MAC 地址），
            // 此时品牌识别不可靠——名字命中和服务命中全为 false，回退到字母序让 Edifier 排第一，
            // 用其 edf00000 UUID 去连根本没有该服务的 vivo/OPPO，必然超时崩溃。
            var infoIncomplete = IsFallbackDeviceName(plan.Candidate.DisplayName);
            // 排序：名称命中 > 专属服务 UUID（强证据）> 服务 UUID 命中 > 通用 SPP 兜底（弱证据，
            // 如华为的标准 00001101——几乎所有串口蓝牙设备都会应答其 RFCOMM 建链，必须最后尝试
            // 且依赖握手验证兜底，否则会把 vivo 等其它品牌误锁成华为会话）。
            // 同 MAC 已确认过品牌时，将其置顶优先，跳过全品牌探测，减少首次识别等待。
            var cachedBrand = plan.Candidate.BluetoothAddress is { } mac ? _settings?.GetBrandForMac(mac) : null;
            var factories = _managerFactories.Values
                .OrderByDescending(factory => cachedBrand is not null
                    && string.Equals(factory.Brand, cachedBrand, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(factory => factory.IsCandidateName(plan.Candidate.DisplayName))
                .ThenByDescending(factory => factory.ProbeEvidence == BrandProbeEvidence.DedicatedService)
                .ThenByDescending(factory => plan.Candidate.ServiceIds.Contains(factory.ServiceId))
                .ThenBy(factory => factory.Brand, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (factories.Length == 0)
            {
                continue;
            }
            var candidateBrands = factories.Select(factory => factory.Brand).ToArray();
            if (infoIncomplete)
            {
                // 信息不完整时不锁定品牌和 ServiceId，把全部候选品牌交给连接层逐个尝试；
                // 自动连接/确认阶段会跳过此类设备，等配对完成后 DisplayName 变为真实名称再正常解析。
                ApplicationLog.Current?.Debug(
                    "Discovery",
                    $"设备信息不完整（配对/连接进行中），暂不锁定品牌：device={plan.Candidate.DisplayName}，候选={string.Join(',', candidateBrands)}，缓存服务数={plan.Candidate.ServiceIds.Count}。");
                resolved.Add(plan with
                {
                    Brand = "",
                    Options = plan.Options with { ServiceId = null },
                    CandidateBrands = candidateBrands,
                    InfoIncomplete = true
                });
            }
            else
            {
                var first = factories[0];
                ApplicationLog.Current?.Debug(
                    "Discovery",
                    $"设备连接候选：device={plan.Candidate.DisplayName}，优先品牌={first.Brand}，顺序={string.Join(',', candidateBrands)}，缓存服务数={plan.Candidate.ServiceIds.Count}。");
                resolved.Add(plan with
                {
                    Brand = first.Brand,
                    Options = plan.Options with { ServiceId = first.ServiceId },
                    CandidateBrands = candidateBrands,
                    InfoIncomplete = false
                });
            }
        }
        return resolved;
    }
    // 配对/连接进行中 Windows 无法读取设备名时会回落成 "耳机 XXXXXXXXXXXX"（MAC 地址），此时品牌识别不可靠。
    private static bool IsFallbackDeviceName(string displayName)
        => string.IsNullOrWhiteSpace(displayName)
            || displayName.StartsWith("耳机 ", StringComparison.Ordinal);
    // 按计划保留的稳定顺序取得可验证品牌，避免监控刷新时重排连接尝试。
    // ignoreProbeFailures=false（Discovery 确认循环）时跳过负结果缓存中未过期的品牌；
    // true（用户/自动连接）时总是全品牌重试。
    private IReadOnlyList<IBrandManagerFactory> GetCandidateFactories(DeviceConnectionPlan plan, bool ignoreProbeFailures)
    {
        var brands = plan.CandidateBrands is { Count: > 0 }
            ? plan.CandidateBrands
            : string.IsNullOrWhiteSpace(plan.Brand) ? [] : [plan.Brand];
        HashSet<string>? failedBrands = null;
        if (!ignoreProbeFailures)
        {
            lock (_availableDevicesLock)
                failedBrands = GetActiveProbeFailures(plan.Candidate.StableId);
        }
        // BLE-only 品牌（ServiceId == Guid.Empty，如 Apple）不参与 RFCOMM 探测/连接：它们走独立的
        // ble-adv 传输，RFCOMM 尝试只会拿 Guid.Empty 空 UUID 白白失败（WSA=10049）并多抛一次异常。
        var isBleAdvertisement = string.Equals(plan.Options.Transport, TransportNames.BleAdvertisement, StringComparison.OrdinalIgnoreCase);
        return brands
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(brand => failedBrands is null || !failedBrands.Contains(brand))
            .Select(brand => _managerFactories.TryGetValue(brand, out var factory) ? factory : null)
            .Where(factory => factory is not null && (isBleAdvertisement || factory.ServiceId != Guid.Empty))
            .Cast<IBrandManagerFactory>()
            .ToArray();
    }

    // 读取某设备当前仍在 TTL 内的品牌探测负结果（调用方需持有 _availableDevicesLock）。
    private HashSet<string> GetActiveProbeFailures(string stableId)
    {
        if (!_brandProbeFailures.TryGetValue(stableId, out var failures))
            return [];
        var now = Environment.TickCount64;
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (brand, stamp) in failures)
        {
            if (now - stamp < BrandProbeFailureTtl.TotalMilliseconds)
                active.Add(brand);
        }
        return active;
    }

    private void RecordProbeFailure(string stableId, string brand)
    {
        lock (_availableDevicesLock)
        {
            if (!_brandProbeFailures.TryGetValue(stableId, out var failures))
                _brandProbeFailures[stableId] = failures = new(StringComparer.OrdinalIgnoreCase);
            failures[brand] = Environment.TickCount64;
        }
    }

    private void ClearProbeFailures(string stableId)
    {
        lock (_availableDevicesLock)
            _brandProbeFailures.Remove(stableId);
    }
    // 将已验证的工厂写回计划，后续用户连接会从成功的协议开始。
    private void MarkConfirmed(DeviceConnectionPlan plan)
    {
        // 在锁外记录 MAC → 品牌缓存，避免文件持久化阻塞设备字典锁。
        var cacheMac = plan.Candidate.BluetoothAddress;
        var cacheBrand = plan.Brand;
        lock (_availableDevicesLock)
        {
            var devices = new Dictionary<string, DeviceConnectionPlan>(_availableDevices, StringComparer.Ordinal);
            devices[plan.Candidate.StableId] = plan;
            _availableDevices = devices;
            _confirmedDeviceIds.Add(plan.Candidate.StableId);
            // 协议验证成功：清除该设备的品牌探测负结果缓存。
            _brandProbeFailures.Remove(plan.Candidate.StableId);
        }
        if (!string.IsNullOrWhiteSpace(cacheMac) && !string.IsNullOrWhiteSpace(cacheBrand))
            _settings?.RecordBrandForMac(cacheMac, cacheBrand);
    }
    private IReadOnlyList<DeviceConnectionOption> GetAvailableDeviceOptions()
    {
        lock (_availableDevicesLock)
        {
            return _availableDevices.Values
                .Where(plan => _confirmedDeviceIds.Contains(plan.Candidate.StableId))
                .Select(plan => new DeviceConnectionOption(plan.Candidate.StableId, plan.Candidate.DisplayName))
                .ToArray();
        }
    }
    private async Task ConfirmDeviceAsync(DeviceConnectionPlan plan, CancellationToken cancellationToken)
    {
        lock (_availableDevicesLock)
        {
            if (_confirmedDeviceIds.Contains(plan.Candidate.StableId)
                || !_probingDeviceIds.Add(plan.Candidate.StableId))
                return;
        }
        try
        {
            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                lock (_availableDevicesLock)
                {
                    if (!_availableDevices.ContainsKey(plan.Candidate.StableId))
                        return;
                }
                var scanner = _deviceScanner ?? throw new InvalidOperationException("Device scanning is unavailable.");
                var budgetDeadline = Environment.TickCount64 + ProbeBudgetMs;
                foreach (var factory in GetCandidateFactories(plan, ignoreProbeFailures: false))
                {
                    var remaining = budgetDeadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        ApplicationLog.Current?.Info("Discovery", $"品牌探测预算耗尽（{ProbeBudgetMs / 1000}s），本轮确认终止：device={plan.Candidate.DisplayName}。");
                        return;
                    }
                    var candidatePlan = plan with
                    {
                        Brand = factory.Brand,
                        // 裸通道兜底按“名称是否命中本品牌”放行：名称强匹配的设备（如 OPPO 盒内休眠、
                        // SDP 不响应但控制通道仍在 channel 1）允许退化到裸通道 1/15 直连自愈；
                        // 跨品牌探测（vivo 被 OPPO 探测）不碰裸通道，端口 0 一超时即快速失败，避免
                        // Vivo 4s + Edifier 8s + OPPO 11s ≈ 25s 的死通道白等。误锁由各品牌握手验证
                        // （LastResponseTicks==0）兜底。
                        Options = plan.Options with
                        {
                            ServiceId = factory.ServiceId,
                            AllowBareChannels = factory.IsCandidateName(plan.Candidate.DisplayName),
                            Channel = factory.GetPreferredChannel(plan.Candidate.DisplayName)
                        }
                    };
                    IBrandManager? manager = null;
                    IRawConnection? connection = null;
                    try
                    {
                        ApplicationLog.Current?.Debug("Discovery", $"确认设备协议：device={plan.Candidate.DisplayName}，brand={factory.Brand}，service={factory.ServiceId}。");
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        attemptCts.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                        connection = await scanner.OpenAsync(candidatePlan, attemptCts.Token);
                        manager = await factory.CreateAsync(candidatePlan, connection, attemptCts.Token);
                        // 确认阶段已经完整初始化会话（身份/能力/通知/初始读取），若当前尚无活动会话，
                        // 直接将其提升为活动会话，省去随后自动连接对同一设备再开一条重复会话的代价。
                        // 依据真机日志：OPPO/vivo 在“设备被发现”后会先确认协议再自动连接，导致同设备
                        // 被完整连接两次（OPPO 因此多耗约 7 秒，vivo 则因确认会话被提前 Dispose 出现
                        // ObjectDisposedException）。复用后自动连接循环看到 ActiveManager 非空会直接返回。
                        if (_activeManager is null && _activeDeviceId is null)
                        {
                            _activeDeviceId = candidatePlan.Candidate.StableId;
                            MarkConfirmed(candidatePlan);
                            await SelectManagerAsync(manager);
                            ApplicationLog.Current?.Info("Discovery", $"已确认并直接接入设备会话：device={plan.Candidate.DisplayName}，brand={factory.Brand}。");
                            return;
                        }
                        await manager.DisposeAsync();
                        manager = null;
                        MarkConfirmed(candidatePlan);
                        ApplicationLog.Current?.Info("Discovery", $"已确认设备协议：device={plan.Candidate.DisplayName}，brand={factory.Brand}。");
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // 探测预算超时（内部 token 触发而外部未取消）：终止本轮，不记品牌负结果。
                        ApplicationLog.Current?.Info("Discovery", $"品牌探测预算耗尽：device={plan.Candidate.DisplayName}，brand={factory.Brand}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                        return;
                    }
                    catch (BluetoothConnectException exception)
                    {
                        // 设备暂不可达属瞬态问题（未真正连上 Windows/在范围外）：不记品牌负结果，
                        // 避免设备恢复后被缓存跳过；本轮继续尝试下一品牌。
                        ApplicationLog.Current?.Debug("Discovery", $"设备协议确认失败（设备暂不可达）：device={plan.Candidate.DisplayName}，brand={factory.Brand}，reason={exception.Message}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        ApplicationLog.Current?.Debug("Discovery", $"设备协议确认失败：device={plan.Candidate.DisplayName}，brand={factory.Brand}，reason={exception.Message}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                        // 握手验证失败/超时等：记入负结果缓存，TTL 内 Discovery 不再重复探测该品牌。
                        RecordProbeFailure(plan.Candidate.StableId, factory.Brand);
                    }
                }
            }
            finally
            {
                _connectionGate.Release();
            }
        }
        finally
        {
            lock (_availableDevicesLock)
                _probingDeviceIds.Remove(plan.Candidate.StableId);
        }
    }
    // 应用手动型号覆盖并通过统一状态通道发布重新解析后的能力。
    public bool SetManualModel(string? modelName)
    {
        var manager = _activeManager;
        if (manager is null)
        {
            ApplicationLog.Current?.Debug("Control", "手动型号应用跳过：当前没有活动设备会话。");
            return false;
        }
        manager.SetManualModel(modelName);
        ApplicationLog.Current?.Info("Control", $"已应用手动型号：{modelName ?? "自动识别"}。");
        return true;
    }
    // 扫描可通信的耳机，并仅向界面返回显示和选择所需的信息。
    public async Task<IReadOnlyList<DeviceConnectionOption>> RefreshAvailableDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var scanner = _deviceScanner ?? throw new InvalidOperationException("Device scanning is unavailable.");
            var plans = await scanner.ScanAsync(cancellationToken);
            await ApplyPlansAsync(plans, cancellationToken);
            return GetAvailableDeviceOptions();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Info("Discovery", "设备扫描已取消。");
            throw;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Discovery", "扫描可用设备失败。", exception);
            return [];
        }
    }
    // 按界面选择的稳定标识建立对应耳机的会话。
    public async Task<bool> ConnectAsync(string deviceId, CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Control", $"请求连接设备：id={deviceId}。");
        lock (_availableDevicesLock)
            _manualDisconnectDeviceId = null;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = Interlocked.Exchange(ref _selectionCancellation, request);
        try { previous?.Cancel(); } catch { }
        previous?.Dispose();
        try
        {
            var requestToken = request.Token;
            DeviceConnectionPlan? plan;
            lock (_availableDevicesLock)
                _availableDevices.TryGetValue(deviceId, out plan);
            if (plan is null)
            {
                await RefreshAvailableDevicesAsync(requestToken);
                lock (_availableDevicesLock)
                    _availableDevices.TryGetValue(deviceId, out plan);
            }
            if (plan is null)
            {
                ApplicationLog.Current?.Error("Control", $"连接设备失败：找不到扫描计划。id={deviceId}");
                return false;
            }
            return await ConnectPlanAsync(plan, requestToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Info("Control", $"连接设备已取消：id={deviceId}。");
            throw;
        }
        catch (OperationCanceledException)
        {
            ApplicationLog.Current?.Debug("Control", $"连接请求被更新的设备选择替换：id={deviceId}。");
            return false;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Control", $"连接设备失败：id={deviceId}。", exception);
            return false;
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _selectionCancellation), request))
                Interlocked.CompareExchange(ref _selectionCancellation, null, request);
        }
    }
    // 启动阶段连接第一个可通信耳机，失败时由调用方决定是否提示用户。
    public async Task<bool> ConnectFirstAvailableAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _autoConnectInProgress, 1) != 0)
            return false;
        try
        {
            ApplicationLog.Current?.Info("Control", "请求自动连接首个可用设备。");
            if (ActiveManager is not null)
                return true;
            var devices = await RefreshAvailableDevicesAsync(cancellationToken);
            DeviceConnectionPlan? plan;
            lock (_availableDevicesLock)
                plan = _availableDevices.Values.FirstOrDefault(candidate =>
                    !candidate.InfoIncomplete
                    && _confirmedDeviceIds.Contains(candidate.Candidate.StableId)
                    && _managerFactories.ContainsKey(candidate.Brand));
            if (plan is null || ActiveManager is not null)
            {
                ApplicationLog.Current?.Info("Control", $"自动连接结束：可用设备={devices.Count}，已支持设备={plan is not null}，已有会话={ActiveManager is not null}。");
                if (plan is null)
                    await ClearActiveManagerAsync();
                return false;
            }
            return await ConnectAsync(plan.Candidate.StableId, cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _autoConnectInProgress, 0);
        }
    }
    // 原子切换当前耳机会话，先释放旧会话再订阅新会话。
    public async Task SelectManagerAsync(IBrandManager manager)
    {
        if (ReferenceEquals(_activeManager, manager))
            return;
        if (_activeManager is not null)
        {
            _activeManager.StateChanged -= OnStateChanged;
            await _activeManager.DisconnectAsync();
            await _activeManager.DisposeAsync();
        }
        _activeManager = manager;
        ApplicationLog.Current?.Info("Control", $"已切换到 {manager.Capability.ModelName} 会话。");
        manager.StateChanged += OnStateChanged;
        manager.SetInteractivePolling(_frontendState.HasInteractiveSurface);
        _frontendState.Publish(manager.Snapshot);
    }
    public async Task DisconnectAsync()
    {
        ApplicationLog.Current?.Info("Control", "请求断开当前设备会话。");
        if (_activeManager is null)
        {
            ApplicationLog.Current?.Debug("Control", "断开跳过：当前没有活动会话。");
            return;
        }
        try { _selectionCancellation?.Cancel(); } catch { }
        lock (_availableDevicesLock)
            _manualDisconnectDeviceId = _activeDeviceId;
        await ClearActiveManagerAsync();
    }
    // 根据当前会话和本地隐藏策略生成多设备展示数据，窗口不再直接读取设置。
    public MultiDeviceDisplayState GetMultiDeviceDisplayState()
    {
        var manager = _activeManager;
        if (manager is null)
            return new MultiDeviceDisplayState([], []);
        var hiddenAddresses = _settings?.GetHiddenMultiDevices()
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return manager.GetMultiDeviceDisplayState(hiddenAddresses);
    }
    // 保存用户隐藏的非当前设备地址，并立即记录策略变化。
    public bool HideMultiDevice(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || _settings is null)
            return false;
        var current = _activeManager?.Snapshot.MultiDevice.Devices
            .FirstOrDefault(device => device.IsCurrent);
        if (string.Equals(current?.Address, address, StringComparison.OrdinalIgnoreCase))
            return false;
        var hidden = _settings.GetHiddenMultiDevices().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!hidden.Add(address))
            return false;
        _settings.SetHiddenMultiDevices(hidden);
        ApplicationLog.Current?.Info("MultiDevice", $"已隐藏多设备：address={address}。");
        return true;
    }
    // 清除所有本地隐藏设备策略，恢复完整多设备列表。
    public void RestoreHiddenMultiDevices()
    {
        if (_settings is null)
            return;
        _settings.SetHiddenMultiDevices([]);
        ApplicationLog.Current?.Info("MultiDevice", "已恢复全部隐藏多设备。");
    }
    // 返回隐藏设备数量，供窗口更新恢复按钮状态。
    public int GetHiddenMultiDeviceCount()
        => _settings?.GetHiddenMultiDevices().Count ?? 0;
    // 通过已确认的扫描计划打开 RFCOMM 链接；异常时按计划顺序验证下一个品牌协议。
    private async Task<bool> ConnectPlanAsync(DeviceConnectionPlan plan, CancellationToken cancellationToken)
    {
        // 用户显式连接（含自动连接）：不受负结果缓存限制，总是全品牌重试一遍。
        var factories = GetCandidateFactories(plan, ignoreProbeFailures: true);
        if (factories.Count == 0)
        {
            ApplicationLog.Current?.Info("Control", $"跳过没有可用品牌协议的设备：device={plan.Candidate.DisplayName}。");
            await ClearActiveManagerAsync();
            return false;
        }
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_activeManager is not null
                && string.Equals(_activeDeviceId, plan.Candidate.StableId, StringComparison.Ordinal))
                return true;
            var scanner = _deviceScanner ?? throw new InvalidOperationException("Device scanning is unavailable.");
            var budgetDeadline = Environment.TickCount64 + ProbeBudgetMs;
            foreach (var factory in factories)
            {
                var candidatePlan = plan with
                {
                    Brand = factory.Brand,
                    Options = plan.Options with
                    {
                        ServiceId = factory.ServiceId,
                        Channel = factory.GetPreferredChannel(plan.Candidate.DisplayName)
                    }
                };
                // 握手阶段若品牌层判定通道不可用（典型为落到不回 GAIA 帧的裸通道），先强制只用服务 UUID
                // 端口 0 重试一次，给“port 0 当时未就绪”的启动竞态一个自愈机会；仍失败则按原路径报错，
                // 由用户手动重连（手动重连时设备多半已就绪，port 0 可连）。
                var allowBare = true;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var remaining = budgetDeadline - Environment.TickCount64;
                    if (remaining <= 0)
                    {
                        ApplicationLog.Current?.Info("Control", $"品牌探测预算耗尽（{ProbeBudgetMs / 1000}s），本轮连接终止：device={plan.Candidate.DisplayName}。");
                        return false;
                    }
                    var attemptPlan = candidatePlan with
                    {
                        Options = candidatePlan.Options with { AllowBareChannels = allowBare }
                    };
                    IBrandManager? manager = null;
                    IRawConnection? connection = null;
                    try
                    {
                        ApplicationLog.Current?.Info("Control", $"正在打开 {plan.Candidate.DisplayName} 的 {factory.Brand} 会话：service={factory.ServiceId}，allowBare={allowBare}。");
                        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        attemptCts.CancelAfter(TimeSpan.FromMilliseconds(remaining));
                        connection = await scanner.OpenAsync(attemptPlan, attemptCts.Token);
                        manager = await factory.CreateAsync(attemptPlan, connection, attemptCts.Token);
                        await SelectManagerAsync(manager);
                        manager = null;
                        _activeDeviceId = plan.Candidate.StableId;
                        MarkConfirmed(candidatePlan);
                        return true;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        // 探测预算超时（内部 token 触发而外部未取消）：终止本轮，不记品牌负结果。
                        ApplicationLog.Current?.Info("Control", $"品牌探测预算耗尽：device={plan.Candidate.DisplayName}，brand={factory.Brand}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                        return false;
                    }
                    catch (ChannelUnusableException ex) when (allowBare)
                    {
                        // 死通道：放弃裸通道兜底，仅用端口 0 重试。
                        ApplicationLog.Current?.Info("Control",
                            $"通道不可用，强制端口 0 重试：device={plan.Candidate.DisplayName}，brand={factory.Brand}，reason={ex.Message}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                        allowBare = false;
                        await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                        continue;
                    }
                    catch (BluetoothConnectException ex)
                    {
                        // 设备暂不可达（典型为 WSA 10064 主机不可达）：属环境/瞬态问题，不是程序错误。
                        // 记录清晰提示，依赖设备重新连接后的自动重连（DeviceWatcher 重新上报时会再次自动连接）。
                        var hint = ex.WsaError is 10060 or 10061 or 10064 or 10065
                            ? $"耳机暂不可达（WSA {ex.WsaError} {BluetoothConnectException.DescribeWsa(ex.WsaError ?? 0)}），可能未真正连上 Windows 或在范围外；设备重新连接后将自动重试。"
                            : $"RFCOMM 连接失败（WSA {ex.WsaError}）。";
                        ApplicationLog.Current?.Info("Control",
                            $"{hint} device={plan.Candidate.DisplayName}，brand={factory.Brand}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                        break;
                    }
                    catch (Exception exception)
                    {
                        ApplicationLog.Current?.Debug("Control", $"连接尝试失败：device={plan.Candidate.DisplayName}，brand={factory.Brand}，reason={exception.Message}。");
                        if (manager is not null)
                            await manager.DisposeAsync();
                        else if (connection is not null)
                            await connection.DisposeAsync();
                        // 握手验证失败/超时等：记入负结果缓存，供 Discovery 确认循环在 TTL 内跳过该品牌。
                        RecordProbeFailure(plan.Candidate.StableId, factory.Brand);
                        break;
                    }
                }
            }
            ApplicationLog.Current?.Error("Control", $"连接设备失败：所有品牌协议均未通过验证。device={plan.Candidate.DisplayName}。");
            return false;
        }
        finally
        {
            _connectionGate.Release();
        }
    }
    private async Task ClearActiveManagerAsync(IBrandManager? expectedManager = null)
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (expectedManager is not null && !ReferenceEquals(_activeManager, expectedManager))
                return;
            var manager = _activeManager;
            if (manager is not null)
            {
                manager.StateChanged -= OnStateChanged;
                _activeManager = null;
                _activeDeviceId = null;
                try
                {
                    await manager.DisconnectAsync();
                }
                finally
                {
                    await manager.DisposeAsync();
                }
            }
            _frontendState.Clear();
        }
        finally
        {
            _connectionGate.Release();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _lifetimeCancellation.Cancel();
        try { _reconnectWake.Release(); } catch (SemaphoreFullException) { }
        if (_reconnectTask is not null)
        {
            try { await _reconnectTask; }
            catch (OperationCanceledException) { }
        }
        try { _selectionCancellation?.Cancel(); } catch { }
        _selectionCancellation?.Dispose();
        _selectionCancellation = null;
        if (_activeManager is not null)
        {
            _activeManager.StateChanged -= OnStateChanged;
            try { await _activeManager.DisconnectAsync(); }
            finally { await _activeManager.DisposeAsync(); }
            _activeManager = null;
            _activeDeviceId = null;
        }
        if (_deviceScanner is not null)
        {
            _deviceScanner.PlansChanged -= OnPlansChanged;
            _deviceScanner.Dispose();
        }
        _frontendState.Clear();
        _frontendState.InteractivePollingChanged -= OnInteractivePollingChanged;
        _lifetimeCancellation.Dispose();
        _reconnectWake.Dispose();
        _connectionGate.Dispose();
        _discoveryGate.Dispose();
    }
    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        ApplicationLog.Current?.Debug("Control", $"收到设备状态：revision={snapshot.Revision}，connected={snapshot.IsConnected}。");
        if (ReferenceEquals(sender, _activeManager))
        {
            _frontendState.Publish(snapshot);
            if (!snapshot.IsConnected)
            {
                ApplicationLog.Current?.Info("Control", "活动品牌后端报告连接已断开，开始清理当前会话。");
                _ = ClearActiveManagerAsync(sender as IBrandManager);
            }
        }
    }
    private void OnInteractivePollingChanged(object? sender, bool enabled)
    {
        ApplicationLog.Current?.Info("Polling", $"交互轮询开关变化：enabled={enabled}。");
        _activeManager?.SetInteractivePolling(enabled);
    }
}
// 向界面公开的设备选择项，不暴露通信参数和协议连接计划。
public sealed record DeviceConnectionOption(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}
public sealed class DeviceOptionsChangedEventArgs : EventArgs
{
    public DeviceOptionsChangedEventArgs(IReadOnlyList<DeviceConnectionOption> devices)
    {
        Devices = devices;
    }
    public IReadOnlyList<DeviceConnectionOption> Devices { get; }
}
