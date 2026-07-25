using System.Runtime.Versioning;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Platforms.Windows;

/// <summary>
/// Opens platform raw connections for Windows. Tries RFCOMM first, then GATT later.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsPlatformConnectionFactory : IPlatformConnectionFactory
{
    public IReadOnlyList<ConnectionProfile> GetProfiles(RawDeviceCandidate device) =>
        WindowsRawConnectionProfiles.ForCandidate(device);

    public ValueTask<IRawConnection> OpenAsync(
        RawDeviceCandidate device,
        ConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IRawConnection connection = profile.Transport switch
        {
            DeviceTransport.Rfcomm or DeviceTransport.BluetoothClassic =>
                new WindowsRfcommRawConnection(device, profile),
            DeviceTransport.Gatt =>
                new WindowsGattRawConnection(device, profile),
            _ => throw new NotSupportedException($"不支持的传输: {profile.Transport}"),
        };
        return ValueTask.FromResult(connection);
    }
}
