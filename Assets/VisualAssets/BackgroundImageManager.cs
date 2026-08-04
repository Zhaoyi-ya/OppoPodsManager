using Avalonia.Media.Imaging;
using SkiaSharp;

namespace OppoPodsManager.Assets.VisualAssets;

// 管理自定义背景文件、缩略图缓存和背景图像处理。
public sealed class BackgroundImageManager : IDisposable
{
    private const int MaximumHistoryCount = 10;
    private static readonly string ManagedDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OppoPodsManager",
        "Background");

    private readonly Dictionary<string, Bitmap> _backgroundCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Bitmap> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);

    // 构造包含文件版本、目标宽度和模糊参数的稳定缓存键。
    public string BuildCacheKey(string path, int targetWidth, int blur)
        => $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|w={targetWidth}|b={blur}";

    // 判断背景文件是否仍然存在，避免界面层直接访问文件系统。
    public bool IsAvailable(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    // 获取或创建指定尺寸和模糊程度的背景位图。
    public Bitmap GetOrCreateBitmap(string path, int targetWidth, int blur, string cacheKey)
    {
        if (_backgroundCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var bitmap = LoadBitmap(path, targetWidth, blur);
        _backgroundCache[cacheKey] = bitmap;
        return bitmap;
    }

    // 获取或创建背景缩略图。
    public Bitmap GetOrCreateThumbnail(string path)
    {
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|thumb=128";
        if (_thumbnailCache.TryGetValue(key, out var cached))
            return cached;

        RemoveThumbnail(path);
        using var stream = File.OpenRead(path);
        var thumbnail = Bitmap.DecodeToWidth(stream, 128, BitmapInterpolationMode.HighQuality);
        _thumbnailCache[key] = thumbnail;
        return thumbnail;
    }

    // 清理不再使用的背景位图，并保留当前缓存项。
    public void ClearBackgroundCache(string? keepKey)
    {
        foreach (var (key, bitmap) in _backgroundCache.ToList())
        {
            if (key == keepKey)
                continue;

            bitmap.Dispose();
            _backgroundCache.Remove(key);
        }
    }

    // 清理全部背景缩略图缓存。
    public void ClearThumbnailCache()
    {
        foreach (var bitmap in _thumbnailCache.Values)
            bitmap.Dispose();
        _thumbnailCache.Clear();
    }

    // 移除指定文件对应的缩略图缓存。
    public void RemoveThumbnail(string path)
    {
        foreach (var (key, bitmap) in _thumbnailCache.ToList())
        {
            if (!key.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase))
                continue;

            bitmap.Dispose();
            _thumbnailCache.Remove(key);
        }
    }

    // 将用户选择的背景复制到应用管理目录，避免源文件被移动后失效。
    public string CopyToManagedFile(string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(ManagedDirectory);
            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".png";

            var destination = Path.Combine(ManagedDirectory, $"{Guid.NewGuid():N}{extension}");
            File.Copy(sourcePath, destination, overwrite: true);
            return destination;
        }
        catch
        {
            return sourcePath;
        }
    }

    // 添加背景历史并清理超出数量限制的托管文件和缩略图。
    public IReadOnlyList<string> AddToHistory(IReadOnlyList<string> history, string sourcePath)
    {
        var managedPath = CopyToManagedFile(sourcePath);
        var updated = history
            .Where(path => !string.Equals(path, managedPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        updated.Insert(0, managedPath);

        while (updated.Count > MaximumHistoryCount)
        {
            var removed = updated[^1];
            updated.RemoveAt(updated.Count - 1);
            RemoveThumbnail(removed);
            DeleteManagedFile(removed);
        }

        return updated;
    }

    // 删除背景历史并清理对应的缓存和托管文件。
    public IReadOnlyList<string> RemoveFromHistory(IReadOnlyList<string> history, string path)
    {
        var updated = history
            .Where(item => !string.Equals(item, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (updated.Count == history.Count)
            return updated;

        RemoveThumbnail(path);
        DeleteManagedFile(path);
        return updated;
    }

    // 删除应用管理目录内的背景文件，阻止误删用户原始文件。
    public void DeleteManagedFile(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var managedRoot = Path.GetFullPath(ManagedDirectory);
            if (fullPath.StartsWith(managedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
        }
    }

    // 使用与原项目一致的尺寸缩放和 Skia 模糊流程加载背景图。
    public static Bitmap LoadShared(string path, int targetWidth, int blur)
        => LoadBitmap(path, targetWidth, blur);

    public void Dispose()
    {
        ClearBackgroundCache(null);
        ClearThumbnailCache();
    }

    // 根据模糊参数选择普通缩放或 Skia 模糊加载路径。
    private static Bitmap LoadBitmap(string path, int targetWidth, int blur)
    {
        if (blur <= 0)
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.HighQuality);
        }

        return LoadBlurredBitmap(path, targetWidth, blur);
    }

    // 使用 Skia 缩放并施加高斯模糊后转换为 Avalonia 位图。
    private static Bitmap LoadBlurredBitmap(string path, int targetWidth, int blur)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null)
            return new Bitmap(path);

        var sourceInfo = codec.Info;
        var scale = targetWidth / (double)Math.Max(1, sourceInfo.Width);
        var targetHeight = Math.Max(1, (int)Math.Round(sourceInfo.Height * scale));
        var scaledSize = codec.GetScaledDimensions((float)Math.Min(1.0, scale));
        if (scaledSize.Width <= 0 || scaledSize.Height <= 0)
            scaledSize = new SKSizeI(targetWidth, targetHeight);

        var decodeInfo = new SKImageInfo(scaledSize.Width, scaledSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var source = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, source.GetPixels());
        if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
            return new Bitmap(path);

        var outputInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(outputInfo);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(blur, blur)
        };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using var sourceImage = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImage, new SKRect(0, 0, targetWidth, targetHeight), sampling, paint);
        canvas.Flush();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 85);
        using var stream = data.AsStream();
        return new Bitmap(stream);
    }
}
