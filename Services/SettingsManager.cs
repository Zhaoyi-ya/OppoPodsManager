using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OppoPodsManager;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class AppSettingsContext : JsonSerializerContext { }

/// <summary>JSON 文件持久化设置存储</summary>
public static class SettingsManager
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OppoPodsManager", "settings.json");
    private static readonly string OldFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OppoPodsWin", "settings.json");

    private static Dictionary<string, string> _cache = new();
    private static DateTime _lastRead = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);

    private static Dictionary<string, string> Load()
    {
        if (DateTime.Now - _lastRead < CacheDuration && _cache.Count > 0)
            return _cache;
        try
        {
            // 迁移旧配置：OppoPodsWin → OppoPodsManager
            if (!File.Exists(FilePath) && File.Exists(OldFilePath))
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.Copy(OldFilePath, FilePath);
                Log.D("CFG", $"Migrate: {OldFilePath} → {FilePath}");
            }

            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _cache = JsonSerializer.Deserialize(json, AppSettingsContext.Default.DictionaryStringString) ?? new();
                Log.D("CFG", $"Load: 从 {FilePath} 读取 {_cache.Count} 项");
            }
            else
            {
                _cache = new Dictionary<string, string>();
                Log.D("CFG", $"Load: 配置文件不存在 {FilePath},使用空配置");
            }
        }
        catch (Exception ex)
        {
            Log.Ex("CFG", "Load", ex);
            _cache = new Dictionary<string, string>();
        }
        _lastRead = DateTime.Now;
        return _cache;
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_cache, AppSettingsContext.Default.DictionaryStringString));
            Log.D("CFG", $"Save: 写入 {_cache.Count} 项 -> {FilePath}");
        }
        catch (Exception ex) { Log.Ex("CFG", "Save", ex); }
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
        return val switch { "1" or "true" => true, "0" or "false" => false, _ => defaultValue };
    }

    public static void SetBool(string key, bool value) =>
        SetString(key, value ? "1" : null);

    public static int GetInt(string key, int defaultValue = 0)
    {
        var val = GetString(key);
        return int.TryParse(val, out var n) ? n : defaultValue;
    }

    public static void SetInt(string key, int value) =>
        SetString(key, value.ToString());
}
