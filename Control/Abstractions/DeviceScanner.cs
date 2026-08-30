using OppoPodsManager.Communication;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control.Abstractions;

// 通用设备扫描器只负责把通信层候选转换成连接计划，不知道任何品牌协议或后端 Manager。
public sealed class DeviceScanner : IDisposable
{
    private readonly CommunicationController _communication;
    private bool _disposed;

    public DeviceScanner(CommunicationController communication)
    {
        _communication = communication;
        _communication.DevicesChanged += OnDevicesChanged;
    }

    public event EventHandler<DevicePlansChangedEventArgs>? PlansChanged;

    public async Task<IReadOnlyList<DeviceConnectionPlan>> ScanAsync(CancellationToken cancellationToken)
    {
        var candidates = await _communication.DiscoverAsync(cancellationToken);
        return CreatePlans(candidates);
    }

    public void StartMonitoring()
        => _communication.StartDiscoveryMonitoring();

    public Task<IRawConnection> OpenAsync(
        DeviceConnectionPlan plan,
        CancellationToken cancellationToken)
        => _communication.OpenAsync(plan.Candidate, plan.Options, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _communication.DevicesChanged -= OnDevicesChanged;
        _communication.Dispose();
    }

    private void OnDevicesChanged(object? sender, DeviceCandidatesChangedEventArgs args)
    {
        try
        {
            var plans = CreatePlans(args.Devices);
            PlansChanged?.Invoke(this, new DevicePlansChangedEventArgs(plans, args.Generation));
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Error("Discovery", "设备变化转换失败。", exception);
        }
    }

    private static IReadOnlyList<DeviceConnectionPlan> CreatePlans(IEnumerable<DeviceCandidate> candidates)
    {
        // 控制层数据模型为「一设备一个 plan」：_availableDevices / ApplyPlansCoreAsync 的
        // ToDictionary 都以 Candidate.StableId 为唯一键。因此每个候选只生成一个 plan，
        // 传输层按优先级取主链路（rfcomm 优先，gatt 兜底）。若设备只暴露 GATT（无 rfcomm
        // 候选），则主传输回退为 gatt，保证 GATT 链路仍可用。
        // 注：RFCOMM→GATT 的「自动回退触发」后续需在连接层（ConnectPlanAsync）实现，
        // 此处先保证不重复键崩溃且主链路可用，不破坏既有的裸通道回退/瞬态不可达判断。
        var plans = new List<DeviceConnectionPlan>();
        foreach (var candidate in candidates)
        {
            var ordered = candidate.Transports
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .OrderBy(t => t.Equals("rfcomm", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToArray();
            if (ordered.Length == 0)
                continue;

            plans.Add(new DeviceConnectionPlan(
                candidate,
                new ConnectionOptions(ordered[0], null, 0)));
        }

        return plans.ToArray();
    }
}

// 控制层保存名称推断后的品牌尝试顺序，并在验证成功后写入最终 Brand 和 ServiceId。
public sealed record DeviceConnectionPlan(
    DeviceCandidate Candidate,
    ConnectionOptions Options,
    string Brand = "",
    IReadOnlyList<string>? CandidateBrands = null,
    // 设备信息是否不完整（配对/连接进行中 Windows 暴露的名称回落成“耳机 XXXX”或缓存服务 UUID 缺失）。
    // 信息不完整时控制层不武断锁定品牌，而是把所有候选品牌交给连接层逐个尝试，并跳过自动连接等待配对稳定。
    bool InfoIncomplete = false);

public sealed class DevicePlansChangedEventArgs : EventArgs
{
    public DevicePlansChangedEventArgs(IReadOnlyList<DeviceConnectionPlan> plans, long generation = 0)
    {
        Plans = plans;
        Generation = generation;
    }

    public IReadOnlyList<DeviceConnectionPlan> Plans { get; }
    public long Generation { get; }
}
