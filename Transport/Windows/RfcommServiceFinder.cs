using System;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace OppoPodsManager;

/// <summary>
/// 经典蓝牙(RFCOMM/SPP)服务发现：用 WinRT DeviceInformation 枚举，
/// 返回可直接用于连接的 RfcommDeviceService。按优先级：
///   1) 按 OPPO SPP 服务 UUID 枚举（RfcommDeviceService.GetDeviceSelector）——最精确；
///   2) 按已配对经典设备的品牌名匹配，再取其上的 SPP 服务。
/// 供 WindowsRfcommLocator（取地址）与 WindowsRfcommStreamTransport（取服务连接）共用。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class RfcommServiceFinder
{
    /// <summary>
    /// 发现 OPPO SPP RfcommDeviceService，找不到返回 null。
    /// targetAddr!=0 时只匹配该 48 位地址的设备（多耳机切换用）；为 0 时沿用"品牌名优先/首个"旧逻辑。
    /// </summary>
    public static async Task<RfcommDeviceService?> FindServiceAsync(ulong targetAddr = 0)
    {
        var svc = await FindByServiceUuidAsync(targetAddr);
        if (svc != null) { Log.D("BT", "RfcommFinder: 命中(服务 UUID 枚举)"); return svc; }

        svc = await FindByPairedNameAsync(targetAddr);
        if (svc != null) { Log.D("BT", "RfcommFinder: 命中(配对经典名称)"); return svc; }

        return null;
    }

    /// <summary>按 OPPO SPP 服务 UUID 用 DeviceInformation 枚举；有目标地址则精确匹配，否则品牌名优先。</summary>
    private static async Task<RfcommDeviceService?> FindByServiceUuidAsync(ulong targetAddr)
    {
        try
        {
            var serviceId = RfcommServiceId.FromUuid(OppoProtocol.OppoSppUuid);
            string selector = RfcommDeviceService.GetDeviceSelector(serviceId);
            var devices = await DeviceInformation.FindAllAsync(selector);
            Log.D("BT", $"RfcommFinder: 服务 UUID 枚举到 {devices.Count} 个候选 (目标={(targetAddr == 0 ? "任意" : targetAddr.ToString("X12"))})");
            for (int i = 0; i < devices.Count; i++)
                Log.D("BT", $"RfcommFinder:   候选[{i}] name=\"{devices[i].Name}\" 支持品牌={IsSupportedBrand(devices[i].Name)} id={devices[i].Id}");
            if (devices.Count == 0) return null;

            // 有目标地址：只开该地址的设备，命中或全不匹配都直接结束（不回退到别的耳机）
            if (targetAddr != 0)
            {
                foreach (var di in devices)
                {
                    var svc = await TryOpenAsync(di.Id);
                    if (svc?.Device != null && svc.Device.BluetoothAddress == targetAddr)
                    {
                        Log.D("BT", $"RfcommFinder: 打开成功(目标地址匹配) name=\"{di.Name}\"");
                        return await ResolveFreshAsync(svc);
                    }
                    svc?.Dispose();  // 非目标设备，释放
                }
                Log.D("BT", "RfcommFinder: 服务 UUID 枚举里无目标地址设备");
                return null;
            }

            foreach (var di in devices)
                if (IsSupportedBrand(di.Name))
                {
                    var svc = await TryOpenAsync(di.Id);
                    if (svc != null) { Log.D("BT", $"RfcommFinder: 打开成功(品牌匹配) name=\"{di.Name}\""); return await ResolveFreshAsync(svc); }
                }
            foreach (var di in devices)
            {
                var svc = await TryOpenAsync(di.Id);
                if (svc != null) { Log.D("BT", $"RfcommFinder: 打开成功(回退首个) name=\"{di.Name}\""); return await ResolveFreshAsync(svc); }
            }
        }
        catch (Exception ex) { Log.Ex("BT", "RfcommFinder.FindByServiceUuidAsync", ex); }
        return null;
    }

    /// <summary>枚举已配对经典设备，按目标地址(优先)/品牌名匹配，再取其上的 OPPO SPP 服务。</summary>
    private static async Task<RfcommDeviceService?> FindByPairedNameAsync(ulong targetAddr)
    {
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector);
            Log.D("BT", $"RfcommFinder: 已配对经典枚举到 {devices.Count} 个");
            var serviceId = RfcommServiceId.FromUuid(OppoProtocol.OppoSppUuid);

            foreach (var di in devices)
            {
                var dev = await BluetoothDevice.FromIdAsync(di.Id);
                if (dev == null) continue;

                // 有目标地址：只认地址；无目标：认品牌名
                if (targetAddr != 0)
                {
                    if (dev.BluetoothAddress != targetAddr) continue;
                }
                else if (!IsSupportedBrand(di.Name)) continue;

                var svcResult = await dev.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached);
                if (svcResult?.Services != null && svcResult.Services.Count > 0)
                    return svcResult.Services[0];
            }
        }
        catch (Exception ex) { Log.Ex("BT", "RfcommFinder.FindByPairedNameAsync", ex); }
        return null;
    }

    /// <summary>
    /// 把（可能来自缓存的）RfcommDeviceService 重新做一次 Uncached SDP 解析，拿到带有效 RFCOMM
    /// 服务端通道号的新鲜记录。
    /// 现象：设备刚重连时 RfcommDeviceService.FromIdAsync 返回的缓存记录里通道号为
    /// 0（日志里 svc=...#RFCOMM:00000000:...），此时 StreamSocket.ConnectAsync / Winsock 都会
    /// 连到无效通道而超时。经设备侧 GetRfcommServicesForIdAsync(Uncached) 强制重查 SDP 即可拿到真实通道。
    /// 重查失败/无结果时回退用原缓存服务（好过没有）。
    /// </summary>
    private static async Task<RfcommDeviceService?> ResolveFreshAsync(RfcommDeviceService cached)
    {
        try
        {
            var dev = cached.Device;
            if (dev == null) return cached;

            var serviceId = RfcommServiceId.FromUuid(OppoProtocol.OppoSppUuid);
            var fresh = await dev.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached);
            if (fresh?.Services != null && fresh.Services.Count > 0)
            {
                var svc = fresh.Services[0];
                Log.D("BT", $"RfcommFinder: Uncached 重解析成功 svc={svc.ConnectionServiceName}");
                cached.Dispose();   // 释放缓存句柄，改用新鲜服务
                return svc;
            }
            Log.D("BT", "RfcommFinder: Uncached 重解析无结果，回退缓存服务");
        }
        catch (Exception ex) { Log.Ex("BT", "RfcommFinder.ResolveFreshAsync", ex); }
        return cached;
    }

    private static async Task<RfcommDeviceService?> TryOpenAsync(string deviceId)
    {
        try
        {
            var svc = await RfcommDeviceService.FromIdAsync(deviceId);
            if (svc == null) Log.D("BT", $"RfcommFinder.TryOpenAsync: FromIdAsync 返回 null (访问被拒/设备离线) id={deviceId}");
            return svc;
        }
        catch (Exception ex) { Log.Ex("BT", $"RfcommFinder.TryOpenAsync id={deviceId}", ex); return null; }
    }

    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
