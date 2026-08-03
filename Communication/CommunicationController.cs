using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication;

public sealed class CommunicationController
{
    private readonly IReadOnlyDictionary<string, IConnectionFactory> _factories;
    private readonly IReadOnlyList<IDeviceDiscovery> _discoveries;

    public CommunicationController(
        IEnumerable<IConnectionFactory> factories,
        IEnumerable<IDeviceDiscovery> discoveries)
    {
        _factories = factories.ToDictionary(factory => factory.Transport, StringComparer.OrdinalIgnoreCase);
        _discoveries = discoveries.ToArray();
    }

    public async Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discoveryTasks = _discoveries.Select(discovery => discovery.DiscoverAsync(cancellationToken));
        var results = await Task.WhenAll(discoveryTasks);
        return results
            .SelectMany(result => result)
            .GroupBy(candidate => candidate.StableId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
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
}
