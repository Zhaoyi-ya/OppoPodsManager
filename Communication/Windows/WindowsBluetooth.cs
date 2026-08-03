using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace OppoPodsManager.Communication.Windows;

// 使用经典蓝牙 API 发现已连接设备，避免 WinRT 代理枚举在 AOT 下的不稳定性。
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsBluetoothDiscovery : IDeviceDiscovery
{
    // 将系统已连接的蓝牙设备转换为统一候选项。
    public async Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        return WindowsClassicBluetoothDiscovery.EnumerateConnected()
            .Select(device => new DeviceCandidate(
                device.Address.ToString("X12"),
                device.Address.ToString("X12"),
                device.Address.ToString("X12"),
                device.Name,
                [WindowsSppConnection.MelodyServiceId],
                [WindowsRfcommFactory.TransportName]))
            .ToArray();
    }

}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsRfcommFactory : IConnectionFactory
{
    public const string TransportName = "rfcomm";

    public string Transport => TransportName;

    public async Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var connection = new WindowsSppConnection(candidate, options.ServiceId);
        try
        {
            await connection.ConnectAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

// 封装系统经典蓝牙枚举，返回当前已连接设备的地址和显示名称。
[SupportedOSPlatform("windows")]
internal static class WindowsClassicBluetoothDiscovery
{
    private const int NameLength = 248;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [InlineArray(NameLength)]
    private struct NameBuffer { private ushort _element; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceInfo
    {
        public uint Size;
        public ulong Address;
        public uint ClassOfDevice;
        public int Connected, Remembered, Authenticated;
        public SystemTime LastSeen, LastUsed;
        public NameBuffer Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParameters
    {
        public uint Size;
        public int ReturnAuthenticated, ReturnRemembered, ReturnUnknown, ReturnConnected, IssueInquiry;
        public byte TimeoutMultiplier;
        public IntPtr Radio;
    }

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParameters search, ref BluetoothDeviceInfo device);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool BluetoothFindNextDevice(IntPtr handle, ref BluetoothDeviceInfo device);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr handle);

    public static IReadOnlyList<(ulong Address, string Name)> EnumerateConnected()
    {
        var devices = new List<(ulong Address, string Name)>();
        var search = new BluetoothDeviceSearchParameters
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceSearchParameters>(),
            ReturnAuthenticated = 1,
            ReturnRemembered = 1,
            ReturnConnected = 1
        };
        var device = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
        var handle = BluetoothFindFirstDevice(ref search, ref device);
        try
        {
            if (handle == IntPtr.Zero)
                return devices;

            do
            {
                var address = device.Address & 0xFFFFFFFFFFFFUL;
                if (device.Connected != 0 && address != 0)
                {
                    var name = ReadName(ref device);
                    devices.Add((address, string.IsNullOrWhiteSpace(name) ? $"Bluetooth {address:X12}" : name));
                }
                device = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
            }
            while (BluetoothFindNextDevice(handle, ref device));
        }
        finally
        {
            if (handle != IntPtr.Zero)
                BluetoothFindDeviceClose(handle);
        }
        return devices;
    }

    // 从固定 WCHAR 缓冲区读取设备名称，不依赖运行时字符串封送。
    private static string ReadName(ref BluetoothDeviceInfo device)
    {
        var characters = new List<char>(NameLength);
        for (var index = 0; index < NameLength; index++)
        {
            var character = device.Name[index];
            if (character == 0)
                break;
            characters.Add((char)character);
        }
        return new string(characters.ToArray());
    }
}
