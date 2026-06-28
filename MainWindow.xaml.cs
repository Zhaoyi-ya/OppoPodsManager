using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace OppoPodsWPF;

// 注册表/资源路径常量
file static class AppConst
{
    public const string RegBase = @"Software\OppoPodsWin";
    public const string RegRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RegRunName = "OppoPodsWin";
    public const string IconConnected = "Assets/tuopan.ico";
    public const string IconDisconnected = "Assets/tuopandis.png";
}

// 从嵌入资源加载图标（支持单文件打包）
file static class AssetHelper
{
    public static System.Drawing.Icon LoadIcon(string path)
    {
        var uri = new Uri($"pack://application:,,,/{path}", UriKind.Absolute);
        var stream = Application.GetResourceStream(uri)?.Stream;
        return stream != null ? new System.Drawing.Icon(stream) : System.Drawing.SystemIcons.Application;
    }
}

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly RfcommService _rfcomm = new();
    private CancellationTokenSource? _pollCts;
    private string _ancMain = "", _ancLevel = "";
    private DateTime _ancUserSetAt = DateTime.MinValue;  // 用户手动切换 ANC 的时间戳，防止轮询覆盖
    private readonly Forms.NotifyIcon _trayIcon = new();
    private System.Drawing.Icon _iconConnected, _iconDisconnected;
    private bool _realClose;
    private string? _modelOverride;   // null=自动检测, 非null=强制型号
    private bool _gameModeCompat;     // 游戏模式兼容实现
    private bool _wasConnected;        // 追踪连接状态，首次连上时弹提示
    private bool _lowBatteryAlerted;   // 低电量提醒已触发过，防止重复弹
    private bool _criticalBatteryAlerted; // 极低电量提醒
    private List<string> _allModelNames = new();  // 设备型号完整列表，用于搜索过滤

    public MainWindow()
    {
        InitializeComponent();

        // 主题：默认跟随系统
        var themeIndex = (int?)Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\OppoPodsWin")?.GetValue("Theme") ?? 0;
        ApplyTheme(themeIndex);
        CbTheme.SelectedIndex = themeIndex;

        // 开机自启时最小化启动
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--minimized"))
        {
            WindowState = WindowState.Minimized;
            Visibility = Visibility.Hidden;
        }

        // 固定在屏幕右下角

        // 固定在屏幕右下角
        Loaded += (_, _) =>
        {
            var sw = SystemParameters.WorkArea.Width;
            var sh = SystemParameters.WorkArea.Height;
            Left = sw - Width - 16;
            Top = sh - Height - 16;
        };

        // 托盘图标：左键切换显示/隐藏，右键功能菜单
        _iconConnected = AssetHelper.LoadIcon(AppConst.IconConnected);

        // 断开图标从嵌入资源加载
        var discUri = new Uri($"pack://application:,,,/{AppConst.IconDisconnected}", UriKind.Absolute);
        var discStream = Application.GetResourceStream(discUri)?.Stream;
        if (discStream != null)
        {
            using var bmp = new System.Drawing.Bitmap(discStream);
            _iconDisconnected = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        else
            _iconDisconnected = _iconConnected;
        _trayIcon.Icon = _iconDisconnected;
        _trayIcon.Text = "OPPO Pods";
        _trayIcon.Visible = true;
        _trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleFromTray();
        else if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
        };

        // 先填充默认 EQ 列表，连接成功后会根据设备型号更新
        foreach (var kv in _rfcomm.Caps.EqPresets) CbEq.Items.Add(kv.Key);
        CbTray.IsChecked = ReadRegBool(AppConst.RegBase, "TrayEnabled");
        CbAuto.IsChecked = ReadRegBool(AppConst.RegRun, AppConst.RegRunName);

        // 设备型号选择（从 JSON 动态加载）
        _allModelNames = DeviceCapabilities.GetModelNames();
        CbModel.Items.Add("自动检测");
        foreach (var name in _allModelNames)
            CbModel.Items.Add(name);
        _modelOverride = ReadRegStr(AppConst.RegBase, "ModelOverride");
        CbModel.SelectedItem = _modelOverride ?? "自动检测";

        // 游戏模式实现
        _gameModeCompat = ReadRegBool(AppConst.RegBase, "GameModeCompat");
        CbGameMode.SelectedIndex = _gameModeCompat ? 1 : 0;

        // 自定义设备名称
        var customName = ReadRegStr(AppConst.RegBase, "CustomName");
        TbCustomName.Text = customName ?? "";
        UpdateTitle();

        _rfcomm.StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        Closed += (_, _) => { _pollCts?.Cancel(); _rfcomm.Dispose(); _trayIcon.Dispose(); };
        _ = ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        while (!_realClose)
        {
            await _rfcomm.ConnectAsync();
            if (_rfcomm.IsConnected)
        {
                _pollCts = new CancellationTokenSource();
                await _rfcomm.PollAsync(_pollCts.Token);
        }
            // 连接失败或断连后反馈状态，等 5 秒重试
            Dispatcher.Invoke(() => OnStateChanged());
            if (!_realClose) await Task.Delay(5000);
        }
    }

    private void OnStateChanged() => Dispatcher.Invoke(() =>
    {
        var s = _rfcomm.State;
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _rfcomm.Caps;

        // 连接状态栏
        if (s.Connected)
        {
            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
            StatusText.Text = $"已连接 — {caps.ModelName}";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0xCC, 0x88));
            BtnReconnect.Visibility = Visibility.Collapsed;
            try { _trayIcon.Icon = _iconConnected; } catch { }

            // 首次连上且已获取到电量时弹出提示
            if (!_wasConnected && s.Battery.Count > 0)
        {
                _wasConnected = true;
                _ = ToastWindow.ShowAsync(s, caps.ModelName);
        }
        }
        else
        {
            var wasConnected = _wasConnected;
            _wasConnected = false;
            _lowBatteryAlerted = false;
            _criticalBatteryAlerted = false;

            StatusDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55));
            var err = _rfcomm.LastError;
            StatusText.Text = err ?? "未连接";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x88, 0x88));
            BtnReconnect.Visibility = Visibility.Visible;
            try { _trayIcon.Icon = _iconDisconnected; } catch { }

            // 从已连接变为未连接时弹断连提示
            if (wasConnected)
                _ = ToastWindow.ShowDisconnectedAsync(caps.ModelName);

            ResetUi();
            return;
        }

        // 首次连接成功时刷新 EQ 列表（取决于设备型号）
        if (CbEq.Items.Count == 0 || (CbEq.Items.Count > 0 && CbEq.Items[0] is string first && !caps.EqPresets.ContainsKey(first)))
        {
            CbEq.SelectionChanged -= CbEq_SelectionChanged;
            CbEq.Items.Clear();
            foreach (var kv in caps.EqPresets) CbEq.Items.Add(kv.Key);
            CbEq.SelectionChanged += CbEq_SelectionChanged;
        }

        // 入盒 = 在充电
        var batL = MergeCharge(s.Battery.GetValueOrDefault("L"), s.WearingL);
        var batR = MergeCharge(s.Battery.GetValueOrDefault("R"), s.WearingR);
        SetBat(LeftBar, LeftLabel, batL);
        SetBat(RightBar, RightLabel, batR);
        SetBat(CaseBar, CaseLabel, s.Battery.GetValueOrDefault("C"));

        // 低电量检测：左或右耳 ≤20% 时弹提醒（每次连接周期仅一次）
        if (!_lowBatteryAlerted)
        {
            if ((batL is { } l && l.Lvl <= 20) || (batR is { } r && r.Lvl <= 20))
        {
                _lowBatteryAlerted = true;
                _ = ToastWindow.ShowLowBatteryAsync(s, caps.ModelName);
        }
        }

        // 极低电量检测：左或右耳 ≤10% 时弹红色警告（每次连接周期仅一次）
        if (!_criticalBatteryAlerted)
        {
            if ((batL is { } l && l.Lvl <= 10) || (batR is { } r && r.Lvl <= 10))
        {
                _criticalBatteryAlerted = true;
                _ = ToastWindow.ShowCriticalBatteryAsync(s, caps.ModelName);
        }
        }

        // 托盘悬浮提示
        var parts = new List<string>();
        if (batL is { } lb) parts.Add($"L:{lb.Lvl}%{(lb.Chg ? "⚡" : "")}");
        if (batR is { } rb) parts.Add($"R:{rb.Lvl}%{(rb.Chg ? "⚡" : "")}");
        if (s.Battery.GetValueOrDefault("C") is { } cb) parts.Add($"C:{cb.Level}%{(cb.Charging ? "⚡" : "")}");
        try { _trayIcon.Text = parts.Count > 0 ? $"{caps.ModelName}\n{string.Join(" ", parts)}" : caps.ModelName; } catch { }

        // 佩戴状态
        var wearList = new List<string>();
        if (!string.IsNullOrEmpty(s.WearingL)) wearList.Add($"左耳{s.WearingL}");
        if (!string.IsNullOrEmpty(s.WearingR)) wearList.Add($"右耳{s.WearingR}");
        WearStatus.Text = string.Join("  ", wearList);

        // ANC 模式：如果用户在3秒内手动切过，忽略设备回读（防轮询覆盖）
        if (s.AncMode is not "?" && (DateTime.Now - _ancUserSetAt).TotalSeconds > 3)
        {
            if (s.AncMode is "Off" or "Adaptive" or "Transparency")
        { _ancMain = s.AncMode; AncSub.Visibility = Visibility.Collapsed; }
        else if (s.AncMode is "Smart" or "Light" or "Medium" or "Deep")
        {
                _ancMain = "Smart";
                // 仅在首次同步时设置子模式（_ancLevel 为空），之后由用户手动切换决定
                if (string.IsNullOrEmpty(_ancLevel)) _ancLevel = s.AncMode;
                AncSub.Visibility = Visibility.Visible;
        }
        }
        Highlight();

        if (s.EqPreset != "?" && CbEq.SelectedItem == null) CbEq.SelectedItem = s.EqPreset;
        CbSpatial.IsChecked = s.SpatialSound;
        CbGame.IsChecked = s.GameMode;
        CbDualDevice.IsChecked = s.DualDevice;

        // 根据设备能力显示/隐藏控件
        SpatialAudioPanel.Visibility = caps.HasSpatialAudio ? Visibility.Visible : Visibility.Collapsed;
        CbSpatial.Visibility = caps.HasSpatialSound ? Visibility.Visible : Visibility.Collapsed;
        CbDualDevice.Visibility = caps.HasDualDevice ? Visibility.Visible : Visibility.Collapsed;

        // 多设备连接列表
        MultiDevicePanel.Visibility = caps.HasDualDevice && _rfcomm.State.DualDevice ? Visibility.Visible : Visibility.Collapsed;
        if (_rfcomm.State.ConnectedDevices.Count > 0)
        {
            MultiDeviceList.ItemsSource = _rfcomm.State.ConnectedDevices;
            MultiDeviceEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            MultiDeviceList.ItemsSource = null;
            MultiDeviceEmpty.Visibility = Visibility.Visible;
        }
        BtnAdaptive.Visibility = caps.HasAdaptiveAnc ? Visibility.Visible : Visibility.Collapsed;

        // 更新型号备注
        ModelNote.Text = $"当前自动识别: {caps.ModelName}";
        UpdateTitle();
    });

    private static void SetBat(ProgressBar b, TextBlock l, (int Lvl, bool Chg)? d)
    {
        if (d is not { } v) { b.Value = 0; l.Text = "-%"; return; }
        b.Value = v.Lvl; l.Text = $"{v.Lvl}%{(v.Chg ? " ⚡" : "")}";
    }

    // 合并设备上报的充电状态和佩戴检测的入盒状态
    private static (int Lvl, bool Chg)? MergeCharge((int Lvl, bool Chg)? bat, string wear) =>
        bat is { } b ? (b.Lvl, b.Chg || wear == "入盒") : null;

    private static readonly System.Windows.Media.SolidColorBrush TransparentBg = System.Windows.Media.Brushes.Transparent;

    private static System.Windows.Media.SolidColorBrush GetAccentBrush() =>
        new(System.Windows.SystemParameters.WindowGlassColor);

    private void Highlight()
    {
        var accent = GetAccentBrush();
        var fg = System.Windows.Media.Brushes.White;
        var nfg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));

        // 主子按钮：选中=主题色背景+白字，未选中=透明灰字；未知模式=全部不选中
        var btns = new[] { (BtnSmart, "Smart"), (BtnAdaptive, "Adaptive"), (BtnTrans, "Transparency"), (BtnOff, "Off") };
        foreach (var (b, t) in btns)
        {
            var active = !string.IsNullOrEmpty(_ancMain) && t == _ancMain;
            b.Background = active ? accent : TransparentBg;
            b.Foreground = active ? fg : nfg;
        }
        var subs = new[] { (BtnAncSmart, "Smart"), (BtnAncLight, "Light"), (BtnAncMedium, "Medium"), (BtnAncDeep, "Deep") };
        foreach (var (b, t) in subs)
        {
            var active = !string.IsNullOrEmpty(_ancLevel) && t == _ancLevel;
            b.Background = active ? accent : TransparentBg;
            b.Foreground = active ? fg : nfg;
        }
    }

    private void AncMain_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button b || b.Tag is not string t) return;
        SwitchAncMain(t);
        Highlight();
    }

    private void AncSub_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button b || b.Tag is not string t) return;
        SwitchAncSub(t);
        Highlight();
    }

    // 切换 ANC 主模式（主页面和托盘菜单共用）
    private void SwitchAncMain(string tag)
    {
        _ancMain = tag;
        _ancUserSetAt = DateTime.Now;
        if (tag is "Off" or "Adaptive" or "Transparency")
        { AncSub.Visibility = Visibility.Collapsed; _rfcomm.SendAnc(tag); }
        else { AncSub.Visibility = Visibility.Visible; _rfcomm.SendAnc(string.IsNullOrEmpty(_ancLevel) ? "Smart" : _ancLevel); }
    }

    // 切换 ANC 子模式
    private void SwitchAncSub(string tag)
    {
        _ancLevel = tag; _ancMain = "Smart";
        _ancUserSetAt = DateTime.Now;
        _rfcomm.SendAnc(tag);
    }

    private void CbSpatial_Changed(object s, RoutedEventArgs e)
    {
        _rfcomm.State.SpatialSound = CbSpatial.IsChecked == true;
        _rfcomm.SendSpatial(CbSpatial.IsChecked == true);
    }

    private void SpatialAudio_Changed(object s, RoutedEventArgs e)
    {
        var tag = (s as RadioButton)?.Tag as string;
        if (tag == null) return;
        _rfcomm.State.SpatialMode = tag;
        _rfcomm.SendSpatialAudio(tag);
    }

    private void CbDualDevice_Changed(object s, RoutedEventArgs e)
    {
        _rfcomm.State.DualDevice = CbDualDevice.IsChecked == true;
        _rfcomm.SendDualDevice(CbDualDevice.IsChecked == true);
        MultiDevicePanel.Visibility = _rfcomm.Caps.HasDualDevice && CbDualDevice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CbGame_Changed(object s, RoutedEventArgs e)
    {
        _rfcomm.State.GameMode = CbGame.IsChecked == true;
        _rfcomm.SendGameMode(CbGame.IsChecked == true, _gameModeCompat);
    }

    private void CbEq_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (CbEq.SelectedItem is string n && n != _rfcomm.State.EqPreset)
        { _rfcomm.State.EqPreset = n; _rfcomm.SendEq(n); }
    }

    private void CbTray_Changed(object s, RoutedEventArgs e) =>
        WriteRegBool(AppConst.RegBase, "TrayEnabled", CbTray.IsChecked == true);

    private void CbAuto_Changed(object s, RoutedEventArgs e)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AppConst.RegRun, true)!;
            if (CbAuto.IsChecked == true) k.SetValue(AppConst.RegRunName, $"\"{Environment.ProcessPath!}\" --minimized");
        else k.DeleteValue(AppConst.RegRunName, false);
        }
        catch { }
    }

    private static bool ReadRegBool(string path, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(path); return k?.GetValue(name) is 1 or (int)1; }
        catch { return false; }
    }

    private static void WriteRegBool(string path, string name, bool v)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(path); k.SetValue(name, v ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord); }
        catch { }
    }

    private static string? ReadRegStr(string path, string name)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(path); return k?.GetValue(name) as string; }
        catch { return null; }
    }

    private static void WriteRegStr(string path, string name, string? v)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(path);
            if (v == null) k.DeleteValue(name, false);
        else k.SetValue(name, v);
        }
        catch { }
    }

    private bool _filtering;  // 防止搜索过滤时触发 CbModel_Changed
    private bool _inTextChanged;  // 防止 TextChanged 递归

    private void CbModel_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (CbModel.IsDropDownOpen) return;
        // 强制布局完成后再打开下拉（首次进入设置页时控件可能尚未完成布局）
        CbModel.UpdateLayout();
        CbModel.IsDropDownOpen = true;
    }

    private void CbModel_DropDownOpened(object s, EventArgs e)
    {
        // 保存当前选中值，清空后重建列表再恢复选中
        var prev = _modelOverride ?? "自动检测";
        _filtering = true;
        CbModel.Items.Clear();
        CbModel.Items.Add("自动检测");
        foreach (var name in _allModelNames) CbModel.Items.Add(name);
        CbModel.SelectedItem = prev;
        CbModel.Text = prev;
        _filtering = false;

        // 监听输入实现实时模糊搜索（只绑定一次）
        if (CbModel.Template.FindName("PART_EditableTextBox", CbModel) is TextBox tb && tb.Tag is null)
        {
            tb.Tag = "hooked";
            tb.TextChanged += (_, _) =>
        {
                if (_inTextChanged) return;
                _inTextChanged = true;
                var filter = tb.Text;
                if (string.IsNullOrEmpty(filter))
                {
                    _filtering = true;
                    CbModel.IsDropDownOpen = true;
                    CbModel.Items.Clear();
                    CbModel.Items.Add("自动检测");
                    foreach (var name in _allModelNames) CbModel.Items.Add(name);
                    _filtering = false;
                    _inTextChanged = false;
                    return;
                }
                _filtering = true;
                CbModel.IsDropDownOpen = true;
                CbModel.Items.Clear();
                CbModel.Items.Add("自动检测");
                foreach (var name in _allModelNames)
                    if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        CbModel.Items.Add(name);
                CbModel.Text = filter;
                _filtering = false;
                tb.CaretIndex = filter.Length;
                _inTextChanged = false;
        };
        }
    }

    private void CbModel_DropDownClosed(object s, EventArgs e)
    {
        // 关闭下拉时恢复正确显示文本，防止搜索后未选择导致显示为空
        var display = _modelOverride ?? "自动检测";
        if (CbModel.Text != display)
        {
            _inTextChanged = true;
            CbModel.Text = display;
            _inTextChanged = false;
        }
    }

    private void CbModel_Changed(object s, SelectionChangedEventArgs e)
    {
        if (_filtering) return;
        if (CbModel.SelectedItem is not string sel) return;
        _modelOverride = sel == "自动检测" ? null : sel;
        WriteRegStr(AppConst.RegBase, "ModelOverride", _modelOverride);

        // 刷新 EQ 列表和 UI
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _rfcomm.Caps;

        CbEq.SelectionChanged -= CbEq_SelectionChanged;
        CbEq.Items.Clear();
        foreach (var kv in caps.EqPresets) CbEq.Items.Add(kv.Key);
        CbEq.SelectionChanged += CbEq_SelectionChanged;

        SpatialAudioPanel.Visibility = caps.HasSpatialAudio ? Visibility.Visible : Visibility.Collapsed;
        CbSpatial.Visibility = caps.HasSpatialSound ? Visibility.Visible : Visibility.Collapsed;
        CbDualDevice.Visibility = caps.HasDualDevice ? Visibility.Visible : Visibility.Collapsed;

        // 多设备连接列表
        MultiDevicePanel.Visibility = caps.HasDualDevice && _rfcomm.State.DualDevice ? Visibility.Visible : Visibility.Collapsed;
        if (_rfcomm.State.ConnectedDevices.Count > 0)
        {
            MultiDeviceList.ItemsSource = _rfcomm.State.ConnectedDevices;
            MultiDeviceEmpty.Visibility = Visibility.Collapsed;
        }
        else
        {
            MultiDeviceList.ItemsSource = null;
            MultiDeviceEmpty.Visibility = Visibility.Visible;
        }
        BtnAdaptive.Visibility = caps.HasAdaptiveAnc ? Visibility.Visible : Visibility.Collapsed;

        ModelNote.Text = sel == "自动检测"
            ? $"当前自动识别: {_rfcomm.Caps.ModelName}"
            : $"已手动指定: {caps.ModelName}";

        // 同步更新状态栏和标题
        UpdateTitle();
        if (_rfcomm.State.Connected)
        {
            StatusText.Text = $"已连接 — {caps.ModelName}";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0xCC, 0x88));
        }
    }

    private void CbGameMode_Changed(object s, SelectionChangedEventArgs e)
    {
        _gameModeCompat = CbGameMode.SelectedIndex == 1;
        WriteRegBool(AppConst.RegBase, "GameModeCompat", _gameModeCompat);
    }

    private void CbTheme_Changed(object s, SelectionChangedEventArgs e)
    {
        var idx = CbTheme.SelectedIndex;
        ApplyTheme(idx);
        try { Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\OppoPodsWin").SetValue("Theme", idx); } catch { }
    }

    private static void ApplyTheme(int index)
    {
        switch (index)
        {
            case 0: // 自动
                Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
                if (Application.Current.MainWindow is MainWindow mw)
                    Wpf.Ui.Appearance.SystemThemeWatcher.Watch(mw);
                break;
            case 1: // 深色
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Dark);
                break;
            case 2: // 浅色
                Wpf.Ui.Appearance.ApplicationThemeManager.Apply(Wpf.Ui.Appearance.ApplicationTheme.Light);
                break;
        }
    }

    private void TbCustomName_Changed(object s, TextChangedEventArgs e)
    {
        WriteRegStr(AppConst.RegBase, "CustomName",
            string.IsNullOrWhiteSpace(TbCustomName.Text) ? null : TbCustomName.Text.Trim());
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _rfcomm.Caps;
        var custom = TbCustomName.Text.Trim();
        var name = !string.IsNullOrEmpty(custom) ? custom : caps.ModelName;
        TitleText.Text = name;
        Title = name;

        // 同步托盘提示
        var s = _rfcomm.State;
        var parts = new List<string>();
        var bl = MergeCharge(s.Battery.GetValueOrDefault("L"), s.WearingL);
        var br = MergeCharge(s.Battery.GetValueOrDefault("R"), s.WearingR);
        if (bl is { } lb) parts.Add($"L:{lb.Lvl}%{(lb.Chg ? "⚡" : "")}");
        if (br is { } rb) parts.Add($"R:{rb.Lvl}%{(rb.Chg ? "⚡" : "")}");
        if (s.Battery.GetValueOrDefault("C") is { } cb) parts.Add($"C:{cb.Level}%{(cb.Charging ? "⚡" : "")}");
        try { _trayIcon.Text = parts.Count > 0 ? $"{name}\n{string.Join(" ", parts)}" : name; } catch { }
    }

    private void Settings_Click(object s, RoutedEventArgs e)
    {
        var showSettings = SettingsPanel.Visibility != Visibility.Visible;
        MainPanel.Visibility = showSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPanel.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
    }

    private void About_Click(object s, RoutedEventArgs e)
    {
        var showAbout = AboutPanel.Visibility != Visibility.Visible;
        MainPanel.Visibility = showAbout ? Visibility.Collapsed : Visibility.Visible;
        AboutPanel.Visibility = showAbout ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
    }

    private void Hyperlink_RequestNavigate(object s, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void TitleBar_MouseDown(object s, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Close_Click(object s, RoutedEventArgs e) => Close();

    private void Reconnect_Click(object s, RoutedEventArgs e)
    {
        _rfcomm.Disconnect();  // 关闭旧 socket，触发 PollAsync 退出 → ConnectAsync 循环自动重连
    }

    private void ResetUi()
    {
        SetBat(LeftBar, LeftLabel, null);
        SetBat(RightBar, RightLabel, null);
        SetBat(CaseBar, CaseLabel, null);
        WearStatus.Text = "";
        AncSub.Visibility = Visibility.Collapsed;
        CbSpatial.IsChecked = false;
        CbGame.IsChecked = false;
        MultiDevicePanel.Visibility = Visibility.Collapsed;
        MultiDeviceList.ItemsSource = null;
    }

    private void BtnRefreshMulti_Click(object s, RoutedEventArgs e) => _rfcomm.SendMultiConnectInfo();

    private void OnWindowClosing(object? s, CancelEventArgs e)
    {
        if (_realClose || CbTray.IsChecked != true) return;
        e.Cancel = true;
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ToggleFromTray()
    {
        if (Visibility == Visibility.Visible)
            Hide();
        else
            ShowFromTray();
    }

    private void SyncUi() => Dispatcher.Invoke(() => OnStateChanged());

    private void ShowTrayMenu()
    {
        var s = _rfcomm.State;
        var caps = _modelOverride != null
            ? DeviceCapabilities.ForceModel(_modelOverride)
            : _rfcomm.Caps;
        var accent = GetAccentBrush();
        var bg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20));

        var border = new Border
        {
            Background = bg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x36, 0x36, 0x36)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Width = 200,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
                Color = System.Windows.Media.Color.FromArgb(0x80, 0, 0, 0),
                BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.35
        }
        };

        var stack = new StackPanel();

        // 连接状态
        var statusColor = s.Connected
            ? System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50)
            : System.Windows.Media.Color.FromRgb(0xFF, 0x55, 0x55);
        stack.Children.Add(new TextBlock
        {
            Text = s.Connected ? $"已连接 — {caps.ModelName}" : "未连接",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(statusColor),
            Margin = new Thickness(10, 6, 10, 4)
        });
        stack.Children.Add(CreateMenuSeparator());

        // 用 Window 替代 Popup — Window 的 Deactivated 可靠地关闭
        Window? menuWin = null;
        var closing = false;
        Action closeMenu = () => { if (menuWin != null) { closing = true; menuWin.Close(); } };

        if (s.Connected)
        {
            // ANC 主模式（降噪按钮后面显示当前子模式）
            var ancLabel = "降噪";
            if (_ancMain == "Smart" && !string.IsNullOrEmpty(_ancLevel))
                ancLabel = $"降噪 · {_ancLevel}";
            foreach (var (label, tag) in new[] { ("关闭", "Off"), ("自适应", "Adaptive"), ("通透", "Transparency"), (ancLabel, "Smart") })
        {
                var isMain = _ancMain == tag;
                stack.Children.Add(CreateMenuRow(label, isMain, () =>
                {
                    SwitchAncMain(tag);
                    closeMenu();
                }));
        }

            // ANC 深度子模式（缩进显示）
            foreach (var (label, tag) in new[] { ("智能", "Smart"), ("轻度", "Light"), ("中度", "Medium"), ("深度", "Deep") })
        {
                var row = CreateMenuRow(label, _ancLevel == tag, () =>
                {
                    SwitchAncSub(tag);
                    closeMenu();
                });
                row.Padding = new Thickness(24, 0, 10, 0);
                row.Visibility = _ancMain == "Smart" ? Visibility.Visible : Visibility.Collapsed;
                stack.Children.Add(row);
        }

            stack.Children.Add(CreateMenuSeparator());

            // 功能开关
            if (caps.HasGameMode)
                stack.Children.Add(CreateMenuToggle("游戏模式", s.GameMode, () =>
                { _rfcomm.SendGameMode(!s.GameMode, _gameModeCompat); s.GameMode = !s.GameMode; SyncUi(); closeMenu(); }));
            if (caps.HasSpatialSound)
                stack.Children.Add(CreateMenuToggle("空间音效", s.SpatialSound, () =>
                { _rfcomm.SendSpatial(!s.SpatialSound); s.SpatialSound = !s.SpatialSound; SyncUi(); closeMenu(); }));
            if (caps.HasDualDevice)
                stack.Children.Add(CreateMenuToggle("双设备连接", s.DualDevice, () =>
                { _rfcomm.SendDualDevice(!s.DualDevice); s.DualDevice = !s.DualDevice; SyncUi(); closeMenu(); }));

            stack.Children.Add(CreateMenuSeparator());
        }

        stack.Children.Add(CreateMenuItem("显示主页面", () => { closeMenu(); ShowFromTray(); }));
        stack.Children.Add(CreateMenuItem("退出", () => { closeMenu(); _realClose = true; Application.Current.Shutdown(); }));

        border.Child = stack;

        menuWin = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowActivated = true,
            Content = border
        };
        menuWin.Deactivated += (_, _) => { if (!closing) { closing = true; menuWin.Close(); } };

        // 定位到鼠标位置（类似原生右键菜单），超出屏幕边界则自动修正
        menuWin.WindowStartupLocation = WindowStartupLocation.Manual;
        var pt = Forms.Control.MousePosition;
        var dpi = VisualTreeHelper.GetDpi(this);
        var x = pt.X / dpi.DpiScaleX;
        var y = pt.Y / dpi.DpiScaleY;
        menuWin.SourceInitialized += (_, _) =>
        {
            var sw = SystemParameters.WorkArea.Width;
            var sh = SystemParameters.WorkArea.Height;
            if (x + menuWin.ActualWidth > sw) x = sw - menuWin.ActualWidth - 4;
            if (y + menuWin.ActualHeight > sh) y = sh - menuWin.ActualHeight - 4;
            menuWin.Left = x;
            menuWin.Top = y;
        };
        menuWin.Left = x;
        menuWin.Top = y;
        menuWin.Show();
        menuWin.Activate();
    }

    // 菜单行按钮（带 ✓ 前缀）
    private static Button CreateMenuRow(string text, bool active, Action onClick)
    {
        var prefix = active ? "✓ " : "   ";
        var btn = new Button
        {
            Content = prefix + text,
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = active ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBB, 0xBB, 0xBB)),
            BorderThickness = new Thickness(0),
            Height = 30, FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(10, 0, 10, 0),
            Cursor = Cursors.Hand
        };
        var hoverBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x35, 0x35));
        btn.MouseEnter += (_, _) => btn.Background = hoverBg;
        btn.MouseLeave += (_, _) => btn.Background = System.Windows.Media.Brushes.Transparent;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // 功能开关行（带 ☑/☐）
    private static Button CreateMenuToggle(string text, bool active, Action onClick) =>
        CreateMenuRow(text, active, onClick);

    private static Border CreateMenuSeparator() => new()
    {
        Height = 1,
        Margin = new Thickness(8, 2, 8, 2),
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3D, 0x3D, 0x3D))
    };

    private static Button CreateMenuItem(string text, Action onClick)
    {
        var hoverBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x35, 0x35));
        var transBg = System.Windows.Media.Brushes.Transparent;

        var btn = new Button
        {
            Content = text,
            Background = transBg,
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Height = 28,
            FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 0, 12, 0),
            Cursor = Cursors.Hand
        };

        var template = new ControlTemplate(typeof(Button));
        var root = new FrameworkElementFactory(typeof(Border));
        root.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        root.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        root.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = root;
        btn.Template = template;

        btn.MouseEnter += (_, _) => btn.Background = hoverBg;
        btn.MouseLeave += (_, _) => btn.Background = transBg;
        btn.Click += (_, _) => onClick();

        return btn;
    }
}
