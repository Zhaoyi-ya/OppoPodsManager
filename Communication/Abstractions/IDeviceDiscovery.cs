namespace OppoPodsManager.Communication.Abstractions;

public interface IDeviceDiscovery
{
    Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken);
}
