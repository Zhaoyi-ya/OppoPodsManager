using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Core.Communication;

public interface IDeviceDiscovery
{
    ValueTask<IReadOnlyList<RawDeviceCandidate>> DiscoverAsync(
        CancellationToken cancellationToken);
}
