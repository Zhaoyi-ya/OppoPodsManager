using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Oppo;
using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Assets.UserSettings;

namespace OppoPodsManager.Control;

// 协调当前品牌管理器，并把设备状态桥接到前端状态容器。
public sealed class ControlManager : IAsyncDisposable
{
    private readonly FrontendState _frontendState;
    private readonly DeviceScanner? _deviceScanner;
    private readonly IReadOnlyDictionary<string, IBrandManagerFactory> _managerFactories;
    private readonly SettingsStore? _settings;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly object _availableDevicesLock = new();
    private IReadOnlyDictionary<string, DeviceConnectionPlan> _availableDevices = new Dictionary<string, DeviceConnectionPlan>();
    private readonly HashSet<string> _confirmedDeviceIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _probingDeviceIds = new(StringComparer.Ordinal);
    private IBrandManager? _activeManager;
    private string? _activeDeviceId;
    private string? _manualDisconnectDeviceId;
    private int _autoConnectInProgress;
    private long _lastDiscoveryGeneration;
    private CancellationTokenSource? _selectionCancellation;

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
    }
    public event EventHandler<DeviceOptionsChangedEventArgs>? AvailableDevicesChanged;

    private async void OnPlansChanged(object? sender, DevicePlansChangedEventArgs args)
    {
        try
        {
            await ApplyPlansAsync(args.Plans, autoConnect: true, cancellationToken: CancellationToken.None, generation: args.Generation);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Discovery", "处理蓝牙设备变化失败。", exception);
        }
    }

    private async Task ApplyPlansAsync(
        IReadOnlyList<DeviceConnectionPlan> plans,
        bool autoConnect,
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
            await ApplyPlansCoreAsync(plans, autoConnect, cancellationToken);
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task ApplyPlansCoreAsync(
        IReadOnlyList<DeviceConnectionPlan> plans,
        bool autoConnect,
        CancellationToken cancellationToken)
    {
        var next = plans.ToDictionary(plan => plan.Candidate.StableId, StringComparer.Ordinal);
        string? removedActiveId;
        lock (_availableDevicesLock)
        {
            removedActiveId = _activeDeviceId is not null && !next.ContainsKey(_activeDeviceId)
                ? _activeDeviceId
                : null;
            _availableDevices = next;
            _confirmedDeviceIds.RemoveWhere(id => !next.ContainsKey(id));
            if (_manualDisconnectDeviceId is not null && !next.ContainsKey(_manualDisconnectDeviceId))
                _manualDisconnectDeviceId = null;
        }

        var candidatesToConfirm = plans
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
                    _confirmedDeviceIds.Contains(plan.Candidate.StableId)
                    && _managerFactories.ContainsKey(plan.Candidate.Brand));
            if (replacement is not null)
                await ConnectAsync(replacement.Candidate.StableId, CancellationToken.None);
            return;
        }

        if (autoConnect && _activeManager is null && _manualDisconnectDeviceId is null)
        {
            DeviceConnectionPlan? autoPlan;
            lock (_availableDevicesLock)
                autoPlan = _availableDevices.Values.FirstOrDefault(plan =>
                    _confirmedDeviceIds.Contains(plan.Candidate.StableId)
                    && _managerFactories.ContainsKey(plan.Candidate.Brand));
            if (autoPlan is not null)
                await ConnectAsync(autoPlan.Candidate.StableId, cancellationToken);
        }
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
            if (!_managerFactories.ContainsKey(plan.Candidate.Brand))
                return;

            await _connectionGate.WaitAsync(cancellationToken);
            try
            {
                lock (_availableDevicesLock)
                {
                    if (!_availableDevices.ContainsKey(plan.Candidate.StableId))
                        return;
                }

                var scanner = _deviceScanner ?? throw new InvalidOperationException("Device scanning is unavailable.");
                IBrandManager? manager = null;
                IRawConnection? connection = null;
                try
                {
                    ApplicationLog.Current?.Debug("Discovery", $"确认设备型号：device={plan.Candidate.DisplayName}，brand={plan.Candidate.Brand}。");
                    connection = await scanner.OpenAsync(plan, cancellationToken);
                    var factory = _managerFactories[plan.Candidate.Brand];
                    manager = await factory.CreateAsync(plan, connection, cancellationToken);
                    await manager.DisposeAsync();
                    manager = null;
                    lock (_availableDevicesLock)
                        _confirmedDeviceIds.Add(plan.Candidate.StableId);
                }
                catch (Exception exception)
                {
                    ApplicationLog.Current?.Debug("Discovery", $"设备型号确认失败：device={plan.Candidate.DisplayName}，reason={exception.Message}。");
                    if (manager is not null)
                        await manager.DisposeAsync();
                    else if (connection is not null)
                        await connection.DisposeAsync();
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
            await ApplyPlansAsync(plans, autoConnect: true, cancellationToken);
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
                    _confirmedDeviceIds.Contains(candidate.Candidate.StableId)
                    && _managerFactories.ContainsKey(candidate.Candidate.Brand));

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

    // 通过已确认的扫描计划打开 RFCOMM 链接并启动 OPPO 协议会话。
    private async Task<bool> ConnectPlanAsync(DeviceConnectionPlan plan, CancellationToken cancellationToken)
    {
        if (!_managerFactories.TryGetValue(plan.Candidate.Brand, out var factory))
        {
            ApplicationLog.Current?.Info("Control", $"跳过未支持品牌：brand={plan.Candidate.Brand}，device={plan.Candidate.DisplayName}。");
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
            IBrandManager? manager = null;
            IRawConnection? connection = null;
            try
            {
                ApplicationLog.Current?.Info("Control", $"正在打开 {plan.Candidate.DisplayName} 的 {plan.Candidate.Brand} 会话。");
                connection = await scanner.OpenAsync(plan, cancellationToken);
                manager = await factory.CreateAsync(plan, connection, cancellationToken);
                _activeDeviceId = plan.Candidate.StableId;
                await SelectManagerAsync(manager);
                return true;
            }
            catch
            {
                ApplicationLog.Current?.Error("Control", $"连接 {plan.Candidate.DisplayName} 失败。");
                if (manager is not null)
                    await manager.DisposeAsync();
                else if (connection is not null)
                    await connection.DisposeAsync();
                if (string.Equals(_activeDeviceId, plan.Candidate.StableId, StringComparison.Ordinal)
                    && !ReferenceEquals(_activeManager, manager))
                    _activeDeviceId = null;
                throw;
            }
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
