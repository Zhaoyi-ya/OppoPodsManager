using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using OppoPodsManager.Localization;

namespace OppoPodsManager;

public partial class MainWindow
{
    private string _eqCurrentPreset = "";
    private int _eqCurrentId; // 当前编辑的设备端预设 eqId，0=新建
    private bool _eqSuppressListEvent;
    private int _lastDeviceEqCount = -1;
    /// <summary>进入编辑时的滑块快照，用于重置。</summary>
    private double[] _eqBackupSliders = Array.Empty<double>();

    /// <summary>滑块值变更 → 更新对应 dB 标签，触发防抖预览下发。</summary>
    private void EqSlider_Changed(object? s, Avalonia.AvaloniaPropertyChangedEventArgs e)
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

        // 防抖 150ms 后下发自定义 EQ（实时预览）。复用同一个 DispatcherTimer，避免拖动滑块时反复创建 Timer/闭包。
        var timer = EnsureEqDebounceTimer();
        timer.Stop();
        timer.Start();
    }

    /// <summary>
    /// 统一刷新主页调音下拉框 + EQ 面板预设列表（共用一套数据源）。
    /// 自动恢复当前选中项。
    /// </summary>
    private void RefreshAllEqViews()
    {
        var caps = _modelOverride != null
            ? _controller.ForceModel(_modelOverride)
            : _pods.Caps;

        // ---- 主页调音下拉框 ----
        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.Items.Clear();
        foreach (var kv in (caps.EqualizerPresets ?? new Dictionary<string, byte>()))
            CbEq.Items.Add(kv.Key);
        foreach (var e in _pods.State.DeviceEqualizers)
            if (!string.IsNullOrEmpty(e.Name) && !CbEq.Items.Contains(e.Name))
                CbEq.Items.Add(e.Name);
        // 恢复选中
        if (!string.IsNullOrEmpty(_eqCurrentPreset) && CbEq.Items.Contains(_eqCurrentPreset))
            CbEq.SelectedItem = _eqCurrentPreset;
        CbEq.SelectionChanged += CbEq_SelectionChanged;

        // ---- EQ 面板预设列表 ----
        _eqSuppressListEvent = true;
        LbEqBuiltinPresets.SelectionChanged -= EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged -= EqCustomPresets_Changed;
        LbEqBuiltinPresets.Items.Clear();
        LbEqCustomPresets.Items.Clear();

        // 左：系统预设
        foreach (var kv in (caps.EqualizerPresets ?? new Dictionary<string, byte>()))
            LbEqBuiltinPresets.Items.Add(new EqPresetItem { Name = kv.Key, DisplayName = kv.Key, IsCustom = false });

        // 右：自定义
        foreach (var e in _pods.State.DeviceEqualizers)
        {
            if (!string.IsNullOrEmpty(e.Name) && !(caps.EqualizerPresets ?? new Dictionary<string, byte>()).ContainsKey(e.Name))
                LbEqCustomPresets.Items.Add(new EqPresetItem { Name = e.Name, DisplayName = e.Name, IsCustom = false, EqId = e.Id });
        }

        // 恢复选中项
        if (!string.IsNullOrEmpty(_eqCurrentPreset))
        {
            SyncCbEqToPanel(_eqCurrentPreset);
            // 如果是自定义/设备端预设，显示均衡器滑块并加载已保存的增益值（不重复发送命令）
            var selItem = LbEqBuiltinPresets.SelectedItem as EqPresetItem
                       ?? LbEqCustomPresets.SelectedItem as EqPresetItem;
            if (selItem is { IsDeviceEntry: true } or { IsCustom: true })
                ApplyEqSelection(selItem, sendToDevice: false);
        }

        LbEqBuiltinPresets.SelectionChanged += EqBuiltinPresets_Changed;
        LbEqCustomPresets.SelectionChanged += EqCustomPresets_Changed;
        _eqSuppressListEvent = false;

        // 保存后重新获取当前预设的 eqId
        if (!string.IsNullOrEmpty(_eqCurrentPreset))
        {
            var entry = _pods.State.DeviceEqualizers.FirstOrDefault(e => e.Name == _eqCurrentPreset);
            if (entry != null) _eqCurrentId = entry.Id;
        }

        // 仅设备明确支持自定义 EQ 时显示新建入口；避免仅支持内置 EQ 的设备进入伪保存流程。
        var maxCustom = caps.CustomEqMaxPresets > 0 ? caps.CustomEqMaxPresets : 3;
        BtnEqNew.IsVisible = caps.HasCustomEq && _pods.State.DeviceEqualizers.Count < maxCustom;
        if (!caps.HasCustomEq)
        {
            EqSliderCard.IsVisible = false;
            BtnEqSave.IsEnabled = false;
        }
    }

    // 兼容旧方法（均转接到统一入口）
    private void RefreshEqPresetList() => RefreshAllEqViews();
    private void RefreshMainEqCombo() => RefreshAllEqViews();

    /// <summary>系统预设选中 → 发送切换、隐藏滑块。</summary>
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

    /// <summary>自定义预设选中 → 交叉取消系统列表的选中。</summary>
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

    /// <summary>预设选中 → 内置发送切换、隐藏滑块；自定义/设备端展开编辑。</summary>
    private void ApplyEqSelection(EqPresetItem item, bool sendToDevice = true)
    {
        _eqCurrentPreset = item.Name;

        if (_pods.IsConnected && sendToDevice)
        {
            Log.D("UI", $"EQ面板: 切换预设 -> {item.Name}");
            _controller.SetEqualizer(item.Name);
        }

        // 内置预设：直接生效，不显示滑块
        if (!item.IsCustom && !item.IsDeviceEntry)
        {
            EqSliderCard.IsVisible = false;
            EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintSwitched), item.Name);
            // 同步主页调音下拉框（抑制事件避免循环）
            CbEq.SelectionChanged -= CbEq_SelectionChanged;
            CbEq.SelectedItem = item.Name;
            CbEq.SelectionChanged += CbEq_SelectionChanged;
            return;
        }

        // 自定义/设备端预设：显示滑块编辑
        EqSliderCard.IsVisible = true;
        // 尝试加载设备保存的增益值
        var entry = _pods.State.DeviceEqualizers.FirstOrDefault(d => d.Name == item.Name);
        if (entry is { Gains.Count: > 0, Frequencies.Count: > 0 })
        {
            var freqMap = new Dictionary<int, Slider>
            {
                { 62, EqSlider62 }, { 250, EqSlider250 }, { 1000, EqSlider1k },
                { 4000, EqSlider4k }, { 8000, EqSlider8k }, { 16000, EqSlider16k },
            };
            for (int i = 0; i < entry.Frequencies.Count; i++)
                if (freqMap.TryGetValue(entry.Frequencies[i], out var sld))
                    sld.Value = entry.Gains[i];
        }
        else SetAllEqSliders(0);
        SnapshotSliders();
        _eqCurrentId = item.EqId;
        Log.D("UI", $"EQ选中: name={item.Name} eqId={_eqCurrentId} isCustom={item.IsCustom} isDev={item.IsDeviceEntry}");
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintEditing), item.Name);
        // 同步主页调音下拉框（抑制事件避免循环）
        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.SelectedItem = item.Name;
        CbEq.SelectionChanged += CbEq_SelectionChanged;
    }

    // ---- 辅助 ----

    /// <summary>双向同步：将 EQ 面板的选中状态同步到主页调音下拉框。</summary>
    private void SyncCbEqToPanel(string name)
    {
        _eqSuppressListEvent = true;
        // 先在系统预设列表里找
        foreach (var item in LbEqBuiltinPresets.Items.OfType<EqPresetItem>())
        {
            if (item.Name == name) { LbEqBuiltinPresets.SelectedItem = item; LbEqCustomPresets.SelectedItem = null; _eqSuppressListEvent = false; return; }
        }
        // 再在自定义列表里找
        foreach (var item in LbEqCustomPresets.Items.OfType<EqPresetItem>())
        {
            if (item.Name == name) { LbEqCustomPresets.SelectedItem = item; LbEqBuiltinPresets.SelectedItem = null; _eqSuppressListEvent = false; return; }
        }
        _eqSuppressListEvent = false;
    }

    private bool IsBuiltinPreset(string name) =>
        (_pods.Caps.EqualizerPresets ?? new Dictionary<string, byte>()).ContainsKey(name);

    private static bool IsValidEqName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\u4e00-\u9fa5a-zA-Z0-9]+$");
    }

    /// <summary>新建/保存后立即在自定义列表中追加并选中，不等设备响应。</summary>
    private void AddCustomPresetToList(string name)
    {
        // 避免重复——已有同名项则只选中不追加
        foreach (var item in LbEqCustomPresets.Items.OfType<EqPresetItem>())
            if (item.Name == name) { LbEqCustomPresets.SelectedItem = item; return; }

        var newItem = new EqPresetItem { Name = name, IsCustom = true, EqId = 0 };
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
    }

    /// <summary>将 6 段 UI 滑块值映射到设备频率数组，未对应 UI 的频段填 0。</summary>
    private int[] SliderToGains()
    {
        var freqSliders = new Dictionary<int, double>
        {
            { 62, EqSlider62.Value },
            { 250, EqSlider250.Value },
            { 1000, EqSlider1k.Value },
            { 4000, EqSlider4k.Value },
            { 8000, EqSlider8k.Value },
            { 16000, EqSlider16k.Value },
        };
        IReadOnlyList<int> freqs = _pods.Caps.CustomEqFrequencies;
        // 如果能力表无频率，直接用 UI 硬编码的 6 段兜底
        if (freqs.Count == 0) freqs = new int[] { 62, 250, 1000, 4000, 8000, 16000 };
        var gains = new int[freqs.Count];
        for (int i = 0; i < freqs.Count; i++)
            gains[i] = freqSliders.TryGetValue(freqs[i], out var v) ? (int)Math.Round(v) : 0;
        return gains;
    }

    /// <summary>向设备发送当前 UI 滑块值作为自定义 EQ 预览/更新。</summary>
    private void SendCurrentCustomEq()
    {
        if (!_pods.IsConnected || _pods.Caps.CustomEqFrequencies.Count == 0) return;
        if (_eqCurrentId <= 0 || string.IsNullOrWhiteSpace(_eqCurrentPreset)) return;
        var gains = SliderToGains();
        _controller.UpdateCustomEqualizer((byte)_eqCurrentId, gains, _eqCurrentPreset);
    }

    // ---- 按钮操作 ----

    private void BtnEqCancel_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // 仅当前编辑自定义/设备端预设时生效
        if (string.IsNullOrEmpty(_eqCurrentPreset)) return;
        SetAllEqSliders(0);
        SnapshotSliders();
        EqHintText.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintReset);
    }

    private async void BtnEqNew_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_pods.Caps.HasCustomEq) return;

        string? name;
        do
        {
            name = await ShowPromptDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InputPresetName),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Personal_Custom),
            LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName));
            if (string.IsNullOrEmpty(name)) return;
            if (IsValidEqName(name)) break;
            await ShowCheckResultDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName), LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidNameTitle));
        } while (true);
        _eqCurrentPreset = name;
        _eqCurrentId = 0;
        SetAllEqSliders(0);
        SnapshotSliders();
        EqSliderCard.IsVisible = true;
        BtnEqSave.IsEnabled = true;
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintNewPreset), name);
        LbEqBuiltinPresets.SelectedItem = null;
        LbEqCustomPresets.SelectedItem = null;

        // 立即加入自定义列表并选中
        AddCustomPresetToList(name);
    }

    private void BtnEqSave_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_pods.Caps.HasCustomEq) return;
        if (string.IsNullOrEmpty(_eqCurrentPreset)) return;
        if (!IsValidEqName(_eqCurrentPreset))
        {
            EqHintText.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InvalidName);
            return;
        }
        Log.D("UI", $"EQ保存: name={_eqCurrentPreset} eqId={_eqCurrentId}");
        // 编辑已有预设：先删旧再新建
        if (_eqCurrentId != 0 && _pods.IsConnected)
            _controller.DeleteEqualizer(_eqCurrentId);
        DoSaveEqPreset(_eqCurrentPreset, 0);
        // 保存后重置 eqId，等设备响应后由 RefreshAllEqViews 重新赋值，避免同名取到旧 id
        _eqCurrentId = 0;
        SnapshotSliders();

        // 立即添加到自定义列表并选中，不等设备异步响应
        AddCustomPresetToList(_eqCurrentPreset);
    }

    private void SnapshotSliders()
    {
        _eqBackupSliders = new[] { EqSlider62.Value, EqSlider250.Value, EqSlider1k.Value, EqSlider4k.Value, EqSlider8k.Value, EqSlider16k.Value };
    }

    private async void EqListItemDelete_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not string name) return;
        if (!await ShowConfirmDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ConfirmDelete),
                string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_DeleteConfirm), name))) return;

        // 仅设备端预设可删除，内置预设忽略
        var devEntry = _pods.State.DeviceEqualizers.FirstOrDefault(ev => ev.Name == name);
        if (devEntry != null)
        {
            _controller.DeleteEqualizer(devEntry.Id);
            // 删除 ACK 成功后会立即更新本地列表并触发 OnStateChanged。
        }
        else
        {
            Log.D("UI", $"EQ面板: 内置预设「{name}」不可删除，已忽略");
            return;
        }

        if (_eqCurrentPreset == name)
        {
            _eqCurrentPreset = "";
            SetAllEqSliders(0);
        }
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintDeleted), name);
    }

    private void DoSaveEqPreset(string name, int eqId = 0)
    {
        _ = eqId;
        _eqCurrentPreset = name;
        if (_pods.IsConnected)
            _controller.SetCustomEqualizer(SliderToGains(), name);
        EqHintText.Text = string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Eq_HintSaved), name);
    }
}
