using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Assets.Localization;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 设备信息页：仅保留隐藏的 EQ 快捷选择器（原 MainWindow.CbEq 逻辑迁入此处）。
/// 触控手势已迁至独立的「快捷手势」侧边栏页（GestureView）。
/// </summary>
public partial class DeviceInfoView : PageView
{
    // 快照重建时抑制 SelectionChanged 误触发下发。
    private bool _suppressEqSelection;

    public DeviceInfoView()
    {
        InitializeComponent();
        CbEq.SelectionChanged += CbEq_SelectionChanged;
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        var manager = ControlManager?.ActiveManager;
        _suppressEqSelection = true;
        try
        {
            CbEq.Items.Clear();
            if (manager is null)
            {
                CbEq.SelectedItem = null;
                return;
            }

            var presentation = manager.Presentation;
            foreach (var presetName in presentation.EqualizerPresets)
                CbEq.Items.Add(presetName);

            var selectedName = snapshot.Equalizer.PresetName
                ?? snapshot.EqualizerEntries.FirstOrDefault(entry => entry.IsSelected)?.Name;
            CbEq.SelectedItem = string.IsNullOrWhiteSpace(selectedName) ? null : selectedName;
        }
        finally
        {
            _suppressEqSelection = false;
        }
    }

    private void CbEq_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_suppressEqSelection)
            return;
        if (CbEq.SelectedItem is not string name)
            return;

        _ = CommandDispatcher?.RunAsync("EQ 预设", manager => manager.SetEqualizerByNameAsync(name, CancellationToken.None));
    }
}
