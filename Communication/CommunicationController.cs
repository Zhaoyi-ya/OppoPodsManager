using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication;

// 设备发现与连接工厂的统一入口；控制层只依赖这个边界，不接触平台实现。
public sealed class CommunicationController : IDisposable
{
    private readonly IReadOnlyDictionary<string, IConnectionFactory> _factories;
    private readonly IReadOnlyList<IDeviceDiscovery> _discoveries;
    private readonly IReadOnlyList<IDeviceDiscoveryMonitor> _monitors;
    private readonly object _gate = new();
    private long _discoveryGeneration;
    private bool _disposed;

    public CommunicationController(
        IEnumerable<IConnectionFactory> factories,
        IEnumerable<IDeviceDiscovery> discoveries)
    {
        _factories = factories.ToDictionary(factory => factory.Transport, StringComparer.OrdinalIgnoreCase);
        _discoveries = discoveries.ToArray();
        _monitors = _discoveries.OfType<IDeviceDiscoveryMonitor>().ToArray();
        foreach (var monitor in _monitors)
            monitor.DevicesChanged += OnDevicesChanged;
    }

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public async Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discoveryTasks = _discoveries.Select(discovery => discovery.DiscoverAsync(cancellationToken));
        var results = await Task.WhenAll(discoveryTasks);
        return results
            .SelectMany(result => result)
            .GroupBy(candidate => candidate.StableId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void StartDiscoveryMonitoring()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        foreach (var monitor in _monitors)
            monitor.Start();
    }

    public Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        if (!_factories.TryGetValue(options.Transport, out var factory))
            throw new NotSupportedException($"No connection factory is registered for '{options.Transport}'.");

        return factory.OpenAsync(candidate, options, cancellationToken);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        foreach (var monitor in _monitors)
        {
            monitor.DevicesChanged -= OnDevicesChanged;
            monitor.Dispose();
        }
    }

    private async void OnDevicesChanged(object? sender, DeviceCandidatesChangedEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        try
        {
            var snapshot = await DiscoverAsync(CancellationToken.None);
            var generation = Interlocked.Increment(ref _discoveryGeneration);
            DevicesChanged?.Invoke(this, new DeviceCandidatesChangedEventArgs(snapshot, generation));
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Error("Discovery", "聚合设备变化失败。", exception);
        }
    }
}
