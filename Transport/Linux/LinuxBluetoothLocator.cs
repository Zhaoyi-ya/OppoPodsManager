using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OppoPodsManager;

public sealed class LinuxBluetoothLocator : IDeviceLocator
{
    private static readonly Regex AnsiRegex = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);
    private static string StripAnsi(string s) => AnsiRegex.Replace(s, "");

    public (ulong addr, string? name) Locate()
    {
        try { var r = FindViaBluetoothctl(); if (r.addr != 0) return r; }
        catch (Exception ex) { Log.D("BT", $"bluetoothctl failed: {ex.Message}"); }
        try { var r = FindViaFileSystem(); if (r.addr != 0) return r; }
        catch (Exception ex) { Log.D("BT", $"filesystem scan failed: {ex.Message}"); }
        Log.D("BT", "Locate: no OPPO Bluetooth device found");
        return (0, null);
    }

    private static (ulong addr, string? name) FindViaBluetoothctl()
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "bluetoothctl", Arguments = "devices Paired",
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        });
        if (proc == null) return (0, null);
        var output = StripAnsi(proc.StandardOutput.ReadToEnd());
        proc.WaitForExit(3000);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Log.D("BT", $"FindViaBluetoothctl: {lines.Length} lines");

        // Collect all candidates, prefer currently connected device
        var candidates = new List<(ulong addr, string name)>();
        var fallback = new List<(ulong addr, string name)>();
        foreach (var line in lines)
        {
            var (addr, name) = ParseDeviceLine(line);
            if (addr == 0 || name == null || !IsSupportedBrand(name)) continue;
            if (IsEarbudDevice(name)) candidates.Add((addr, name));
            else fallback.Add((addr, name));
        }

        foreach (var c in candidates) fallback.Add(c); // earbuds first in fallback
        if (fallback.Count == 0) { Log.D("BT", "FindViaBluetoothctl: no matching devices"); return (0, null); }

        // Check which candidate is currently connected
        foreach (var c in candidates)
        {
            if (IsDeviceConnected(c.addr))
            {
                Log.D("BT", $"FindViaBluetoothctl: selected connected device \"{c.name}\"");
                return c;
            }
        }
        foreach (var c in fallback)
        {
            if (IsDeviceConnected(c.addr))
            {
                Log.D("BT", $"FindViaBluetoothctl: selected connected device \"{c.name}\"");
                return c;
            }
        }

        // Prefer earbud devices, then fallback
        if (candidates.Count > 0)
        {
            Log.D("BT", $"FindViaBluetoothctl: none connected, using first earbud \"{candidates[0].name}\"");
            return candidates[0];
        }
        Log.D("BT", $"FindViaBluetoothctl: none connected, using fallback \"{fallback[0].name}\"");
        return fallback[0];
    }

    private static (ulong addr, string? name) ParseDeviceLine(string line)
    {
        if (!line.StartsWith("Device ")) return (0, null);
        var parts = line.Substring(7).Split(' ', 2);
        if (parts.Length < 1) return (0, null);
        var addr = ParseBtAddr(parts[0]);
        var name = parts.Length > 1 ? parts[1].Trim() : null;
        return (addr, name);
    }

    private static bool IsDeviceConnected(ulong addr)
    {
        try
        {
            var addrStr = BtAddrToString(addr);
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "bluetoothctl", Arguments = $"info {addrStr}",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (proc == null) return false;
            var output = StripAnsi(proc.StandardOutput.ReadToEnd());
            proc.WaitForExit(3000);
            return output.Contains("Connected: yes");
        }
        catch { return false; }
    }

    private static string BtAddrToString(ulong addr)
    {
        return string.Join(":", new[]
        {
            (byte)((addr >> 40) & 0xFF), (byte)((addr >> 32) & 0xFF),
            (byte)((addr >> 24) & 0xFF), (byte)((addr >> 16) & 0xFF),
            (byte)((addr >> 8) & 0xFF), (byte)(addr & 0xFF)
        }.Select(b => b.ToString("X2")));
    }

    private static bool IsEarbudDevice(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        foreach (var kw in new[] { "buds", "enco", "air", "clip", "free", "bullets" })
            if (lower.Contains(kw)) return true;
        return false;
    }

    private static (ulong addr, string? name) FindViaFileSystem()
    {
        var path = "/var/lib/bluetooth";
        if (!Directory.Exists(path)) return (0, null);
        foreach (var adapterDir in Directory.GetDirectories(path))
            foreach (var deviceDir in Directory.GetDirectories(adapterDir))
            {
                var infoFile = Path.Combine(deviceDir, "info");
                if (!File.Exists(infoFile)) continue;
                var (addr, name) = ParseInfoFile(infoFile);
                if (addr != 0 && IsSupportedBrand(name)) return (addr, name);
            }
        return (0, null);
    }

    private static (ulong addr, string? name) ParseInfoFile(string infoFile)
    {
        try
        {
            var lines = File.ReadAllLines(infoFile);
            string? name = null; ulong addr = 0;
            foreach (var line in lines)
            {
                if (line.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                    name = line.Substring(5).Trim();
                else if (line.StartsWith("Address=", StringComparison.OrdinalIgnoreCase))
                    ulong.TryParse(line.Substring(8).Trim().Replace(":", ""),
                        System.Globalization.NumberStyles.HexNumber, null, out addr);
            }
            return (addr, name);
        }
        catch { return (0, null); }
    }

    private static ulong ParseBtAddr(string? addrStr)
    {
        if (string.IsNullOrEmpty(addrStr)) return 0;
        var hex = addrStr.Replace(":", "").Replace("-", "");
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var a) ? a : 0;
    }

    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
