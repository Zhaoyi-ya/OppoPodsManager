using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OppoPodsManager.Control.Oppo.Models;
using EqPresetItem = OppoPodsManager.EqPresetItem;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 均衡器（EQ）页面视图：承载预设列表、自定义滑块与保存/删除逻辑。
/// 与 DeviceInfo 页的快捷下拉框 CbEq 解耦——CbEq 由 DeviceInfoView 从快照驱动，
/// 本视图仅根据 <see cref="ApplySnapshot"/> 提供的快照渲染自身控件。
/// </summary>
public partial class EqView : PageView
{
    // ---- EQ 调节 ----
    private string _eqCurrentPreset = "";
    private int _eqCurrentId; // 当前编辑的设备端预设 eqId，0=新建
    private bool _eqSuppressListEvent;
    private bool _nextEqEditing;
    private bool _synchronizingNextEq;
    private IReadOnlyList<ushort> _nextEqFrequencies = [];
    private sbyte _nextEqMinimumGain = BrandPresentation.DefaultCustomEqMinimumGain;
    private sbyte _nextEqMaximumGain = BrandPresentation.DefaultCustomEqMaximumGain;
    private readonly List<DynamicEqBand> _dynamicEqBands = [];
    private DispatcherTimer? _eqDebounceTimer;

    // 保存动态 EQ 频段对应的滑块和数值标签。
    private sealed record DynamicEqBand(int Frequency, Slider Slider, TextBlock DbLabel);

    public EqView()
    {
        InitializeComponent();
        EqSlider62.PropertyChanged += EqSlider_Changed;
        EqSlider250.PropertyChanged += EqSlider_Changed;
        EqSlider1k.PropertyChanged += EqSlider_Changed;
        EqSlider4k.PropertyChanged += EqSlider_Changed;
        EqSlider8k.PropertyChanged += EqSlider_Changed;
        EqSlider16k.PropertyChanged += EqSlider_Changed;
        LbEqBuiltinPresets.SelectionChanged += EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged += EqCustomPresets_Changed;
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        if (_nextEqEditing)
            return;

        var manager = ControlManager?.ActiveManager;
        _eqSuppressListEvent = true;
        try
        {
            LbEqBuiltinPresets.Items.Clear();
            LbEqCustomPresets.Items.Clear();

            if (!snapshot.IsConnected || manager is null)
            {
                BtnEqNew.IsVisible = false;
                EqCustomPresetPanel.IsVisible = false;
                EqSliderCard.IsVisible = false;
                BtnEqSave.IsEnabled = false;
                _eqCurrentPreset = string.Empty;
                _eqCurrentId = 0;
                _synchronizingNextEq = true;
                try
                {
                    ConfigureNextEqBands(
                        [],
                        BrandPresentation.DefaultCustomEqMinimumGain,
                        BrandPresentation.DefaultCustomEqMaximumGain);
                    SetAllEqSliders(0);
                }
                finally
                {
                    _synchronizingNextEq = false;
                }
                return;
            }

            // 新建入口仅在型号能力明确支持自定义 EQ 时显示。
            var presentation = manager.Presentation;
            var supportsCustomEq = presentation.SupportsCustomEqualizer;
            BtnEqNew.IsVisible = supportsCustomEq;
            EqCustomPresetPanel.IsVisible = supportsCustomEq;
            ConfigureNextEqBands(
                presentation.CustomEqFrequencies,
                presentation.CustomEqMinimumGain,
                presentation.CustomEqMaximumGain);

            foreach (var presetName in presentation.EqualizerPresets)
            {
                var displayName = DeviceProfileLoader.LocalizedEqName(presetName);
                LbEqBuiltinPresets.Items.Add(new EqPresetItem
                {
                    Name = presetName,
                    DisplayName = displayName,
                    IsCustom = false
                });
            }

            foreach (var entry in supportsCustomEq ? snapshot.EqualizerEntries : [])
            {
                if (string.IsNullOrWhiteSpace(entry.Name)
                    || presentation.EqualizerPresets.Contains(entry.Name))
                    continue;

                LbEqCustomPresets.Items.Add(new EqPresetItem
                {
                    Name = entry.Name,
                    // 设备端和用户自定义名称是协议数据，显示时保留原文。
                    DisplayName = string.IsNullOrWhiteSpace(entry.Name)
                        ? $"EQ {entry.Id}"
                        : entry.Name,
                    IsCustom = false,
                    EqId = entry.Id
                });
            }

            var selectedName = snapshot.Equalizer.PresetName
                ?? snapshot.EqualizerEntries.FirstOrDefault(entry => entry.IsSelected)?.Name;
            if (string.IsNullOrWhiteSpace(selectedName))
                return;

            var selectedEntry = supportsCustomEq
                ? snapshot.EqualizerEntries.FirstOrDefault(entry => entry.Name == selectedName)
                : null;
            if (selectedEntry is not null)
            {
                LbEqCustomPresets.SelectedItem = LbEqCustomPresets.Items
                    .OfType<EqPresetItem>()
                    .FirstOrDefault(item => item.Name == selectedName);
                EqSliderCard.IsVisible = true;
                BtnEqSave.IsEnabled = true;
                _synchronizingNextEq = true;
                try
                {
                    ApplyNextEqEntry(selectedEntry);
                }
                finally
                {
                    _synchronizingNextEq = false;
                }
            }
            else
            {
                LbEqBuiltinPresets.SelectedItem = LbEqBuiltinPresets.Items
                    .OfType<EqPresetItem>()
                    .FirstOrDefault(item => item.Name == selectedName);
                EqSliderCard.IsVisible = false;
                BtnEqSave.IsEnabled = false;
                ConfigureNextEqBands(
                    [],
                    presentation.CustomEqMinimumGain,
                    presentation.CustomEqMaximumGain);
            }
        }
        finally
        {
            _eqSuppressListEvent = false;
        }
    }

    internal void StopDebounceTimer() => _eqDebounceTimer?.Stop();

    private DispatcherTimer EnsureEqDebounceTimer()
    {
        if (_eqDebounceTimer != null)
            return _eqDebounceTimer;

        _eqDebounceTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _eqDebounceTimer?.Stop();
            SendCurrentCustomEq();
        });
        return _eqDebounceTimer;
    }

    private void EqSlider_Changed(object? s, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty) return;
        if (s is not Slider slider) return;

        var db = (int)Math.Round(slider.Value);
        var sign = db > 0 ? "+" : "";
        var text = $"{sign}{db}";

        if (slider == EqSlider62) EqDb62.Text = text;
        else if (slider == EqSlider250) EqDb250.Text = text;
        else if (slider == EqSlider1k) EqDb1k.Text = text;
        else if (slider == EqSlider4k) EqDb4k.Text = text;
        else if (slider == EqSlider8k) EqDb8k.Text = text;
        else if (slider == EqSlider16k) EqDb16k.Text = text;
        else
        {
            var dynamicBand = _dynamicEqBands.FirstOrDefault(band => ReferenceEquals(band.Slider, slider));
            if (dynamicBand is not null)
                dynamicBand.DbLabel.Text = text;
        }

        if (_synchronizingNextEq)
            return;

        // 防抖 150ms 后下发自定义 EQ（实时预览）。复用同一个 DispatcherTimer，避免拖动滑块时反复创建 Timer/闭包。
        var timer = EnsureEqDebounceTimer();
        timer.Stop();
        timer.Start();
    }
    private void EqBuiltinPresets_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_eqSuppressListEvent) return;
        if (LbEqBuiltinPresets.SelectedItem is not EqPresetItem item) return;

        // 交叉取消自定义列表的选中
        _eqSuppressListEvent = true;
        LbEqCustomPresets.SelectedItem = null;
        _eqSuppressListEvent = false;

        ApplyEqSelection(item);
    }
    private void EqCustomPresets_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_eqSuppressListEvent) return;
        if (LbEqCustomPresets.SelectedItem is not EqPresetItem item) return;

        // 交叉取消系统列表的选中
        _eqSuppressListEvent = true;
        LbEqBuiltinPresets.SelectedItem = null;
        _eqSuppressListEvent = false;

        ApplyEqSelection(item);
    }
    private void ApplyEqSelection(EqPresetItem item, bool sendToDevice = true)
    {
        _eqCurrentPreset = item.Name;

        var manager = ControlManager?.ActiveManager;
        if (manager is null) return;
        if (sendToDevice)
            _ = CommandDispatcher?.RunAsync("EQ 预设", active => active.SetEqualizerByNameAsync(item.Name, CancellationToken.None));

        // 内置预设：直接生效，不显示滑块
        if (!item.IsCustom && !item.IsDeviceEntry)
        {
            EqSliderCard.IsVisible = false;
            EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintSwitched), item.Name);
            return;
        }

        // 自定义/设备端预设：显示滑块编辑
        EqSliderCard.IsVisible = true;
        // 尝试加载设备保存的增益值
        var entry = FrontendState?.Snapshot.EqualizerEntries.FirstOrDefault(d => d.Name == item.Name);
        if (entry is { Gains.Count: > 0, Frequencies.Count: > 0 })
            ApplyNextEqEntry(entry);
        else
            SetAllEqSliders(0);
        _eqCurrentId = item.EqId;
        Log?.Debug("UI", $"EQ选中: name={item.Name} eqId={_eqCurrentId} isCustom={item.IsCustom} isDev={item.IsDeviceEntry}");
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintEditing), item.Name);
    }
    private void AddCustomPresetToList(string name)
    {
        // 避免重复——已有同名项则只选中不追加
        foreach (var item in LbEqCustomPresets.Items.OfType<EqPresetItem>())
            if (item.Name == name) { LbEqCustomPresets.SelectedItem = item; return; }

        var newItem = new EqPresetItem { Name = name, DisplayName = name, IsCustom = true, EqId = 0 };
        _eqSuppressListEvent = true;
        LbEqBuiltinPresets.SelectedItem = null;
        LbEqCustomPresets.Items.Add(newItem);
        LbEqCustomPresets.SelectedItem = newItem;
        _eqSuppressListEvent = false;
        // 显示均衡器
        EqSliderCard.IsVisible = true;
        BtnEqSave.IsEnabled = true;
    }

    private void SetAllEqSliders(double value)
    {
        EqSlider62.Value = value;
        EqSlider250.Value = value;
        EqSlider1k.Value = value;
        EqSlider4k.Value = value;
        EqSlider8k.Value = value;
        EqSlider16k.Value = value;
        foreach (var band in _dynamicEqBands)
            band.Slider.Value = Math.Clamp(value, band.Slider.Minimum, band.Slider.Maximum);
    }
    private void ApplyNextEqEntry(EqualizerEntrySnapshot entry)
    {
        var manager = ControlManager?.ActiveManager;
        var presentation = manager?.Presentation;
        var frequencies = presentation?.CustomEqFrequencies.ToArray() ?? [];
        if (manager is null || frequencies.Length == 0)
        {
            ConfigureNextEqBands([], entry.MinimumGain, entry.MaximumGain);
            return;
        }

        _nextEqMinimumGain = entry.MinimumGain;
        _nextEqMaximumGain = entry.MaximumGain;
        var gains = manager.AlignCustomEqualizerGains(entry);
        ConfigureNextEqBands(frequencies, _nextEqMinimumGain, _nextEqMaximumGain, gains);
    }
    private void ConfigureFixedEqSliders(sbyte minimumGain, sbyte maximumGain, IReadOnlyList<sbyte>? gains)
    {
        var sliders = new[] { EqSlider62, EqSlider250, EqSlider1k, EqSlider4k, EqSlider8k, EqSlider16k };
        for (var index = 0; index < sliders.Length; index++)
        {
            sliders[index].Minimum = minimumGain;
            sliders[index].Maximum = maximumGain;
            sliders[index].Value = gains is not null && index < gains.Count ? gains[index] : 0;
        }
    }
    private static void UpdateEqDbLabel(Slider slider, TextBlock label)
    {
        var db = (int)Math.Round(slider.Value);
        label.Text = db > 0 ? $"+{db}" : db.ToString();
    }
    private static string FormatEqFrequency(ushort frequency) => frequency >= 1000 && frequency % 1000 == 0
        ? $"{frequency / 1000}k"
        : frequency.ToString();

    private IReadOnlyList<double> ReadNextEqGains()
    {
        if (_dynamicEqBands.Count > 0)
            return _dynamicEqBands.Select(band => band.Slider.Value).ToArray();

        return new[] { EqSlider62, EqSlider250, EqSlider1k, EqSlider4k, EqSlider8k, EqSlider16k }
            .Select(slider => slider.Value)
            .ToArray();
    }
    private void SendCurrentCustomEq()
    {
        if (_eqCurrentId <= 0 || string.IsNullOrWhiteSpace(_eqCurrentPreset))
            return;
        var entry = BuildNextEqEntry((byte)_eqCurrentId, _eqCurrentPreset);
        if (entry is not null)
            _ = CommandDispatcher?.RunAsync("EQ 预览", manager => manager.PreviewCustomEqualizerAsync(entry, CancellationToken.None));
    }
    private void BtnEqCancel_Click(object? s, RoutedEventArgs e)
    {
        // 仅当前编辑自定义/设备端预设时生效
        if (string.IsNullOrEmpty(_eqCurrentPreset)) return;
        SetAllEqSliders(0);
        EqHintText.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintReset);
    }

    private async void BtnEqNew_Click(object? s, RoutedEventArgs e)
    {
        var manager = ControlManager?.ActiveManager;
        var presentation = manager?.Presentation;
        if (manager is null || presentation is null || !presentation.SupportsCustomEqualizer)
            return;
        if (Host is null)
            return;

        string? name;
        do
        {
            name = await Host.ShowPromptDialogAsync(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InputPresetName),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Personal_Custom),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName));
            if (string.IsNullOrEmpty(name)) return;
            if (manager.IsValidCustomEqualizerName(name)) break;
            await Host.ShowCheckResultDialogAsync(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName), LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidNameTitle));
        } while (true);
        _eqCurrentPreset = name;
        _eqCurrentId = 0;
        _nextEqEditing = true;
        var nextPresentation = ControlManager?.ActiveManager?.Presentation;
        if (nextPresentation is not null)
        {
            // 新建时立即按当前型号白名单创建全部频段，不能只清零原有固定滑块。
            ConfigureNextEqBands(
                nextPresentation.CustomEqFrequencies,
                nextPresentation.CustomEqMinimumGain,
                nextPresentation.CustomEqMaximumGain);
        }
        SetAllEqSliders(0);
        EqSliderCard.IsVisible = true;
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintNewPreset), name);
        LbEqBuiltinPresets.SelectedItem = null;
        LbEqCustomPresets.SelectedItem = null;

        // 立即加入自定义列表并选中
        AddCustomPresetToList(name);
    }

    private void BtnEqSave_Click(object? s, RoutedEventArgs e)
    {
        var manager = ControlManager?.ActiveManager;
        if (manager is null
            || string.IsNullOrEmpty(_eqCurrentPreset)
            || !manager.IsValidCustomEqualizerName(_eqCurrentPreset))
            return;
        var entry = BuildNextEqEntry((byte)_eqCurrentId, _eqCurrentPreset);
        if (entry is null)
            return;
        _ = SaveNextEqualizerAsync(entry);
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintSaved), _eqCurrentPreset);
    }

    private async void EqListItemDelete_Click(object? s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not string name) return;
        if (Host is null)
            return;
        if (!await Host.ShowConfirmDialogAsync(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ConfirmDelete),
                string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_DeleteConfirm), name))) return;

        var nextEntry = FrontendState?.Snapshot.EqualizerEntries.FirstOrDefault(entry => entry.Name == name);
        if (nextEntry is null)
            return;
        _ = CommandDispatcher?.RunAsync("EQ 删除", manager => manager.DeleteCustomEqualizerAsync(nextEntry, CancellationToken.None));

        if (_eqCurrentPreset == name)
        {
            _eqCurrentPreset = "";
            SetAllEqSliders(0);
        }
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintDeleted), name);
    }
    private async Task SaveNextEqualizerAsync(EqualizerEntrySnapshot entry)
    {
        try
        {
            await (CommandDispatcher?.RunAsync("EQ 保存", manager => manager.SaveCustomEqualizerAsync(entry, CancellationToken.None)) ?? Task.FromResult(false));
        }
        finally
        {
            _nextEqEditing = false;
        }
    }
    private void ConfigureNextEqBands(
        IReadOnlyList<ushort> frequencies,
        sbyte minimumGain,
        sbyte maximumGain,
        IReadOnlyList<sbyte>? gains = null)
    {
        var normalizedFrequencies = frequencies.ToArray();
        _nextEqFrequencies = normalizedFrequencies;
        _nextEqMinimumGain = minimumGain;
        _nextEqMaximumGain = maximumGain;
        var useFixedBands = normalizedFrequencies is [62, 250, 1000, 4000, 8000, 16000];

        EqFixedBandsGrid.IsVisible = useFixedBands;
        EqDynamicBandsGrid.IsVisible = !useFixedBands;
        _dynamicEqBands.Clear();
        EqDynamicBandsGrid.Children.Clear();
        EqDynamicBandsGrid.ColumnDefinitions.Clear();
        if (normalizedFrequencies.Length == 0)
            return;
        if (useFixedBands)
        {
            ConfigureFixedEqSliders(minimumGain, maximumGain, gains);
            return;
        }

        EqDynamicBandsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var scale = new Grid
        {
            Height = 180,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        scale.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        scale.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        scale.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var maximumLabel = new TextBlock { Text = $"+{maximumGain}", FontSize = 10, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Right };
        var zeroLabel = new TextBlock { Text = "0", FontSize = 10, Opacity = 0.2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var minimumLabel = new TextBlock { Text = minimumGain.ToString(), FontSize = 10, Opacity = 0.35, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(zeroLabel, 1);
        Grid.SetRow(minimumLabel, 2);
        scale.Children.Add(maximumLabel);
        scale.Children.Add(zeroLabel);
        scale.Children.Add(minimumLabel);
        EqDynamicBandsGrid.Children.Add(scale);

        for (var index = 0; index < normalizedFrequencies.Length; index++)
        {
            var frequency = normalizedFrequencies[index];
            var dbLabel = new TextBlock
            {
                Text = "0",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.5,
                Margin = new Thickness(0, 0, 0, 2)
            };
            var slider = new Slider
            {
                Width = 36,
                Height = 180,
                Orientation = Orientation.Vertical,
                Minimum = minimumGain,
                Maximum = maximumGain,
                TickFrequency = 1,
                IsSnapToTickEnabled = true,
                Value = gains is not null && index < gains.Count ? gains[index] : 0
            };
            slider.PropertyChanged += EqSlider_Changed;
            var frequencyLabel = new TextBlock
            {
                Text = FormatEqFrequency(frequency),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(0, 2, 0, 0)
            };
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(dbLabel);
            panel.Children.Add(slider);
            panel.Children.Add(frequencyLabel);
            EqDynamicBandsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(panel, index + 1);
            EqDynamicBandsGrid.Children.Add(panel);
            _dynamicEqBands.Add(new DynamicEqBand(frequency, slider, dbLabel));
            UpdateEqDbLabel(slider, dbLabel);
        }
    }

    private EqualizerEntrySnapshot? BuildNextEqEntry(byte id, string name)
    {
        var manager = ControlManager?.ActiveManager;
        if (manager is null)
            return null;

        var gains = ReadNextEqGains();
        return manager.CreateCustomEqualizerEntry(id, name, gains);
    }

    /// <summary>由外壳的功能状态应用路由至此，统一启用/禁用 EQ 相关控件。</summary>
    internal void SetControlsEnabled(bool enabled)
    {
        EqSlider62.IsEnabled = enabled;
        EqSlider250.IsEnabled = enabled;
        EqSlider1k.IsEnabled = enabled;
        EqSlider4k.IsEnabled = enabled;
        EqSlider8k.IsEnabled = enabled;
        EqSlider16k.IsEnabled = enabled;
        EqDb62.Opacity = enabled ? 0.5 : 0.2;
        EqDb250.Opacity = enabled ? 0.5 : 0.2;
        EqDb1k.Opacity = enabled ? 0.5 : 0.2;
        EqDb4k.Opacity = enabled ? 0.5 : 0.2;
        EqDb8k.Opacity = enabled ? 0.5 : 0.2;
        EqDb16k.Opacity = enabled ? 0.5 : 0.2;
        LbEqBuiltinPresets.IsEnabled = enabled;
        LbEqCustomPresets.IsEnabled = enabled;
        BtnEqNew.IsEnabled = enabled;
    }
}
