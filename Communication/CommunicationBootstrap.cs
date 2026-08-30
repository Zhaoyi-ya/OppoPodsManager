using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication;

// 应用层只请求平台通信组合，不直接引用 Windows/Linux 类型。
public static class CommunicationBootstrap
{
    public static CommunicationController CreateDefault()
    {
#if WINDOWS
        var bleHub = new Windows.BleAdvertisementHub();
        return new CommunicationController(
            [new Windows.WindowsRfcommConnectionFactory(), new Windows.BleAdvertisementConnectionFactory(bleHub)],
            [new Windows.WindowsBluetoothDiscovery(), new Windows.WindowsBleAdvertisementDiscovery(bleHub)]);
#elif LINUX
        return new CommunicationController(
            [new Linux.LinuxConnectionFactory(), new Linux.LinuxGattConnectionFactory()],
            [new Linux.LinuxBluetoothDiscovery()]);
#else
        return new CommunicationController([], []);
#endif
    }
}
