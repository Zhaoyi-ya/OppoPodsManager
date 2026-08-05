using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using OppoPodsManager.Assets.Oplus;
using OppoPodsManager.Control.Vivo;
using OppoPodsManager.Control.Edifier;

namespace OppoPodsManager.Communication.Windows;

// Windows 平台内部的设备发现实现：Win32 fConnected 为主，WinRT 为补充，
// 并使用 Melody 服务 UUID 或官方型号目录过滤候选。
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class OppoWindowsDeviceDiscoveryCore
{
    private const int WinRtTimeoutMs = 4000;
    private const int MaxNameSize = 248;

    public static IReadOnlyList<(ulong Address, string Name)> ListConnected()
    {
        var merged = new Dictionary<ulong, string>();
        try
        {
            foreach (var device in EnumerateWin32())
            {
                if (device.Connected && device.Address != 0 && IsCandidate(device.Name, HasMelodyService(device.Address)))
                    merged.TryAdd(device.Address, device.Name);
            }
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Logging.ApplicationLog.Current?.Error("Bluetooth", "Win32 设备发现失败。", exception);
        }

        try
        {
            var task = EnumerateWinRtAsync();
            if (task.Wait(WinRtTimeoutMs))
            {
                foreach (var device in task.Result)
                    if (device.Address != 0 && IsCandidate(device.Name, HasMelodyService(device.Address)))
                        merged.TryAdd(device.Address, device.Name);
            }
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Logging.ApplicationLog.Current?.Error("Bluetooth", "WinRT 设备发现失败。", exception);
        }

        return merged
            .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
    }

    private static async Task<IReadOnlyList<(ulong Address, string Name)>> EnumerateWinRtAsync()
    {
        var result = new List<(ulong Address, string Name)>();
        var seen = new HashSet<ulong>();
        var selector = global::Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
            global::Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
        var devices = await global::Windows.Devices.Enumeration.DeviceInformation.FindAllAsync(selector);
        foreach (var info in devices)
        {
            var device = await global::Windows.Devices.Bluetooth.BluetoothDevice.FromIdAsync(info.Id);
            if (device is null || device.ConnectionStatus != global::Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected)
                continue;

            var address = device.BluetoothAddress;
            var name = string.IsNullOrWhiteSpace(info.Name) ? $"耳机 {address:X12}" : info.Name;
            if (address != 0 && seen.Add(address))
                result.Add((address, name));
        }
        return result;
    }

    private static IReadOnlyList<(ulong Address, string Name, bool Connected)> EnumerateWin32()
    {
        var result = new List<(ulong Address, string Name, bool Connected)>();
        var search = new BluetoothDeviceSearchParameters
        {
            Size = (uint)Marshal.SizeOf<BluetoothDeviceSearchParameters>(),
            ReturnAuthenticated = 1,
            ReturnRemembered = 1,
            ReturnConnected = 1
        };
        var info = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
        var handle = BluetoothFindFirstDevice(ref search, ref info);
        try
        {
            if (handle == IntPtr.Zero)
                return result;
            do
            {
                var address = info.Address & 0xFFFFFFFFFFFFUL;
                var name = ReadName(ref info);
                result.Add((address, string.IsNullOrWhiteSpace(name) ? $"耳机 {address:X12}" : name, info.Connected != 0));
                info = new BluetoothDeviceInfo { Size = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
            }
            while (BluetoothFindNextDevice(handle, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(handle);
        }
        return result;
    }

    private static bool IsCandidate(string? name, bool hasMelodyService)
    {
        if (hasMelodyService)
            return true;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // 多品牌：vivo / iQOO 与 Edifier 不在 OPPO 型号目录，也不含 Melody 服务，
        // 仅凭家族名即可作为候选（真实 RFCOMM 通道由 SDP 查询/扫描解析）。
        if (VivoModels.IsFamilyName(name) || EdifierModels.IsFamilyName(name))
            return true;

        var normalized = Normalize(name);
        return DeviceModelData.LoadCatalog().Models.Any(model => model.Names.Any(modelName =>
        {
            var candidate = Normalize(modelName);
            return candidate.Length > 0
                && (normalized == candidate || normalized.StartsWith(candidate, StringComparison.Ordinal));
        }));
    }

    private static bool HasMelodyService(ulong address)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{address:X12}");
            return key?.GetSubKeyNames().Any(name =>
                name.Contains("0000079A", StringComparison.OrdinalIgnoreCase)
                || name.Contains("000079A", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static string ReadName(ref BluetoothDeviceInfo info)
    {
        var builder = new StringBuilder(MaxNameSize);
        for (var index = 0; index < MaxNameSize; index++)
        {
            var value = info.Name[index];
            if (value == 0)
                break;
            builder.Append((char)value);
        }
        return builder.ToString();
    }

    [InlineArray(MaxNameSize)]
    private struct NameBuffer { private ushort _value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

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
    private static extern IntPtr BluetoothFindFirstDevice(ref BluetoothDeviceSearchParameters search, ref BluetoothDeviceInfo device);
    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool BluetoothFindNextDevice(IntPtr handle, ref BluetoothDeviceInfo device);
    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr handle);
}
