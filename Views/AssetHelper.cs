using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OppoPodsManager;

public static class AssetHelper
{
    public static WindowIcon? LoadIcon(string avaresPath)
    {
        try
        {
            var uri = new Uri(avaresPath, UriKind.Absolute);
            var stream = AssetLoader.Open(uri);
            return new WindowIcon(stream);
        }
        catch { return null; }
    }

    public static Bitmap? LoadBitmap(string avaresPath)
    {
        try
        {
            var uri = new Uri(avaresPath, UriKind.Absolute);
            var stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch { return null; }
    }
}
