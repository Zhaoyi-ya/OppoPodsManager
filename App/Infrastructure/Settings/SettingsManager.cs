using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OppoPodsManager;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
internal partial class AppSettingsContext : JsonSerializerContext { }

/// <summary>JSON 文件持久化设置存储。</summary>
public static class SettingsManager
{
    private const string HiddenMultiDeviceMacsKey = "HiddenMultiDeviceMacs";
    private static readonly string RoamingDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string LocalDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string AppDataDirectory { get; } = Path.Combine(RoamingDir, "OppoPodsManager");
    private static readonly string FilePath = Path.Combine(AppDataDirectory, "settings.json");

    private static readonly string[] LegacyFilePaths =
    {
        Path.Combine(RoamingDir, "OppoPodsWin", "settings.json"),
        Path.Combine(RoamingDir, "OPPO Pods For Windows", "settings.json"),
        Path.Combine(RoamingDir, "OppoPodsForWindows", "settings.json"),
        Path.Combine(LocalDir, "OppoPodsManager", "settings.json"),
        Path.Combine(LocalDir, "OppoPodsWin", "settings.json"),
        Path.Combine(LocalDir, "OPPO Pods For Windows", "settings.json"),
        Path.Combine(LocalDir, "OppoPodsForWindows", "settings.json")
    };

    private static Dictionary<string, string> _cache = new();
    private static bool _loaded;
    private static DateTime _lastRead = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private static Dictionary<string, string> Load()
    {
        if (_loaded && DateTime.Now - _lastRead < CacheDuration)
            return _cache;

        foreach (var path in EnumerateConfigPaths())
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                var loaded = ParseSettings(json);
                if (loaded.Count == 0 && !string.IsNullOrWhiteSpace(json))
                    Log.D("CFG", $"Load: {path} 未读到有效配置项");

                _cache = loaded;
                _loaded = true;
                _lastRead = DateTime.Now;
                Log.D("CFG", $"Load: 从 {path} 读取 {_cache.Count} 项");

                if (!Path.GetFullPath(path).Equals(Path.GetFullPath(FilePath), StringComparison.OrdinalIgnoreCase))
                    Save();

                return _cache;
            }
            catch (Exception ex)
            {
                Log.Ex("CFG", $"Load {path}", ex);
            }
        }

        _cache = new Dictionary<string, string>();
        _loaded = true;
        _lastRead = DateTime.Now;
        Log.D("CFG", $"Load: 未找到可用配置文件，使用空配置 {FilePath}");
        return _cache;
    }

    private static IEnumerable<string> EnumerateConfigPaths()
    {
        yield return FilePath;
        foreach (var path in LegacyFilePaths)
            yield return path;
    }

    private static Dictionary<string, string> ParseSettings(string json)
    {
        try
        {
            var strict = JsonSerializer.Deserialize(json, AppSettingsContext.Default.DictionaryStringString);
            if (strict != null)
                return strict;
        }
        catch
        {
            // 继续走兼容解析，支持旧版本写入的 bool/int/array 等 JSON 值。
        }

        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var value = ConvertJsonElementToString(property.Value);
            if (value != null)
                result[property.Name] = value;
        }

        return result;
    }

    private static string? ConvertJsonElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array or JsonValueKind.Object => element.GetRawText(),
            _ => null
        };
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cache, AppSettingsContext.Default.DictionaryStringString);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);

            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);

            Log.D("CFG", $"Save: 写入 {_cache.Count} 项 -> {FilePath}");
        }
        catch (Exception ex)
        {
            Log.Ex("CFG", "Save", ex);
        }
    }

    public static string? GetString(string key, string? defaultValue = null)
    {
        var data = Load();
        return data.TryGetValue(key, out var v) ? v : defaultValue;
    }

    public static void SetString(string key, string? value)
    {
        var data = Load();
        if (value != null)
            data[key] = value;
        else
            data.Remove(key);
        Save();
    }

    public static bool GetBool(string key, bool defaultValue = false)
    {
        var val = GetString(key);
        return val?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => defaultValue
        };
    }

    public static void SetBool(string key, bool value) =>
        SetString(key, value ? "1" : null);

    public static int GetInt(string key, int defaultValue = 0)
    {
        var val = GetString(key);
        return int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : defaultValue;
    }

    public static void SetInt(string key, int value) =>
        SetString(key, value.ToString(CultureInfo.InvariantCulture));

    public static List<string>? GetStringList(string key)
    {
        var val = GetString(key);
        if (string.IsNullOrEmpty(val)) return null;
        try { return JsonSerializer.Deserialize(val, AppSettingsContext.Default.ListString); }
        catch { return null; }
    }

    public static void SetStringList(string key, List<string> value)
    {
        var json = JsonSerializer.Serialize(value, AppSettingsContext.Default.ListString);
        SetString(key, json);
    }

    public static HashSet<string> GetHiddenMultiDeviceMacs()
    {
        var values = GetStringList(HiddenMultiDeviceMacsKey) ?? new List<string>();
        return values
            .Select(NormalizeMacAddress)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static void HideMultiDevice(string address)
    {
        var normalized = NormalizeMacAddress(address);
        if (normalized.Length == 0) return;

        var hidden = GetHiddenMultiDeviceMacs();
        if (!hidden.Add(normalized)) return;
        SetStringList(HiddenMultiDeviceMacsKey, hidden.OrderBy(value => value).ToList());
    }

    public static bool ClearHiddenMultiDevices()
    {
        var hadHiddenDevices = GetHiddenMultiDeviceMacs().Count > 0;
        SetString(HiddenMultiDeviceMacsKey, null);
        return hadHiddenDevices;
    }

    private static string NormalizeMacAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return "";
        var hex = new string(address.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
        if (hex.Length != 12) return "";
        return string.Join(":", Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2)));
    }
}
