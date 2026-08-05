using OppoPodsManager.Communication;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control;

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
            global::OppoPodsManager.Control.Logging.ApplicationLog.Current?.Error("Discovery", "设备变化转换失败。", exception);
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

        var serviceId = candidate.ServiceIds.FirstOrDefault();
        return new DeviceConnectionPlan(
            candidate,
            new ConnectionOptions(transport, serviceId == Guid.Empty ? null : serviceId, 0));
    }
}

public sealed record DeviceConnectionPlan(DeviceCandidate Candidate, ConnectionOptions Options);

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
