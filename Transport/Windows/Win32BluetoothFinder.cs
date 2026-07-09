using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace OppoPodsManager;

/// <summary>
/// 用 Win32 蓝牙 API（BluetoothFindFirstDevice/Next）枚举系统已知的经典蓝牙设备，
/// 并读取权威的 fConnected 标志。相比 WinRT 设备代理枚举，本路径能覆盖
/// 部分"WinRT 枚举不出来"的耳机（某些仅经典配对/固件不注册到 WinRT DeviceBroker 的设备）。
/// 纯 P/Invoke，结构体全 blittable（InlineArray 存名字），AOT 友好。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32BluetoothFinder
{
    private const int BLUETOOTH_MAX_NAME_SIZE = 248;

    [InlineArray(BLUETOOTH_MAX_NAME_SIZE)]
    private struct NameBuffer { private ushort _e0; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceInfo
    {
        public uint dwSize;
        public ulong Address;            // BLUETOOTH_ADDRESS 联合体：低 48 位为 MAC
        public uint ulClassofDevice;
        public int fConnected;           // BOOL
        public int fRemembered;          // BOOL
        public int fAuthenticated;       // BOOL
        public SystemTime stLastSeen;
        public SystemTime stLastUsed;
        public NameBuffer szName;        // WCHAR[248]
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BluetoothDeviceSearchParams
    {
        public uint dwSize;
        public int fReturnAuthenticated; // BOOL
        public int fReturnRemembered;    // BOOL
        public int fReturnUnknown;       // BOOL
        public int fReturnConnected;     // BOOL
        public int fIssueInquiry;        // BOOL
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BluetoothDeviceSearchParams pbtsp, ref BluetoothDeviceInfo pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool BluetoothFindNextDevice(IntPtr hFind, ref BluetoothDeviceInfo pbtdi);

    [DllImport("bthprops.cpl", SetLastError = true)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    /// <summary>
    /// 枚举系统已知设备，返回受支持品牌且当前已连接的耳机 (地址, 名称)。
    /// connectedOnly=false 时也返回未连接的已配对设备（connected 标记随行）。
    /// </summary>
    public static List<(ulong addr, string name, bool connected)> Enumerate()
    {
        var result = new List<(ulong addr, string name, bool connected)>();

        var search = new BluetoothDeviceSearchParams
        {
            dwSize = (uint)Marshal.SizeOf<BluetoothDeviceSearchParams>(),
            fReturnAuthenticated = 1,   // 已认证（= 已配对）
            fReturnRemembered = 1,      // 记住的
            fReturnUnknown = 0,
            fReturnConnected = 1,       // 已连接
            fIssueInquiry = 0,          // 不主动 inquiry（不扫空气，只查系统已知，快且不打扰）
            cTimeoutMultiplier = 0,
            hRadio = IntPtr.Zero        // 所有 radio
        };

        var info = new BluetoothDeviceInfo { dwSize = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };

        IntPtr hFind = IntPtr.Zero;
        try
        {
            hFind = BluetoothFindFirstDevice(ref search, ref info);
            if (hFind == IntPtr.Zero)
            {
                Log.D("BT", $"Win32Finder: BluetoothFindFirstDevice 无结果 (Win32Err={Marshal.GetLastWin32Error()})");
                return result;
            }

            do
            {
                string name = ReadName(ref info);
                bool connected = info.fConnected != 0;
                ulong addr = info.Address & 0xFFFFFFFFFFFFUL;
                Log.D("BT", $"Win32Finder: 设备 addr={addr:X12} name=\"{name}\" connected={connected} auth={info.fAuthenticated != 0}");

                if (addr != 0 && IsSupportedBrand(name))
                    result.Add((addr, string.IsNullOrEmpty(name) ? ("耳机 " + addr.ToString("X12")) : name, connected));

                info = new BluetoothDeviceInfo { dwSize = (uint)Marshal.SizeOf<BluetoothDeviceInfo>() };
            }
            while (BluetoothFindNextDevice(hFind, ref info));
        }
        catch (Exception ex) { Log.Ex("BT", "Win32Finder.Enumerate", ex); }
        finally
        {
            if (hFind != IntPtr.Zero) { try { BluetoothFindDeviceClose(hFind); } catch { } }
        }

        return result;
    }

    private static string ReadName(ref BluetoothDeviceInfo info)
    {
        var sb = new StringBuilder(BLUETOOTH_MAX_NAME_SIZE);
        for (int i = 0; i < BLUETOOTH_MAX_NAME_SIZE; i++)
        {
            ushort c = info.szName[i];
            if (c == 0) break;
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
