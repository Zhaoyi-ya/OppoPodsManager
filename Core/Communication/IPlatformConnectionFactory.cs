using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Core.Communication;

public interface IPlatformConnectionFactory
{
    IReadOnlyList<ConnectionProfile> GetProfiles(RawDeviceCandidate device);

    ValueTask<IRawConnection> OpenAsync(
        RawDeviceCandidate device,
        ConnectionProfile profile,
        CancellationToken cancellationToken);
}
