using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OppoPodsManager;

/// <summary>
/// 用 WinRT 枚举"当前已连接"的经典蓝牙耳机（受支持品牌）。
/// 与注册表枚举不同：只返回此刻与本机存在活动蓝牙链路的设备，
/// 不会列出历史配对过但当前不在线的耳机（如已收盒/关机/不在范围的设备）。
/// 供设备选择器使用——只展示能真正连上的耳机。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsConnectedDeviceFinder
{
    private const int TimeoutMs = 4000;

    /// <summary>返回当前已连接的受支持品牌耳机 (地址, 显示名)，按名称排序去重。</summary>
    public static IReadOnlyList<(ulong addr, string name)> ListConnected()
    {
        var result = new List<(ulong addr, string name)>();
        try
        {
            var hits = RunSync(EnumerateConnectedAsync, TimeoutMs);
            if (hits != null) result.AddRange(hits);
        }
        catch (Exception ex) { Log.Ex("BT", "ConnectedFinder.ListConnected", ex); }

        result.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
        Log.D("BT", $"ConnectedFinder: 当前已连接受支持耳机 {result.Count} 副");
        return result;
    }

    private static async Task<List<(ulong addr, string name)>> EnumerateConnectedAsync()
    {
        var list = new List<(ulong addr, string name)>();
        var seen = new HashSet<ulong>();

        // 只取"已连接"状态的经典蓝牙设备
        string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector);
        Log.D("BT", $"ConnectedFinder: 已连接经典设备枚举到 {devices.Count} 个");

        foreach (var di in devices)
        {
            if (!IsSupportedBrand(di.Name)) continue;
            BluetoothDevice? dev = null;
            try { dev = await BluetoothDevice.FromIdAsync(di.Id); }
            catch (Exception ex) { Log.Ex("BT", $"ConnectedFinder.FromIdAsync name=\"{di.Name}\"", ex); }
            if (dev == null) continue;

            // 二次确认连接态（枚举结果可能有瞬时滞后）
            if (dev.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                Log.D("BT", $"ConnectedFinder: 跳过 name=\"{di.Name}\" 状态={dev.ConnectionStatus}");
                continue;
            }

            ulong addr = dev.BluetoothAddress;
            if (addr == 0 || !seen.Add(addr)) continue;
            var name = string.IsNullOrEmpty(di.Name) ? ("耳机 " + addr.ToString("X12")) : di.Name;
            list.Add((addr, name));
            Log.D("BT", $"ConnectedFinder: 命中已连接 addr={addr:X12} name=\"{name}\"");
        }
        return list;
    }

    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static T? RunSync<T>(Func<Task<T>> op, int timeoutMs) where T : class
    {
        try
        {
            var task = op();
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch (Exception ex) { Log.Ex("BT", "ConnectedFinder.RunSync", ex); return null; }
    }
}
