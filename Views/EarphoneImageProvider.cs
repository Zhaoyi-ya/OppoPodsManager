using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media.Imaging;

namespace OppoPodsManager;

/// <summary>
/// 耳机图案槽位。充电盒为「首页 + 快捷卡片」共用一个设置。
/// </summary>
public enum EarphoneSlot
{
    HomeLeft,    // 首页：左耳
    HomeRight,   // 首页：右耳
    Case,        // 充电盒（首页 & 快捷卡片共用）
    SmallDual,   // 快捷卡片：双耳机
}

/// <summary>
/// 耳机图案解析：优先使用配置目录（%APPDATA%/OppoPodsManager/earphone）里的自定义图片文件，
/// 没有时才回退到编译进程序集的默认资源。
/// 自定义文件使用保留文件名，用户也可手动把图片丢进 earphone 子目录生效。
/// 兼容合并前的旧文件名，并按「新子目录 → 旧根目录」顺序回退。
/// </summary>
public static class EarphoneImageProvider
{
    private static readonly Dictionary<EarphoneSlot, string> DefaultResource = new()
    {
        [EarphoneSlot.HomeLeft]  = "avares://OppoPodsManager/Assets/official_left.png",
        [EarphoneSlot.HomeRight] = "avares://OppoPodsManager/Assets/official_right.png",
        [EarphoneSlot.Case]      = "avares://OppoPodsManager/Assets/official_case.png",
        [EarphoneSlot.SmallDual] = "avares://OppoPodsManager/Assets/official_dual.png",
    };

    // 配置目录中的保留文件名。充电盒合并后只有一个主文件，并兼容合并前的旧文件（自动回退）。
    private static readonly Dictionary<EarphoneSlot, string[]> ReservedNames = new()
    {
        [EarphoneSlot.HomeLeft]  = new[] { "earphone_home_left.png" },
        [EarphoneSlot.HomeRight] = new[] { "earphone_home_right.png" },
        [EarphoneSlot.SmallDual] = new[] { "earphone_small_dual.png" },
        [EarphoneSlot.Case]      = new[] { "earphone_case.png", "earphone_home_case.png", "earphone_small_case.png" },
    };

    // 自定义图片保存到独立的 earphone 子目录，避免污染配置根目录
    private static readonly string CustomDirectory = Path.Combine(SettingsManager.AppDataDirectory, "earphone");

    /// <summary>该槽位自定义图片在 earphone 子目录里的「主」完整路径（写入/删除目标，无论是否存在）。</summary>
    public static string GetCustomFilePath(EarphoneSlot slot)
        => Path.Combine(CustomDirectory, ReservedNames[slot][0]);

    /// <summary>该槽位是否存在任意自定义图片文件。</summary>
    public static bool HasCustom(EarphoneSlot slot)
        => ReservedNames[slot].Any(n => File.Exists(Path.Combine(CustomDirectory, n)));

    /// <summary>
    /// 取该槽位的位图：优先 earphone 子目录里的自定义文件，否则编译内默认资源。
    /// 自定义文件每次返回新实例（调用方持有）；默认资源走共享缓存。
    /// </summary>
    public static Bitmap? GetBitmap(EarphoneSlot slot)
    {
        foreach (var name in ReservedNames[slot])
        {
            var path = Path.Combine(CustomDirectory, name);
            if (File.Exists(path))
            {
                try { return new Bitmap(path); }
                catch (Exception ex)
                {
                    Log.D("UI", $"自定义耳机图加载失败 {path}: {ex.Message}");
                }
            }
        }
        return AssetHelper.LoadSharedBitmap(DefaultResource[slot]);
    }

    /// <summary>把用户选择的图片写入 earphone 子目录的主保留文件名（覆盖即生效）。</summary>
    public static void SaveCustom(EarphoneSlot slot, string sourcePath)
    {
        var dest = GetCustomFilePath(slot);
        Directory.CreateDirectory(CustomDirectory);
        File.Copy(sourcePath, dest, overwrite: true);
    }

    /// <summary>删除该槽位的所有自定义图片，恢复默认。</summary>
    public static void ResetCustom(EarphoneSlot slot)
    {
        foreach (var name in ReservedNames[slot])
        {
            var path = Path.Combine(CustomDirectory, name);
            if (File.Exists(path))
            {
                try { File.Delete(path); }
                catch (Exception ex) { Log.D("UI", $"删除自定义耳机图失败 {path}: {ex.Message}"); }
            }
        }
    }
}
