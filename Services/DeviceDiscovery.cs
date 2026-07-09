using System;
using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>
/// 已配对耳机发现（供 UI 的设备选择器使用）。
/// Windows 下走注册表枚举；其它平台暂返回空列表（后续可接 BlueZ/IOBluetooth）。
/// </summary>
public static class DeviceDiscovery
{
    /// <summary>
    /// 返回"当前已连接"的受支持品牌耳机 (地址, 显示名)。无或不支持的平台返回空列表。
    /// 只列出此刻有活动蓝牙链路的设备（不含历史配对但离线的），地址可直接传给 PodManager(targetAddr, name)。
    ///
    /// 多源合并（提升覆盖率——单一 WinRT 枚举会漏掉部分设备）：
    ///   1) Win32 BluetoothFindFirstDevice：权威 fConnected，覆盖 WinRT 枚举不出来的经典耳机（主源）；
    ///   2) WinRT GetDeviceSelectorFromConnectionStatus：补 Win32 没报的（如个别只走 BLE 的）。
    /// 两源按 48 位地址去重合并。
    ///
    /// 注意：会做蓝牙枚举（有 IO），应在后台线程调用，勿在 UI 线程直接调。
    /// </summary>
    public static IReadOnlyList<(ulong addr, string name)> ListConnected()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var merged = new Dictionary<ulong, string>();

            // 源 1：Win32（只取 connected=true 的）
            try
            {
                foreach (var (addr, name, connected) in Win32BluetoothFinder.Enumerate())
                    if (connected && addr != 0)
                        merged[addr] = name;
            }
            catch (Exception ex) { Log.Ex("BT", "DeviceDiscovery.Win32", ex); }

            // 源 2：WinRT（补 Win32 漏掉的；已有的不覆盖名字）
            try
            {
                foreach (var (addr, name) in WindowsConnectedDeviceFinder.ListConnected())
                    if (addr != 0 && !merged.ContainsKey(addr))
                        merged[addr] = name;
            }
            catch (Exception ex) { Log.Ex("BT", "DeviceDiscovery.WinRT", ex); }

            var list = new List<(ulong addr, string name)>();
            foreach (var kv in merged) list.Add((kv.Key, kv.Value));
            list.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            Log.D("BT", $"DeviceDiscovery.ListConnected: 合并去重后 {list.Count} 副 (Win32+WinRT)");
            return list;
        }
#endif
#if LINUX
        if (OperatingSystem.IsLinux())
        {
            try { return new LinuxBluetoothLocator().LocateAllConnected(); }
            catch (Exception ex) { Log.Ex("BT", "DeviceDiscovery.Linux", ex); }
        }
#endif
        return Array.Empty<(ulong, string)>();
    }
}
