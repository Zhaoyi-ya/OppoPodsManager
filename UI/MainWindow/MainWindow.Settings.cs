using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Assets.UserSettings;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    /// <summary>
    /// 切换界面语言并刷新所有本地化敏感的控件与状态（由 PersonalView 经 IViewHost 调用）。
    /// </summary>
    internal void ApplyLanguage(LanguageOption option)
    {
        _uiSettings.SetString("Language", LanguageManager.ToStoredLanguage(option));
        LanguageManager.ApplyConfiguredCulture(option.IsAutomatic ? null : option.CultureCode);
        // 语言列表里的"自动"项在初始化时按启动语言本地化，切换语言后需重新取当前语言的显示文本。
        // 必须延迟到本次 SelectionChanged 的选择更新结束后再改源集合，否则 Avalonia 抛
        // "Source collection was modified during selection update"。
        var autoOption = LanguageManager.GetAvailableLanguages()[0];
        Dispatcher.UIThread.Post(() =>
        {
            // 先记录当前选中索引。替换首项（自动）后，若该对象正是当前选中项且其
            // 本地化文本随语言变化，record 值不再相等，Avalonia 会判定选中项已离开列表而
            // 清空选择——下拉框会显示空白。按原索引重新选中即可恢复正确的显示文本。
            PersonalView?.RefreshLanguageSelectionUi();
        });
        PersonalView?.RefreshLocalizedComboBoxes();
        HomeView?.RefreshAncLabels();
        HomeView?.RefreshSpatialAudioLabels();
        // 自定义耳机图案行的文案需随语言重建
        BuildEarphoneCustomUi();
        // "恢复已隐藏设备"按钮文字由代码动态设置（带计数），切语言后需重新本地化
        SettingsView?.RefreshRestoreHiddenDevicesButton();
        // 顶栏连接状态与佩戴状态文字在状态事件里赋值，切语言后需重新刷新一次，
        // 否则会停留在旧语言直到下次状态变化。
        if (_frontendState is not null)
            ApplyNextSnapshot(_frontendState.Snapshot);
        // 音效页系统预设名按当前语言本地化（DisplayName），切语言后需重建列表以刷新显示。
        if (_frontendState is not null)
            EqView?.ApplySnapshot(_frontendState.Snapshot);
        // 优先设备下拉的中文选项在策略同步时重建；重置签名确保语言切换后使用本地化文本。
        SettingsView?.ResetPrioritySignature();
        // Force rebuild multi-device list with new language strings
        SyncNextMultiDeviceList(_frontendState?.Snapshot);
    }

    private void ApplyTheme(int index)
    {
        var theme = SukiTheme.GetInstance();
        switch (index)
        {
            case 0:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Default);
                _isLightTheme = DetectSystemLightTheme();
                break;
            case 1:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Dark);
                _isLightTheme = false;
                break;
            case 2:
                theme.ChangeBaseTheme(Avalonia.Styling.ThemeVariant.Light);
                _isLightTheme = true;
                break;
        }
        RefreshThemeColors();
    }

    private static bool DetectSystemLightTheme()
    {
        var actualTheme = Application.Current?.ActualThemeVariant;
        return actualTheme == Avalonia.Styling.ThemeVariant.Light;
    }
    private void RefreshThemeColors(bool refreshState = true)
    {
        // 清除之前可能残留的 SukiUI 资源覆盖，让 SukiUI 原生主题系统接管
        // （按钮、ComboBox 等控件的 Background 绑定到 SukiBackground，
        //   如果在 Window 级覆盖会导致按钮背景与窗口背景混为一体）
        Resources.Remove("SukiBackground");
        Resources.Remove("SukiCardBackground");

        EnsureThemeResourceBrushes();
        // 同步共享调色板主题标志，供主页视图（ANC/状态）按主题取色。
        AppPalette.IsLightTheme = _isLightTheme;

        // 按原项目主题刷新顺序恢复窗口背景，避免侧栏透明层改变 Acrylic 的视觉层次。
        _windowBackgroundBrush.Color = _isLightTheme
            ? Color.FromRgb(0xE5, 0xE5, 0xEA)
            : Colors.Transparent;
        Background = _windowBackgroundBrush;

        var transparencyPct = ReadCardOpacity();
        var alpha = (byte)Math.Clamp(255 - (transparencyPct * 255 / 100), 25, 255); // 0→255, 90→26

        // 卡片背景：浅色使用白色半透；深色使用深色半透。Linux 下白色半透卡片容易呈现为白底。
        var cardBase = _isLightTheme ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1C, 0x1C, 0x1E);
        _glassCardBgBrush.Color = Color.FromArgb(alpha, cardBase.R, cardBase.G, cardBase.B);

        // 侧边栏底框与卡片背景保持一致，复用画刷对象。
        _sidebarBackgroundBrush.Color = Color.FromArgb(alpha, cardBase.R, cardBase.G, cardBase.B);
        SidebarBorder.Background = _sidebarBackgroundBrush;

        // 侧边栏选中高亮：浅色灰底，深色微白
        _sidebarSelectedBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x0C, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF);

        // 弹窗遮罩：深色半透明黑 / 浅色半透明白
        _dialogOverlayBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x50, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x80, 0x00, 0x00, 0x00);

        // 普通文字按钮背景板：浅色用微黑，深色用微白，保留轻量层次感。
        _textPanelButtonBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x14, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        _textPanelButtonHoverBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x20, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
        _textPanelButtonPressedBgBrush.Color = _isLightTheme
            ? Color.FromArgb(0x2A, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x3A, 0xFF, 0xFF, 0xFF);

        // 重置对话框确认按钮颜色，避免主题切换后残留
        if (DialogConfirmBtn.IsVisible)
            DialogConfirmBtn.Background = Brushes.Transparent;

        HomeView?.HighlightAnc();
        if (_frontendState is not null && refreshState)
            ApplyNextSnapshot(_frontendState.Snapshot);
    }

    private void EnsureThemeResourceBrushes()
    {
        if (_themeResourceBrushesRegistered)
            return;

        Resources["GlassCardBg"] = _glassCardBgBrush;
        Resources["SidebarSelectedBg"] = _sidebarSelectedBgBrush;
        Resources["DialogOverlayBg"] = _dialogOverlayBgBrush;
        Resources["TextPanelButtonBg"] = _textPanelButtonBgBrush;
        Resources["TextPanelButtonHoverBg"] = _textPanelButtonHoverBgBrush;
        Resources["TextPanelButtonPressedBg"] = _textPanelButtonPressedBgBrush;
        _themeResourceBrushesRegistered = true;
    }

    private void RefreshCardOpacity()
    {
        RefreshThemeColors(refreshState: false); // 只更新复用画刷颜色，不触发完整状态刷新链路
    }

    private static void UpdateGlassCards(ScrollViewer sv, IBrush bg)
    {
        if (sv.Content is StackPanel sp)
            foreach (var child in sp.Children)
                if (child is Border b && b.Classes.Contains("glassCard"))
                    b.Background = bg;
    }

    private int NextThemeIndex()
        => _uiSettings.GetString("Theme")?.ToLowerInvariant() switch
        {
            "dark" => 1,
            "light" => 2,
            _ => 0
        };
    private static string NextThemeName(int index) => index switch
    {
        1 => "Dark",
        2 => "Light",
        _ => "System"
    };
    private int ReadCardOpacity()
        => Math.Clamp(_uiSettings.GetInt("CardOpacity", 50), 0, 90);

    // 读取 Next 界面设置，避免窗口直接依赖设置对象结构。
    private bool ReadUiBool(string key, bool fallback)
        => _uiSettings.GetBool(key, fallback);

    private int ReadUiInt(string key, int fallback)
        => _uiSettings.GetInt(key, fallback);

    private void WriteUiBool(string key, bool value)
        => _uiSettings.SetBool(key, value);

    private void WriteUiString(string key, string? value)
        => _uiSettings.SetString(key, value);

}
