using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Gestures;

namespace OppoPodsManager.UI;

/// <summary>
/// 触控手势 UI 共享构建器：按 <see cref="GestureEntry"/> 动态生成左右耳手势行（多品牌兼容）。
/// 主页面 HomeView 与设备详情页 DeviceInfoView 共用，避免逻辑重复。
/// </summary>
internal static class GestureUi
{
    /// <summary>按手势条目生成一行（标签 + 可选 ComboBox）。onSet 在用户改动时回调。</summary>
    public static Avalonia.Controls.Control BuildRow(GestureEntry entry, Action<EarSide, TapKind, GestureActionKind>? onSet)
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
                onSet?.Invoke(captured.Ear, captured.Kind, kind);
        };
        grid.Children.Add(cb);
        return grid;
    }

    /// <summary>清空并按左右耳把条目注入对应宿主面板。</summary>
    public static void Rebuild(Panel? leftHost, Panel? rightHost,
        IReadOnlyList<GestureEntry>? entries, Action<EarSide, TapKind, GestureActionKind>? onSet)
    {
        leftHost?.Children.Clear();
        rightHost?.Children.Clear();
        if (entries is null)
            return;
        foreach (var entry in entries)
        {
            var host = entry.Ear == EarSide.Left ? leftHost : rightHost;
            host?.Children.Add(BuildRow(entry, onSet));
        }
    }

    public static string L(string key) => TranslationCatalog.Get(key);

    public static string LabelKey(TapKind kind) => kind switch
    {
        TapKind.Single => "DeviceInfo_SingleTap",
        TapKind.Double => "DeviceInfo_DoubleTap",
        TapKind.Triple => "DeviceInfo_TripleTap",
        TapKind.Slide => "DeviceInfo_Slide",
        TapKind.LongPress => "DeviceInfo_LongPress",
        _ => "DeviceInfo_DoubleTap",
    };
}
