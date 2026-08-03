using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.IO;

namespace OppoPodsManager.Assets.VisualAssets;

// 解析首页与小窗口使用的耳机图片。
public enum EarphoneSlot
{
    HomeLeft,
    HomeRight,
    Case,
    SmallDual
}

public static class EarphoneImageProvider
{
    // 默认资源与原项目保持同一套官方图片。
    private static readonly IReadOnlyDictionary<EarphoneSlot, string> DefaultAssets =
        new Dictionary<EarphoneSlot, string>
        {
            [EarphoneSlot.HomeLeft] = "avares://OppoPodsManager/Assets/Oplus/Images/official_left.png",
            [EarphoneSlot.HomeRight] = "avares://OppoPodsManager/Assets/Oplus/Images/official_right.png",
            [EarphoneSlot.Case] = "avares://OppoPodsManager/Assets/Oplus/Images/official_case.png",
            [EarphoneSlot.SmallDual] = "avares://OppoPodsManager/Assets/Oplus/Images/official_dual.png"
        };

    // 保留原应用的文件名，用户迁移后无需重新选择图片。
    private static readonly IReadOnlyDictionary<EarphoneSlot, string[]> CustomNames =
        new Dictionary<EarphoneSlot, string[]>
        {
            [EarphoneSlot.HomeLeft] = ["earphone_home_left.png"],
            [EarphoneSlot.HomeRight] = ["earphone_home_right.png"],
            [EarphoneSlot.Case] = ["earphone_case.png", "earphone_home_case.png", "earphone_small_case.png"],
            [EarphoneSlot.SmallDual] = ["earphone_small_dual.png"]
        };

    // Next 项目将自定义图片保存在独立目录，避免依赖旧项目设置路径。
    private static readonly string CustomDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OppoPodsManager",
        "earphone");

    // 返回独立位图实例，避免窗口关闭时影响其他窗口持有的图片。
    public static Bitmap? GetBitmap(EarphoneSlot slot)
    {
        foreach (var fileName in CustomNames[slot])
        {
            var path = Path.Combine(CustomDirectory, fileName);
            if (!File.Exists(path))
                continue;

            try
            {
                return new Bitmap(path);
            }
            catch
            {
            }
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri(DefaultAssets[slot]));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    // 返回该槽位自定义图片的主文件路径。
    public static string GetCustomFilePath(EarphoneSlot slot)
        => Path.Combine(CustomDirectory, CustomNames[slot][0]);

    // 保存用户选择的图片并覆盖该槽位的当前图片。
    public static void SaveCustom(EarphoneSlot slot, string sourcePath)
    {
        Directory.CreateDirectory(CustomDirectory);
        File.Copy(sourcePath, GetCustomFilePath(slot), true);
    }

    // 删除该槽位的全部自定义图片并恢复默认资源。
    public static void ResetCustom(EarphoneSlot slot)
    {
        foreach (var fileName in CustomNames[slot])
        {
            var path = Path.Combine(CustomDirectory, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
