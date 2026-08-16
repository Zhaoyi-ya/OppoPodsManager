using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Gestures;

namespace OppoPodsManager.UI;

/// <summary>
/// 触控手势 UI 共享构建器：按 <see cref="GestureEntry"/> 动态生成左右耳手势行（多品牌兼容）。
/// 主页面 HomeView 与设备详情页 DeviceInfoView 共用，避免逻辑重复。
/// 每行归属于一个「控制源」（主触控区 / 柄），同耳列内按源插入分组标题。
/// </summary>
internal static class GestureUi
{
    /// <summary>按手势条目生成一行（标签 + 可选 ComboBox）。onSet 在用户改动时回调，携带控制源。</summary>
    public static Avalonia.Controls.Control BuildRow(GestureEntry entry, Action<EarSide, GestureSource, TapKind, GestureActionKind>? onSet)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("38,*"),
            Margin = new Thickness(0, 2),
        };
        grid.Children.Add(new TextBlock
        {
            Text = L(LabelKey(entry.Kind)),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
            FontSize = 12,
        });

        if (!entry.IsConfigurable)
        {
            var readOnly = new TextBlock
            {
                Text = L(GestureDisplay.KeyFor(entry.Current)),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
            };
            Grid.SetColumn(readOnly, 1);
            grid.Children.Add(readOnly);
            return grid;
        }

        var cb = new ComboBox
        {
            Height = 26,
            FontSize = 13,
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(cb, 1);
        foreach (var opt in entry.Options)
            cb.Items.Add(new ComboBoxItem { Content = L(opt.DisplayKey), Tag = opt.Kind });

        var idx = entry.Options.ToList().FindIndex(o => o.Kind == entry.Current);
        cb.SelectedIndex = idx < 0 ? 0 : idx;

        var captured = entry;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is ComboBoxItem item && item.Tag is GestureActionKind kind)
                onSet?.Invoke(captured.Ear, captured.Source, captured.Kind, kind);
        };
        grid.Children.Add(cb);
        return grid;
    }

    /// <summary>清空并按左右耳把条目注入对应宿主面板；每组 (耳, 控制源) 的第一行前插入源分组标题。</summary>
    public static void Rebuild(Panel? leftHost, Panel? rightHost,
        IReadOnlyList<GestureEntry>? entries, Action<EarSide, GestureSource, TapKind, GestureActionKind>? onSet)
    {
        leftHost?.Children.Clear();
        rightHost?.Children.Clear();
        if (entries is null)
            return;

        GestureSource? lastLeftSource = null;
        GestureSource? lastRightSource = null;
        foreach (var entry in entries)
        {
            var isLeft = entry.Ear == EarSide.Left;
            var host = isLeft ? leftHost : rightHost;
            if (host is null)
                continue;

            var lastSource = isLeft ? lastLeftSource : lastRightSource;
            if (lastSource != entry.Source)
            {
                host.Children.Add(BuildSourceHeader(entry.Source));
                if (isLeft)
                    lastLeftSource = entry.Source;
                else
                    lastRightSource = entry.Source;
            }

            host.Children.Add(BuildRow(entry, onSet));
        }
    }

    /// <summary>控制源分组标题（如「主触控区」「柄」），用于在左右耳列内分隔不同物理输入。</summary>
    private static Avalonia.Controls.Control BuildSourceHeader(GestureSource source) => new TextBlock
    {
        Text = L(SourceLabelKey(source)),
        FontSize = 11,
        FontWeight = FontWeight.SemiBold,
        Opacity = 0.5,
        Margin = new Thickness(0, 8, 0, 0),
    };

    public static string L(string key) => TranslationCatalog.Get(key);

    public static string LabelKey(TapKind kind) => kind switch
    {
        TapKind.Single => "DeviceInfo_SingleTap",
        TapKind.Double => "DeviceInfo_DoubleTap",
        TapKind.Triple => "DeviceInfo_TripleTap",
        TapKind.Slide => "DeviceInfo_Slide",
        TapKind.LongPress => "DeviceInfo_LongPress",
        TapKind.Press => "DeviceInfo_PressTap",
        _ => "DeviceInfo_DoubleTap",
    };

    public static string SourceLabelKey(GestureSource source) => source switch
    {
        GestureSource.Touch => "Gesture_Source_Touch",
        GestureSource.Stem => "Gesture_Source_Stem",
        _ => "Gesture_Source_Touch",
    };
}
