using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OppoPodsManager;

/// <summary>
/// 单条自定义 EQ 预设：6 段均衡器的 dB 值 (-6 ~ +6)。
/// </summary>
public sealed class CustomEqPreset
{
    public string Name { get; set; } = "";
    public int Band62 { get; set; }
    public int Band250 { get; set; }
    public int Band1k { get; set; }
    public int Band4k { get; set; }
    public int Band8k { get; set; }
    public int Band16k { get; set; }
}

/// <summary>ListBox 展示用数据项，区分内置/自定义预设。</summary>
public sealed class EqPresetItem
{
    public string Name { get; set; } = "";
    public bool IsCustom { get; set; }
}

/// <summary>
/// 自定义 EQ 预设持久化存储。
/// 文件位置：%APPDATA%\OppoPodsWin\CustomEq.json
/// </summary>
public static class EqPresetStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OppoPodsWin", "CustomEq.json");

    private static List<CustomEqPreset>? _cache;

    public static List<CustomEqPreset> LoadAll()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                _cache = JsonSerializer.Deserialize<List<CustomEqPreset>>(json) ?? new();
            }
            else
            {
                _cache = new List<CustomEqPreset>();
            }
        }
        catch
        {
            _cache = new List<CustomEqPreset>();
        }
        return _cache;
    }

    private static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_cache ?? new());
            File.WriteAllText(FilePath, json);
        }
        catch { /* 静默失败，不影响主流程 */ }
    }

    /// <summary>添加或更新预设（按名称匹配）。返回是否新建。</summary>
    public static bool SavePreset(CustomEqPreset preset)
    {
        var list = LoadAll();
        var existing = list.FirstOrDefault(p => p.Name == preset.Name);
        if (existing != null)
        {
            existing.Band62 = preset.Band62;
            existing.Band250 = preset.Band250;
            existing.Band1k = preset.Band1k;
            existing.Band4k = preset.Band4k;
            existing.Band8k = preset.Band8k;
            existing.Band16k = preset.Band16k;
            Save();
            return false;
        }
        list.Add(preset);
        Save();
        return true;
    }

    /// <summary>按名称删除预设。返回是否成功。</summary>
    public static bool DeletePreset(string name)
    {
        var list = LoadAll();
        var removed = list.RemoveAll(p => p.Name == name) > 0;
        if (removed) Save();
        return removed;
    }

    /// <summary>按名称查找预设。</summary>
    public static CustomEqPreset? Find(string name)
    {
        return LoadAll().FirstOrDefault(p => p.Name == name);
    }

    /// <summary>获取所有自定义预设名称列表。</summary>
    public static List<string> GetAllNames()
    {
        return LoadAll().Select(p => p.Name).ToList();
    }

    /// <summary>使缓存失效（下次 LoadAll 重新读文件）。</summary>
    public static void InvalidateCache()
    {
        _cache = null;
    }
}
