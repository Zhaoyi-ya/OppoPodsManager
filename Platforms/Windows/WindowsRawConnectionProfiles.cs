using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Platforms.Windows;

public static class WindowsRawConnectionProfiles
{
    public static IReadOnlyList<ConnectionProfile> ForCandidate(RawDeviceCandidate device)
    {
        var profiles = new List<ConnectionProfile>();
        if (device.AvailableTransports.Contains(DeviceTransport.Rfcomm)
            || device.AvailableTransports.Contains(DeviceTransport.BluetoothClassic))
        {
            profiles.Add(new ConnectionProfile(DeviceTransport.Rfcomm, "Windows RFCOMM", Priority: 0));
            profiles.Add(new ConnectionProfile(DeviceTransport.BluetoothClassic, "Windows SPP", Priority: 1));
        }

        if (device.AvailableTransports.Contains(DeviceTransport.Gatt))
            profiles.Add(new ConnectionProfile(DeviceTransport.Gatt, "Windows GATT", Priority: 2));

        if (profiles.Count == 0)
        {
            profiles.Add(new ConnectionProfile(DeviceTransport.Rfcomm, "Windows RFCOMM", Priority: 0));
            profiles.Add(new ConnectionProfile(DeviceTransport.Gatt, "Windows GATT", Priority: 2));
        }

        return profiles;
    }
}
