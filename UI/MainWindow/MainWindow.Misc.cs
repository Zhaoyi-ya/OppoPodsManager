using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Assets.VisualAssets;
using EarphoneSlot = OppoPodsManager.Assets.VisualAssets.EarphoneSlot;
using BatteryLevel = OppoPodsManager.Control.Core.Models.BatteryLevel;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private void UpdateTitle()
    {
        var name = GetNextWindowTitle();
        Title = name;
        var snapshot = _frontendState?.Snapshot;
        var parts = new List<string>();
        AddNextBatteryPart(parts, "L", snapshot?.LeftBattery);
        AddNextBatteryPart(parts, "R", snapshot?.RightBattery);
        AddNextBatteryPart(parts, "C", snapshot?.CaseBattery);
    }
    private string GetNextWindowTitle()
    {
        var snapshot = _frontendState?.Snapshot;
        if (snapshot?.IsConnected != true)
            return AppConst.WindowTitle;

        var custom = (_customDeviceName ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(custom)
            ? custom
            : DeviceText.DeviceName(snapshot.Identity?.DisplayName, snapshot.DeviceName, snapshot.Identity?.ModelName);
    }
    private static void AddNextBatteryPart(List<string> parts, string channel, BatteryLevel? battery)
    {
        if (battery is { } value)
            parts.Add($"{channel}:{value.Percent}%{(value.IsCharging ? "⚡" : "")}");
    }
    private void ExportFeedback(string url)
    {
        if (_feedbackExporter is null)
            return;

        var result = _feedbackExporter.ExportToDesktop(
            AppInfo.VersionLabel,
            _frontendState?.Snapshot);
        if (!result.Succeeded)
            return;

        _desktopLinks?.TryOpen(url, "反馈链接");
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
            await ShowCheckResultDialog(
                string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_FeedbackExported), result.FileName),
                LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_FeedbackTitle)));
    }

    private void RefreshEarphoneImages()
    {
        if (HomeView is not null)
        {
            ReplaceEarphoneImage(HomeView.BatteryLeftImage, EarphoneSlot.HomeLeft);
            ReplaceEarphoneImage(HomeView.BatteryRightImage, EarphoneSlot.HomeRight);
            ReplaceEarphoneImage(HomeView.BatteryCaseImage, EarphoneSlot.Case);
        }
    }
    private static void ReplaceEarphoneImage(Image image, EarphoneSlot slot)
    {
        var old = image.Source;
        image.Source = EarphoneImageProvider.GetBitmap(slot);
        if (old is Bitmap oldBitmap && !AssetHelper.IsShared(oldBitmap))
            oldBitmap.Dispose();
    }
    private void BuildEarphoneCustomUi()
    {
        foreach (var preview in _earphonePreviews.Values)
            DisposeEarphoneImage(preview);

        PersonalView.EarphoneCustomContent.Children.Clear();
        _earphonePreviews.Clear();
        foreach (var slot in new[] { EarphoneSlot.Case, EarphoneSlot.HomeLeft, EarphoneSlot.HomeRight })
        {
            var preview = new Image
            {
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
                Source = EarphoneImageProvider.GetBitmap(slot)
            };
            _earphonePreviews[slot] = preview;

            var border = new Border
            {
                Width = 56,
                Height = 56,
                CornerRadius = new CornerRadius(8),
                Background = _textPanelButtonBgBrush,
                Child = preview,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            // 点击耳机图案弹出选项菜单：选择图片 / 恢复默认
            var menu = new ContextMenu();
            var pickItem = new MenuItem
            {
                Header = LanguageManager.Instance.GetString(LanguageManager.Instance.Personal_EarphoneSelect)
            };
            pickItem.Click += async (_, _) => await PickAndSaveEarphoneImage(slot);
            var resetItem = new MenuItem
            {
                Header = LanguageManager.Instance.GetString(LanguageManager.Instance.Personal_EarphoneReset)
            };
            resetItem.Click += (_, _) =>
            {
                EarphoneImageProvider.ResetCustom(slot);
                RefreshEarphoneImages();
                BuildEarphoneCustomUi();
            };
            menu.Items.Add(pickItem);
            menu.Items.Add(resetItem);
            border.ContextMenu = menu;
            border.Tapped += (_, _) => menu.Open(border);

            PersonalView.EarphoneCustomContent.Children.Add(new StackPanel
            {
                Width = 108,
                Spacing = 6,
                Margin = new Thickness(8, 4),
                Children =
                {
                    border,
                    new TextBlock
                    {
                        Text = LanguageManager.Instance.GetString(slot switch
                        {
                            EarphoneSlot.HomeLeft => LanguageManager.Instance.Personal_EarphoneLeft,
                            EarphoneSlot.HomeRight => LanguageManager.Instance.Personal_EarphoneRight,
                            _ => LanguageManager.Instance.Personal_EarphoneCase
                        }),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = BrushGray
                    }
                }
            });
        }
    }
    private async Task PickAndSaveEarphoneImage(EarphoneSlot slot)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
            return;

        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = LanguageManager.Instance.GetString(LanguageManager.Instance.Personal_EarphonePickTitle),
            AllowMultiple = false,
            FileTypeFilter = new List<Avalonia.Platform.Storage.FilePickerFileType>
            {
                new(LanguageManager.Instance.GetString(LanguageManager.Instance.ImagePicker_FilterName))
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp" }
                }
            }
        });

        if (files is not { Count: > 0 })
            return;

        var path = files[0].Path.LocalPath;
        try
        {
            EarphoneImageProvider.SaveCustom(slot, path);
            RefreshEarphoneImages();
            BuildEarphoneCustomUi();
        }
        catch (Exception ex)
        {
            _logManager?.Error("UI", $"保存自定义耳机图案失败：{slot}", ex);
        }
    }
    private static void DisposeEarphoneImage(Image image)
    {
        if (image.Source is Bitmap bitmap && !AssetHelper.IsShared(bitmap))
            bitmap.Dispose();
        image.Source = null;
    }
    private void DisposeWindowImages()
    {
        if (HomeView is not null)
        {
            DisposeEarphoneImage(HomeView.BatteryLeftImage);
            DisposeEarphoneImage(HomeView.BatteryRightImage);
            DisposeEarphoneImage(HomeView.BatteryCaseImage);
        }
        foreach (var preview in _earphonePreviews.Values)
            DisposeEarphoneImage(preview);
        _earphonePreviews.Clear();
        PersonalView.EarphoneCustomContent.Children.Clear();
    }
}
