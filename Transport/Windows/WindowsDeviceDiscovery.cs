using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OppoPodsManager;

/// <summary>Windows 已配对设备与当前连接设备的托管发现实现。</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsDeviceDiscovery
{
    private const int WinRtTimeoutMs = 4000;

    public static IReadOnlyList<(ulong addr, string name)> ListPaired()
    {
        var result = new List<(ulong addr, string name)>();
        var seen = new HashSet<ulong>();
        try
        {
            Log.D("BT", "Locate: 扫描注册表已配对蓝牙设备...");
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            if (key == null) return result;

            var subKeys = key.GetSubKeyNames();
            Log.D("BT", $"Locate: 共 {subKeys.Length} 个已配对设备");
            foreach (var subName in subKeys)
            {
                if (!TryParseAddress(subName, out var addr)) continue;
                var name = ReadDeviceName(key, subName);
                if (IsSupportedBrand(name) && seen.Add(addr))
                {
                    var displayName = name ?? $"耳机 {addr:X12}";
                    result.Add((addr, displayName));
                    Log.D("BT", $"Locate: 按品牌命中设备 addr={addr:X12} name=\"{displayName}\"");
                }
            }

            foreach (var subName in subKeys)
            {
                if (!TryParseAddress(subName, out var addr) || !seen.Add(addr)) continue;
                if (!HasOppoSppService(subName)) continue;
                var name = ReadDeviceName(key, subName) ?? $"耳机 {addr:X12}";
                result.Add((addr, name));
                Log.D("BT", $"Locate: 按 SPP UUID 命中设备 addr={addr:X12} name=\"{name}\"");
            }
            Log.D("BT", $"Locate: 注册表候选共 {result.Count} 个");
        }
        catch (Exception ex) { Log.Ex("BT", "WindowsDeviceDiscovery.ListPaired", ex); }
        return result;
    }

    public static IReadOnlyList<(ulong addr, string name)> ListConnectedViaWinRt()
    {
        try
        {
            var task = EnumerateConnectedAsync();
            var result = task.Wait(WinRtTimeoutMs) ? task.Result : [];
            result.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            Log.D("BT", $"ConnectedFinder: 当前已连接受支持耳机 {result.Count} 副");
            return result;
        }
        catch (Exception ex)
        {
            Log.Ex("BT", "WindowsDeviceDiscovery.ListConnectedViaWinRt", ex);
            return [];
        }
    }

    private static async Task<List<(ulong addr, string name)>> EnumerateConnectedAsync()
    {
        var result = new List<(ulong addr, string name)>();
        var seen = new HashSet<ulong>();
        string selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector);
        Log.D("BT", $"ConnectedFinder: 已连接经典设备枚举到 {devices.Count} 个");

        foreach (var info in devices)
        {
            if (!IsSupportedBrand(info.Name)) continue;
            BluetoothDevice? device = null;
            try { device = await BluetoothDevice.FromIdAsync(info.Id); }
            catch (Exception ex) { Log.Ex("BT", $"ConnectedFinder.FromIdAsync name=\"{info.Name}\"", ex); }
            if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

            ulong addr = device.BluetoothAddress;
            if (addr == 0 || !seen.Add(addr)) continue;
            var name = string.IsNullOrEmpty(info.Name) ? $"耳机 {addr:X12}" : info.Name;
            result.Add((addr, name));
            Log.D("BT", $"ConnectedFinder: 命中已连接 addr={addr:X12} name=\"{name}\"");
        }
        return result;
    }

    private static bool TryParseAddress(string value, out ulong address)
    {
        address = 0;
        return value.Length == 12 && ulong.TryParse(
            value,
            System.Globalization.NumberStyles.HexNumber,
            null,
            out address);
    }

    private static string? ReadDeviceName(RegistryKey devicesKey, string subKeyName)
    {
        try
        {
            using var deviceKey = devicesKey.OpenSubKey(subKeyName);
            if (deviceKey == null) return null;
            return DecodeName(deviceKey.GetValue("Name")) ?? DecodeName(deviceKey.GetValue("FriendlyName"));
        }
        catch (Exception ex)
        {
            Log.Ex("BT", $"ReadDeviceName({subKeyName})", ex);
            return null;
        }
    }

    private static string? DecodeName(object? value) => value switch
    {
        byte[] bytes => System.Text.Encoding.ASCII.GetString(bytes).TrimEnd('\0'),
        string text when !string.IsNullOrEmpty(text) => text,
        _ => null,
    };

    private static bool HasOppoSppService(string subKeyName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{subKeyName}");
            if (key == null) return false;
            foreach (var serviceName in key.GetSubKeyNames())
                if (serviceName.Contains("0000079A", StringComparison.OrdinalIgnoreCase) ||
                    serviceName.Contains("000079A", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch (Exception ex) { Log.Ex("BT", $"HasOppoSppService({subKeyName})", ex); }
        return false;
    }

    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
