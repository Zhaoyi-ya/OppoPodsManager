using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Windows;

// 为 ble-adv 传输创建 BleAdvertisementConnection；控制层按传输名路由到此工厂。
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class BleAdvertisementConnectionFactory : IConnectionFactory
{
    private readonly BleAdvertisementHub _hub;

    public BleAdvertisementConnectionFactory(BleAdvertisementHub hub)
    {
        _hub = hub;
    }

    public string Transport => TransportNames.BleAdvertisement;

    public Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
        => Task.FromResult<IRawConnection>(new BleAdvertisementConnection(_hub, candidate.StableId));
}
