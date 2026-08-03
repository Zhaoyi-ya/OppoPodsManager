using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OppoPodsManager.Assets.VisualAssets;

public static class AssetHelper
{
    private static readonly Dictionary<string, Bitmap?> SharedBitmaps = new(StringComparer.OrdinalIgnoreCase);

    public static WindowIcon? LoadIcon(string avaresPath)
    {
        try
        {
            var uri = new Uri(avaresPath, UriKind.Absolute);
            using var stream = AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch { return null; }
    }

    public static Bitmap? LoadBitmap(string avaresPath)
    {
        try
        {
            var uri = new Uri(avaresPath, UriKind.Absolute);
            using var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch { return null; }
    }

    public static Bitmap? LoadSharedBitmap(string avaresPath)
    {
        if (SharedBitmaps.TryGetValue(avaresPath, out var cached))
            return cached;

        var bitmap = LoadBitmap(avaresPath);
        SharedBitmaps[avaresPath] = bitmap;
        return bitmap;
    }

    /// <summary>该位图是否为共享缓存中的嵌入资源（引用相等判断，用于安全地避免误释放）。</summary>
    public static bool IsShared(Bitmap? bmp)
        => bmp != null && SharedBitmaps.ContainsValue(bmp);
}
