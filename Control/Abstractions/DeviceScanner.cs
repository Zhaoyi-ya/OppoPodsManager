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
        => candidates
            .Select(CreatePlan)
            .Where(plan => plan is not null)
            .Cast<DeviceConnectionPlan>()
            .ToArray();

    private static DeviceConnectionPlan? CreatePlan(DeviceCandidate candidate)
    {
        var transport = candidate.Transports.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(transport))
            return null;

        return new DeviceConnectionPlan(
            candidate,
            new ConnectionOptions(transport, null, 0));
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
