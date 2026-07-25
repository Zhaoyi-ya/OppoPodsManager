using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Platforms.Common;

/// <summary>
/// Discovery adapter that lets the composition root supply OS-specific enumeration
/// without leaking brand/protocol concerns into the platform layer.
/// </summary>
public sealed class FuncDeviceDiscovery : IDeviceDiscovery
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyList<RawDeviceCandidate>>> _discover;

    public FuncDeviceDiscovery(
        Func<CancellationToken, ValueTask<IReadOnlyList<RawDeviceCandidate>>> discover)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
    }

    public ValueTask<IReadOnlyList<RawDeviceCandidate>> DiscoverAsync(
        CancellationToken cancellationToken) =>
        _discover(cancellationToken);
}
