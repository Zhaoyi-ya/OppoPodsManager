using OppoPodsManager.Control.Oppo;
using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Assets.UserSettings;

namespace OppoPodsManager.Control;

// 协调当前品牌管理器，并把设备状态桥接到前端状态容器。
public sealed class ControlManager : IAsyncDisposable
{
    private readonly FrontendState _frontendState;
    private readonly DeviceScanner? _deviceScanner;
    private readonly ModelCatalog? _modelCatalog;
    private readonly SettingsStore? _settings;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _availableDevicesLock = new();
    private IReadOnlyDictionary<string, DeviceConnectionPlan> _availableDevices = new Dictionary<string, DeviceConnectionPlan>();
    private IBrandManager? _activeManager;

    public ControlManager(
        FrontendState frontendState,
        DeviceScanner? deviceScanner = null,
        ModelCatalog? modelCatalog = null,
        SettingsStore? settings = null)
    {
        _frontendState = frontendState;
        _deviceScanner = deviceScanner;
        _modelCatalog = modelCatalog;
        _settings = settings;
        _frontendState.InteractivePollingChanged += OnInteractivePollingChanged;
    }

    public IBrandManager? ActiveManager => _activeManager;

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
            lock (_availableDevicesLock)
            {
                _availableDevices = plans.ToDictionary(plan => plan.Candidate.StableId, StringComparer.Ordinal);
            }

            ApplicationLog.Current?.Debug("Discovery", $"发现 {plans.Count} 个已连接候选设备。");
            return plans.Select(plan => new DeviceConnectionOption(plan.Candidate.StableId, plan.Candidate.DisplayName)).ToArray();
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
        try
        {
            DeviceConnectionPlan? plan;
            lock (_availableDevicesLock)
                _availableDevices.TryGetValue(deviceId, out plan);

            if (plan is null)
            {
                await RefreshAvailableDevicesAsync(cancellationToken);
                lock (_availableDevicesLock)
                    _availableDevices.TryGetValue(deviceId, out plan);
            }

            if (plan is null)
            {
                ApplicationLog.Current?.Error("Control", $"连接设备失败：找不到扫描计划。id={deviceId}");
                return false;
            }

            await ConnectPlanAsync(plan, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Info("Control", $"连接设备已取消：id={deviceId}。");
            throw;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Control", $"连接设备失败：id={deviceId}。", exception);
            return false;
        }
    }

    // 启动阶段连接第一个可通信耳机，失败时由调用方决定是否提示用户。
    public async Task<bool> ConnectFirstAvailableAsync(CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Control", "请求自动连接首个可用设备。");
        if (ActiveManager is not null)
            return true;

        var devices = await RefreshAvailableDevicesAsync(cancellationToken);
        var device = devices.FirstOrDefault();
        if (device is null || ActiveManager is not null)
        {
            ApplicationLog.Current?.Info("Control", $"自动连接结束：可用设备={devices.Count}，已有会话={ActiveManager is not null}。");
            return false;
        }

        return await ConnectAsync(device.Id, cancellationToken);
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

        await _activeManager.DisconnectAsync();
        ApplicationLog.Current?.Info("Control", "当前耳机会话已断开。");
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
    private async Task ConnectPlanAsync(DeviceConnectionPlan plan, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var scanner = _deviceScanner ?? throw new InvalidOperationException("Device scanning is unavailable.");
            var manager = new OppoManager(_modelCatalog);
            ConnectionLink? link = null;
            try
            {
                ApplicationLog.Current?.Info("Control", $"正在打开 {plan.Candidate.DisplayName} 的 RFCOMM 会话。");
                link = await scanner.OpenAsync(plan, cancellationToken);
                await manager.StartSessionAsync(plan.Candidate.DisplayName, link, cancellationToken);
                await SelectManagerAsync(manager);
            }
            catch
            {
                ApplicationLog.Current?.Error("Control", $"连接 {plan.Candidate.DisplayName} 失败。");
                if (link is not null)
                    await link.DisposeAsync();

                await manager.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_activeManager is not null)
        {
            _activeManager.StateChanged -= OnStateChanged;
            await _activeManager.DisposeAsync();
            _activeManager = null;
        }

        _frontendState.InteractivePollingChanged -= OnInteractivePollingChanged;
        _connectionGate.Dispose();
    }

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        ApplicationLog.Current?.Debug("Control", $"收到设备状态：revision={snapshot.Revision}，connected={snapshot.IsConnected}。");
        if (ReferenceEquals(sender, _activeManager))
            _frontendState.Publish(snapshot);
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
