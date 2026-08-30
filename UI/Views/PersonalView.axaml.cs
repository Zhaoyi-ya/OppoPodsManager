using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Assets.VisualAssets;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 个性化设置页面：外观（语言/主题/卡片透明度/Toast 时长）、
/// 背景（缩略图/模糊）、自定义耳机图案、设备名、高级渲染/Acrylic。
/// 页面本地持有控件与事件；主题应用、语言切换、窗口背景、卡片透明度、标题、
/// 高级渲染/Acrylic 等外壳级效果通过 <see cref="Host"/> 回退到 MainWindow。
/// </summary>
public partial class PersonalView : PageView
{
    private bool _initializing;
    private bool _refreshingComboBoxes;
    private readonly ObservableCollection<LanguageOption> _languageList = new();

    public PersonalView()
    {
        InitializeComponent();

        // 仅本页控件的事件接线；外壳级副作用经 Host 回退。
        CbTheme.SelectionChanged += CbTheme_Changed;
        CbLanguage.SelectionChanged += CbLanguage_Changed;
        SlOpacity.ValueChanged += (_, _) =>
        {
            var v = (int)SlOpacity.Value;
            TbOpacity.Text = $"{v}%";
            UiSettings.SetInt("CardOpacity", v);
            Host?.RefreshCardOpacity();
        };
        BtnResetOpacity.Click += (_, _) => SlOpacity.Value = 50;
        CbToastDuration.SelectionChanged += CbToastDuration_Changed;
        BtnBgLeft.Click += BtnBgLeft_Click;
        BtnBgRight.Click += BtnBgRight_Click;
        BgThumbDefault.PointerPressed += (_, _) => Host?.SelectBackground("default");
        BgThumbAdd.PointerPressed += (_, _) => Host?.AddBackgroundImage();
        SlBgBlur.ValueChanged += (_, _) =>
        {
            var v = (int)SlBgBlur.Value;
            TbBgBlur.Text = v.ToString();
            UiSettings.SetInt("BgBlur", v);
            Host?.ApplyBackgroundBlur();
        };
        BtnResetBgBlur.Click += (_, _) => SlBgBlur.Value = 0;
        TbCustomName.TextChanged += TbCustomName_Changed;
        CbAdvancedRender.IsCheckedChanged += (_, _) =>
        {
            var on = CbAdvancedRender.IsChecked == true;
            UiSettings.SetBool("AdvancedRender", on);
            Log?.Debug("UI", $"设置: 高级渲染 -> {on}");
            Host?.SetAdvancedRender(on);
        };
        CbAcrylicBlur.IsCheckedChanged += (_, _) =>
        {
            // 启动期 Attach() 会显式调用 SetAcrylicBlurSilent（静默，不弹提示）；
            // 此处仅响应**用户手动拨动开关**的操作。
            if (_initializing) return;
            Host?.SetAcrylicBlur(CbAcrylicBlur.IsChecked == true);
        };
    }

    public override void Attach(
        ControlManager? controlManager,
        SettingsStore uiSettings,
        ApplicationLog? log,
        CommandDispatcher? commandDispatcher,
        FrontendState? frontendState,
        DesktopLinkService? desktopLinks)
    {
        base.Attach(controlManager, uiSettings, log, commandDispatcher, frontendState, desktopLinks);
        _initializing = true;
        try
        {
            InitializeLanguageSelection();
            CbTheme.SelectedIndex = NextThemeIndex();

            var opacityVal = Math.Clamp(UiSettings.GetInt("CardOpacity", 50), 0, 90);
            SlOpacity.Value = opacityVal;
            TbOpacity.Text = $"{opacityVal}%";

            CbToastDuration.SelectedIndex = ReadToastDurationIndex();

            var customName = UiSettings.GetString("CustomName");
            TbCustomName.Text = customName ?? "";

            // 背景缩略图与窗口背景由外壳按当前历史应用。
            Host?.RefreshBackground();

            SlBgBlur.Value = UiSettings.GetInt("BgBlur", 0);
            TbBgBlur.Text = SlBgBlur.Value.ToString();

            CbAdvancedRender.IsChecked = UiSettings.GetBool("AdvancedRender", false);
            // Acrylic 模糊开关（仅 Windows 支持；Linux 上无 DWM 毛玻璃，禁用该选项避免渲染异常）
            CbAcrylicBlur.IsChecked = OperatingSystem.IsWindows() && UiSettings.GetBool("AcrylicBlur", false);
            CbAcrylicBlur.IsEnabled = OperatingSystem.IsWindows();
            // 启动期静默应用 Acrylic：窗口已在 AdaptToPlatform 按设置应用，这里只同步背景设置可用状态，
            // 不弹提示（提示只在用户手动拨动 CbAcrylicBlur 开关时由 IsCheckedChanged 触发）。
            Host?.SetAcrylicBlurSilent(CbAcrylicBlur.IsChecked == true);
        }
        finally
        {
            _initializing = false;
        }
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        // 个性化页面无快照驱动的状态，留空。
    }

    // ---- 语言 ----
    private void CbLanguage_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_refreshingComboBoxes || _initializing || CbLanguage.SelectedItem is not LanguageOption option)
            return;
        Host?.ApplyLanguage(option);
    }

    private void InitializeLanguageSelection()
    {
        _languageList.Clear();
        foreach (var option in LanguageManager.GetAvailableLanguages())
            _languageList.Add(option);

        CbLanguage.ItemsSource = _languageList;
        var configured = UiSettings.GetString("Language");
        var selected = _languageList.FirstOrDefault(option =>
            string.Equals(option.CultureCode,
                LanguageManager.NormalizeSelectionCulture(configured),
                StringComparison.OrdinalIgnoreCase));
        CbLanguage.SelectedItem = selected ?? _languageList[0];
    }

    /// <summary>外壳在切换语言后调用：替换首项"自动"的本地化文本并恢复选中。</summary>
    internal void RefreshLanguageSelectionUi()
    {
        var autoOption = LanguageManager.GetAvailableLanguages()[0];
        _refreshingComboBoxes = true;
        try
        {
            var selIdx = CbLanguage.SelectedIndex;
            _languageList[0] = autoOption;
            if (selIdx >= 0 && selIdx < _languageList.Count)
                CbLanguage.SelectedIndex = selIdx;
        }
        finally
        {
            _refreshingComboBoxes = false;
        }
    }

    /// <summary>外壳在切换语言后调用：刷新本页受本地化影响的下拉框显示。</summary>
    internal void RefreshLocalizedComboBoxes()
    {
        _refreshingComboBoxes = true;
        RefreshSelectedIndex(CbTheme);
        RefreshSelectedIndex(CbToastDuration);
        RefreshSelectedIndex(CbLanguage);
        _refreshingComboBoxes = false;
    }

    private static void RefreshSelectedIndex(ComboBox comboBox)
    {
        var idx = comboBox.SelectedIndex;
        if (idx >= 0)
        {
            comboBox.SelectedIndex = -1;
            comboBox.SelectedIndex = idx;
        }
    }

    // ---- 主题 ----
    private void CbTheme_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_refreshingComboBoxes) return;
        var idx = CbTheme.SelectedIndex;
        Log?.Debug("UI", $"用户操作: 切换主题 -> {idx}");
        Host?.ApplyTheme(idx);
    }

    private int NextThemeIndex()
        => UiSettings.GetString("Theme")?.ToLowerInvariant() switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };

    // ---- Toast 时长 ----
    private void CbToastDuration_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (_refreshingComboBoxes || _initializing) return;
        UiSettings.SetInt("ToastDuration", ToastDurationSecondsFromIndex(CbToastDuration.SelectedIndex));
        Log?.Debug("UI", $"设置: Toast 时长索引 -> {CbToastDuration.SelectedIndex}");
    }

    private int ReadToastDurationIndex()
    {
        var seconds = UiSettings.GetInt("ToastDuration", 5);
        return seconds switch { 3 => 0, 4 => 1, 6 => 3, 7 => 4, 8 => 5, _ => 2 };
    }

    private static int ToastDurationSecondsFromIndex(int index) => index switch
    {
        0 => 3,
        1 => 4,
        3 => 6,
        4 => 7,
        5 => 8,
        _ => 5
    };

    // ---- 背景缩略图滚动 ----
    private void BtnBgLeft_Click(object? s, RoutedEventArgs e)
        => BgThumbScroller.Offset = BgThumbScroller.Offset.WithX(Math.Max(0, BgThumbScroller.Offset.X - 200));

    private void BtnBgRight_Click(object? s, RoutedEventArgs e)
        => BgThumbScroller.Offset = BgThumbScroller.Offset.WithX(
            Math.Min(BgThumbScroller.Extent.Width - BgThumbScroller.Viewport.Width,
                     BgThumbScroller.Offset.X + 200));

    // ---- 设备名 ----
    private void TbCustomName_Changed(object? s, TextChangedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(TbCustomName.Text) ? null : TbCustomName.Text.Trim();
        UiSettings.SetString("CustomName", name);
        Host?.SetCustomDeviceName(name);
    }
}
