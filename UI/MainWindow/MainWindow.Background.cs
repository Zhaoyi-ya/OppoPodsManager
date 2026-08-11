using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SukiUI;
using SukiUI.Enums;
using OppoPodsManager.Assets.VisualAssets;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private void BtnBgLeft_Click(object? s, RoutedEventArgs e)
        => PersonalView.BgThumbScroller.Offset = PersonalView.BgThumbScroller.Offset.WithX(Math.Max(0, PersonalView.BgThumbScroller.Offset.X - 200));

    private void BtnBgRight_Click(object? s, RoutedEventArgs e)
        => PersonalView.BgThumbScroller.Offset = PersonalView.BgThumbScroller.Offset.WithX(
            Math.Min(PersonalView.BgThumbScroller.Extent.Width - PersonalView.BgThumbScroller.Viewport.Width,
                     PersonalView.BgThumbScroller.Offset.X + 200));

    private async Task BtnBgAdd_Click()
    {
        if (PersonalView.CbAcrylicBlur.IsChecked == true)
            return;

        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage == null) return;
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = LanguageManager.Instance.GetString(LanguageManager.Instance.ImagePicker_Title),
            FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
            {
                new(LanguageManager.Instance.GetString(LanguageManager.Instance.ImagePicker_FilterName)) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" } }
            },
            AllowMultiple = false,
        });
        if (files is { Count: > 0 })
        {
            if (_backgroundSelection.Add(files[0].Path.LocalPath))
                RefreshBgThumbs();
        }
    }
    private void SelectBackground(string key)
    {
        if (PersonalView.CbAcrylicBlur.IsChecked == true && key != "default")
            return;

        _logManager?.Debug("UI", key == "default" ? "背景: 选择默认背景" : "背景: 选择自定义背景");
        _backgroundSelection.Select(key);
        PersonalView.BgThumbDefault.Classes.Set("selected", key == "default");
        foreach (var child in PersonalView.BgThumbList.Children)
        {
            Border? img = null;
            if (child is Border b && b != PersonalView.BgThumbDefault && b != PersonalView.BgThumbAdd)
                img = b;
            else if (child is Panel p && p.Children.Count > 0)
                img = p.Children[0] as Border;
            if (img != null)
                img.Classes.Set("selected", img.Tag as string == key);
        }

        ApplySavedBackground();
    }

    private void ApplySavedBackground()
    {
        var key = _backgroundSelection.SelectedKey;
        if (key == "default" || !_backgroundImages.IsAvailable(key))
        {
            SetSukiBackgroundStyle(SukiBackgroundStyle.Bubble);
            SetBackgroundImageSource(null, "");
            _backgroundImages.ClearBackgroundCache(keepKey: null);
            BgFullImage.IsVisible = false;
            return;
        }

        SetSukiBackgroundStyle(SukiBackgroundStyle.Flat);
        var blur = Math.Clamp(ReadUiInt("BgBlur", 0), 0, 20);
        var cacheKey = _backgroundImages.BuildCacheKey(key, GetBackgroundTargetWidth(), blur);
        try
        {
            // 背景图可能损坏或被外部改动导致解码失败；加载失败时回退默认背景，
            // 避免异常冒泡到调用方（最小化到托盘恢复、切个性化页等路径）造成整窗无法加载。
            SetBackgroundImageSource(
                _backgroundImages.GetOrCreateBitmap(key, GetBackgroundTargetWidth(), blur, cacheKey),
                cacheKey);
            _backgroundImages.ClearBackgroundCache(keepKey: cacheKey);
            BgFullImage.IsVisible = true;
        }
        catch (Exception exception)
        {
            _logManager?.Error("UI", $"背景图加载失败，回退默认背景：{key}", exception);
            SetSukiBackgroundStyle(SukiBackgroundStyle.Bubble);
            SetBackgroundImageSource(null, "");
            BgFullImage.IsVisible = false;
        }
    }

    private void SetBackgroundImageSource(IImage? source, string cacheKey)
    {
        var old = _backgroundImageSource;
        if (ReferenceEquals(old, source))
            return;

        BgFullImage.Source = source;
        _backgroundImageSource = source;
    }

    private int GetBackgroundTargetWidth()
    {
        var windowWidth = Bounds.Width > 0 ? Bounds.Width : 900;
        // 限制背景处理尺寸，避免 2K/4K 图片在前台产生过大的 Skia/Avalonia/GPU 峰值。
        return Math.Clamp((int)Math.Ceiling(windowWidth * 1.1), 900, 1440);
    }

    private void SetSukiBackgroundStyle(SukiBackgroundStyle style)
    {
        if (BackgroundStyle != style)
            BackgroundStyle = style;
    }
    private void RefreshBgThumbs()
    {
        // 清除旧历史缩略图（保留默认缩略图）
        for (int i = PersonalView.BgThumbList.Children.Count - 1; i >= 0; i--)
        {
            var c = PersonalView.BgThumbList.Children[i];
            if ((c is Border b && b == PersonalView.BgThumbDefault) || c == null)
                continue;
            PersonalView.BgThumbList.Children.RemoveAt(i);
        }
        foreach (var path in _backgroundSelection.History)
        {
            var img = new Border
            {
                Width = 90, Height = 60, CornerRadius = new Avalonia.CornerRadius(8),
                Classes = { "bgThumb" }, Tag = path,
            };
            try
            {
                if (_backgroundImages.IsAvailable(path))
                    img.Background = new Avalonia.Media.ImageBrush(_backgroundImages.GetOrCreateThumbnail(path))
                    {
                        Stretch = Stretch.UniformToFill
                    };
                else
                    img.Background = Avalonia.Media.Brushes.DimGray;
            }
            catch { img.Background = Avalonia.Media.Brushes.DimGray; }
            img.PointerPressed += (_, _) => SelectBackground(path);

            // 删除按钮（悬停时出现在右上角）
            var delBtn = new Border
            {
                Width = 18, Height = 18, CornerRadius = new Avalonia.CornerRadius(9),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(200, 40, 40)),
                IsVisible = false,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 2, 0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = "✕", FontSize = 11,
                    Foreground = Avalonia.Media.Brushes.White,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            };
            delBtn.PointerPressed += (_, e) =>
            {
                e.Handled = true; // 阻止点击穿透
                if (_backgroundSelection.Remove(path))
                    RefreshBgThumbs();
            };

            var wrapper = new Panel { Width = 90, Height = 60, Cursor = img.Cursor };
            wrapper.Children.Add(img);
            wrapper.Children.Add(delBtn);
            wrapper.PointerEntered += (_, _) => delBtn.IsVisible = true;
            wrapper.PointerExited += (_, _) => delBtn.IsVisible = false;

            PersonalView.BgThumbList.Children.Add(wrapper);
        }
        SelectBackground(_backgroundSelection.SelectedKey);
    }

    private DispatcherTimer EnsureBackgroundApplyDebounceTimer()
    {
        if (_bgApplyDebounceTimer != null)
            return _bgApplyDebounceTimer;

        _bgApplyDebounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(220), DispatcherPriority.Background, (_, _) =>
        {
            _bgApplyDebounceTimer?.Stop();
            ApplySavedBackground();
        });
        return _bgApplyDebounceTimer;
    }
}
