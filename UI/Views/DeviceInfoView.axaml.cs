using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Gestures;
using OppoPodsManager.Assets.Localization;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 设备信息页：承载耳机触控操控卡（按设备能力动态生成手势行，多品牌兼容）与隐藏的 EQ 快捷选择器。
/// 触控耳机图案由外壳经 <see cref="TouchLeftImage"/> / <see cref="TouchRightImage"/> 注入（EarphoneImageProvider），
/// 隐藏的 CbEq 由快照驱动（原 MainWindow.CbEq 逻辑迁入此处）。
/// </summary>
public partial class DeviceInfoView : PageView
{
    // 外壳刷新耳机图案时通过这两个属性访问本页图片控件。
    public Image TouchLeftImage => DiTouchLeftImage;
    public Image TouchRightImage => DiTouchRightImage;

    // 快照重建时抑制 SelectionChanged 误触发下发。
    private bool _suppressEqSelection;
    private bool _suppressGestureSelection;

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
                RebuildGesturePanel(manager);
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

        RebuildGesturePanel(manager);
    }

    private void CbEq_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_suppressEqSelection)
            return;
        if (CbEq.SelectedItem is not string name)
            return;

        _ = CommandDispatcher?.RunAsync("EQ 预设", manager => manager.SetEqualizerByNameAsync(name, CancellationToken.None));
    }

    // ---- 触控手势面板：复用共享 GestureUi 按设备能力动态生成（多品牌兼容）----
    private void RebuildGesturePanel(IBrandManager? manager)
    {
        _suppressGestureSelection = true;
        try
        {
            GestureUi.Rebuild(GestureLeftHost, GestureRightHost, manager?.GestureEntries, OnGestureSet);
        }
        finally
        {
            _suppressGestureSelection = false;
        }
    }

    private void OnGestureSet(EarSide ear, TapKind kind, GestureActionKind action)
    {
        if (_suppressGestureSelection)
            return;
        _ = CommandDispatcher?.RunAsync("触控手势",
            m => m.SetTouchGestureAsync(ear, kind, action, CancellationToken.None));
    }
}
