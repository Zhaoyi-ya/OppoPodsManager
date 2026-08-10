using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 设备信息页：承载耳机触控操控卡（WIP，暂未接协议下发）与隐藏的 EQ 快捷选择器。
/// 触控耳机图案由外壳经 <see cref="TouchLeftImage"/> / <see cref="TouchRightImage"/> 注入（EarphoneImageProvider），
/// 隐藏的 CbEq 由快照驱动（原 MainWindow.CbEq 逻辑迁入此处）。
/// </summary>
public partial class DeviceInfoView : PageView
{
    // 外壳刷新耳机图案时通过这两个属性访问本页图片控件。
    public Image TouchLeftImage => DiTouchLeftImage;
    public Image TouchRightImage => DiTouchRightImage;

    // 快照重建 CbEq 时抑制 SelectionChanged 误触发下发。
    private bool _suppressEqSelection;

    public DeviceInfoView()
    {
        InitializeComponent();
        CbEq.SelectionChanged += CbEq_SelectionChanged;
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        // 触控操控当前为 WIP（无协议下发），此处仅驱动隐藏的 EQ 快捷选择器（原 MainWindow.CbEq）。
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
