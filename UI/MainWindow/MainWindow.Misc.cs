using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Assets.VisualAssets;
using EarphoneSlot = OppoPodsManager.Assets.VisualAssets.EarphoneSlot;
using BatteryLevel = OppoPodsManager.Control.Oppo.Models.BatteryLevel;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private void UpdateTitle()
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
        foreach (var slot in new[] { EarphoneSlot.Case, EarphoneSlot.HomeLeft, EarphoneSlot.HomeRight, EarphoneSlot.SmallDual })
        {
            var preview = new Image
            {
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
                Source = EarphoneImageProvider.GetBitmap(slot)
            };
            _earphonePreviews[slot] = preview;
            PersonalView.EarphoneCustomContent.Children.Add(new StackPanel
            {
                Width = 108,
                Spacing = 6,
                Margin = new Thickness(8, 4),
                Children =
                {
                    new Border
                    {
                        Width = 56,
                        Height = 56,
                        CornerRadius = new CornerRadius(8),
                        Background = _textPanelButtonBgBrush,
                        Child = preview
                    },
                    new TextBlock
                    {
                        Text = LanguageManager.Instance.GetString(slot switch
                        {
                            EarphoneSlot.HomeLeft => LanguageManager.Instance.Personal_EarphoneLeft,
                            EarphoneSlot.HomeRight => LanguageManager.Instance.Personal_EarphoneRight,
                            EarphoneSlot.SmallDual => LanguageManager.Instance.Personal_EarphoneDual,
                            _ => LanguageManager.Instance.Personal_EarphoneCase
                        }),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = BrushGray
                    }
                }
            });
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
