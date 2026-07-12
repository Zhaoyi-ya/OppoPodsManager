using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OppoPodsManager;

/// <summary>
/// Windows 设备发现：从注册表 BTHPORT\Parameters\Devices 找已配对的 OPPO/SPP 耳机。
/// 仅 Windows 可用；其它平台请实现各自的 IDeviceLocator。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBluetoothLocator : IDeviceLocator
{
    public (ulong addr, string? name) Locate()
    {
        var devices = ListPaired();
        return devices.Count > 0 ? devices[0] : (0, null);
    }

    /// <summary>枚举注册表中全部受支持品牌或带 OPPO SPP 服务的已配对设备。</summary>
    public IReadOnlyList<(ulong addr, string name)> ListPaired()
    {
        var result = new List<(ulong addr, string name)>();
        var seen = new HashSet<ulong>();
        ReadBtDevices(result, seen);
        return result;
    }

    private static void ReadBtDevices(List<(ulong addr, string name)> result, HashSet<ulong> seen)
    {
        try
        {
            Log.D("BT", "Locate: 扫描注册表已配对蓝牙设备...");
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            if (key == null) { Log.D("BT", "Locate: 打不开 BTHPORT\\Devices 注册表项"); return; }

            var subKeys = key.GetSubKeyNames();
            Log.D("BT", $"Locate: 共 {subKeys.Length} 个已配对设备");

            // 第一轮：按所有支持品牌匹配，不再只识别 OPPO。
            foreach (var subName in subKeys)
            {
                if (subName.Length != 12 || !ulong.TryParse(subName,
                    System.Globalization.NumberStyles.HexNumber, null, out var addr))
                    continue;

                string? name = ReadBtDeviceName(key, subName);

                if (IsSupportedBrand(name) && seen.Add(addr))
                {
                    var displayName = name ?? ("耳机 " + addr.ToString("X12"));
                    result.Add((addr, displayName));
                    Log.D("BT", $"Locate: 按品牌命中设备 addr={addr:X12} name=\"{displayName}\"");
                }
            }

            // 第二轮：按服务 UUID 匹配（即使名称不含 "OPPO" 也能找到）
            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Length != 12 || !ulong.TryParse(subName,
                    System.Globalization.NumberStyles.HexNumber, null, out var addr))
                    continue;

                if (HasOppoSppService(key, subName) && seen.Add(addr))
                {
                    var name = ReadBtDeviceName(key, subName) ?? ("耳机 " + addr.ToString("X12"));
                    result.Add((addr, name));
                    Log.D("BT", $"Locate: 按 SPP UUID 命中设备 addr={addr:X12} name=\"{name}\"");
                }
            }
            Log.D("BT", $"Locate: 注册表候选共 {result.Count} 个");
        }
        catch (Exception ex) { Log.Ex("BT", "Locate", ex); }
    }

    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>从注册表读取蓝牙设备名称</summary>
    private static string? ReadBtDeviceName(RegistryKey devicesKey, string subKeyName)
    {
        try
        {
            using var devKey = devicesKey.OpenSubKey(subKeyName);
            if (devKey == null) return null;

            var raw = devKey.GetValue("Name");
            var name = raw switch
            {
                byte[] bytes => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
                string s => s,
                _ => null
            };

            if (string.IsNullOrEmpty(name))
            {
                var fn = devKey.GetValue("FriendlyName");
                name = fn switch
                {
                    string s => s,
                    byte[] bytes => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
                    _ => null
                };
            }

            return name;
        }
        catch (Exception ex) { Log.Ex("BT", $"ReadBtDeviceName({subKeyName})", ex); return null; }
    }

    /// <summary>检查注册表中是否有 OPPO SPP 服务的 SDP 记录</summary>
    private static bool HasOppoSppService(RegistryKey devicesKey, string subKeyName)
    {
        try
        {
            // Windows 存储 SDP 记录的路径
            using var sdpKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{subKeyName}");
            if (sdpKey == null) return false;

            // OPPO SPP UUID: 0000079A-D102-11E1-9B23-00025B00A5A5
            // 注册表中服务子键名称为 UUID 去横线大写: 0000079AD10211E19B2300025B00A5A5
            foreach (var serviceName in sdpKey.GetSubKeyNames())
            {
                if (serviceName.Contains("0000079A", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (serviceName.Contains("000079A", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception ex) { Log.Ex("BT", $"HasOppoSppService({subKeyName})", ex); return false; }
    }

}
