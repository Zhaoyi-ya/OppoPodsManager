using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Platforms.Common;

/// <summary>
/// Small composition helper used by Windows/Linux backends while their OS API
/// implementations are migrated. It only selects transport profiles and opens
/// raw byte connections supplied by the platform layer.
/// </summary>
public sealed class ConfiguredConnectionFactory : IPlatformConnectionFactory
{
    private readonly Func<RawDeviceCandidate, IReadOnlyList<ConnectionProfile>> _profiles;
    private readonly Func<RawDeviceCandidate, ConnectionProfile, CancellationToken, ValueTask<IRawConnection>> _open;

    public ConfiguredConnectionFactory(
        Func<RawDeviceCandidate, IReadOnlyList<ConnectionProfile>> profiles,
        Func<RawDeviceCandidate, ConnectionProfile, CancellationToken, ValueTask<IRawConnection>> open)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _open = open ?? throw new ArgumentNullException(nameof(open));
    }

    public IReadOnlyList<ConnectionProfile> GetProfiles(RawDeviceCandidate device) =>
        _profiles(device);

    public ValueTask<IRawConnection> OpenAsync(
        RawDeviceCandidate device,
        ConnectionProfile profile,
        CancellationToken cancellationToken) =>
        _open(device, profile, cancellationToken);
}
