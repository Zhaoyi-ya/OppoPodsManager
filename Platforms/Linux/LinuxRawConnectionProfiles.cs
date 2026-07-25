using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Platforms.Linux;

public static class LinuxRawConnectionProfiles
{
    public static IReadOnlyList<ConnectionProfile> ForCandidate(RawDeviceCandidate device)
    {
        var profiles = new List<ConnectionProfile>();
        if (device.AvailableTransports.Contains(DeviceTransport.Rfcomm)
            || device.AvailableTransports.Contains(DeviceTransport.BluetoothClassic))
        {
            profiles.Add(new ConnectionProfile(DeviceTransport.Rfcomm, "Linux RFCOMM", Priority: 0));
        }

        if (device.AvailableTransports.Contains(DeviceTransport.Gatt))
            profiles.Add(new ConnectionProfile(DeviceTransport.Gatt, "Linux GATT", Priority: 1));

        if (profiles.Count == 0)
        {
            profiles.Add(new ConnectionProfile(DeviceTransport.Rfcomm, "Linux RFCOMM", Priority: 0));
            profiles.Add(new ConnectionProfile(DeviceTransport.Gatt, "Linux GATT", Priority: 1));
        }

        return profiles;
    }
}
