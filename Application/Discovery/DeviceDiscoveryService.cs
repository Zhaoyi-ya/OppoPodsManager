using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Application.Discovery;

/// <summary>
/// Application-facing device discovery use case. Platform enumeration stays
/// behind <see cref="IDeviceDiscovery"/>.
/// </summary>
public sealed class DeviceDiscoveryService
{
    private readonly IDeviceDiscovery _discovery;

    public DeviceDiscoveryService(IDeviceDiscovery discovery)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
    }

    public ValueTask<IReadOnlyList<RawDeviceCandidate>> DiscoverAsync(
        CancellationToken cancellationToken = default) =>
        _discovery.DiscoverAsync(cancellationToken);
}
