using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using SukiUI;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private void AdaptToPlatform()
    {
        // AcrylicBlur 仅 Windows 有 DWM 毛玻璃；Linux(X11/Wayland) 上 Avalonia 会把
        // WindowTransparencyLevel.AcrylicBlur 退化成半透明灰 + 深色背景透出，效果差，
        // 故 Linux 直接跳过整个分支（即使 settings 里存了 true 也不应用）。
        if (OperatingSystem.IsWindows() && ReadUiBool("AcrylicBlur", false))
        {
            TransparencyLevelHint = new List<WindowTransparencyLevel>
            {
                WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Transparent
            };
            Background = Avalonia.Media.Brushes.Transparent;
            SidebarFullBg.IsVisible = true;
            SidebarBorder.Background = Avalonia.Media.Brushes.Transparent;
            BackgroundShaderCode = "vec4 main(vec2 fragCoord) { return vec4(0.0); }";
        }
        if (ReadUiBool("AdvancedRender", false))
            EnableAdvancedRender();
    }
    private void ToggleAcrylicBlur(bool on)
    {
        ApplyAcrylicBlurSilently(on);
        ToastManager.CreateToast()
            .WithTitle(on
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicEnabled)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicDisabled))
            .WithContent(on
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicEnabledMsg)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicDisabledMsg))
            .Dismiss().After(TimeSpan.FromSeconds(3)).Queue();
    }

    // 启动期静默应用 Acrylic：只写设置、复位背景、同步背景设置可用状态，不弹提示。
    internal void ApplyAcrylicBlurSilently(bool on)
    {
        WriteUiBool("AcrylicBlur", on);
        _logManager?.Debug("UI", $"设置: Acrylic 模糊(静默) -> {on}");
        if (on)
            SelectBackground("default");
        UpdateBackgroundSettingsAvailability(on);
    }

    private void UpdateBackgroundSettingsAvailability(bool acrylicBlurEnabled)
    {
        var enabled = !acrylicBlurEnabled;
        PersonalView.BackgroundSettingsContent.IsEnabled = enabled;
        PersonalView.BackgroundSettingsContent.Opacity = enabled ? 1 : 0.45;
        PersonalView.BgThumbDefault.Classes.Set("selected", true);
    }
    private void EnableAdvancedRender()
    {
        IsTitleBarVisible = false;
        CustomTitleBar.IsVisible = true;
        SidebarFullBg.IsVisible = true;
        SidebarBorder.Background = Avalonia.Media.Brushes.Transparent;
        RootGrid.Margin = new Thickness(0, 32, 0, 0);
    }

    private void DisableAdvancedRender()
    {
        IsTitleBarVisible = true;
        CustomTitleBar.IsVisible = false;
        SidebarFullBg.IsVisible = false;
        SidebarBorder.Background = _sidebarBackgroundBrush;
        RootGrid.Margin = default;
    }

    private void TitleBarDrag_PointerPressed(object? s, Avalonia.Input.PointerPressedEventArgs e)
        => BeginMoveDrag(e);

    private void CustomMin_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "窗口操作: 最小化");
        WindowState = WindowState.Minimized;
    }

    private void CustomMax_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var nextState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        _logManager?.Debug("UI", $"窗口操作: 切换窗口状态 -> {nextState}");
        WindowState = nextState;
    }

    private void CustomClose_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "窗口操作: 点击关闭按钮");
        Close();
    }
    private void OnWindowClosing(object? s, WindowClosingEventArgs e)
    {
        if (_realClose)
            return;

        // 通知主页视图窗口正在关闭，避免异步回追访问已释放控件。
        HomeView?.MarkClosed();

        // 未启用关闭到托盘时，主窗口关闭请求直接交给应用生命周期处理。
        if (_shouldKeepWindowAlive?.Invoke() != true && _requestApplicationExit is not null)
        {
            e.Cancel = true;
            _realClose = true;
            DisposeRuntimeUiResources();
            _requestApplicationExit();
            return;
        }

        // 启用关闭到托盘时，关闭主窗口本身但保留托盘和设备会话。
        _realClose = true;
        DisposeRuntimeUiResources();
    }

    private void DisposeRuntimeUiResources()
    {
        if (_runtimeUiDisposed)
            return;
        _runtimeUiDisposed = true;

        Closing -= OnWindowClosing;
        if (_frontendState is not null)
            _frontendState.Changed -= OnNextStateChanged;
        if (_controlManager is not null)
            _controlManager.AvailableDevicesChanged -= OnAvailableDevicesChanged;
        PropertyChanged -= OnWindowPropertyChanged;
        _interactiveSurface?.Dispose();
        _interactiveSurface = null;
        foreach (var subscription in _linguaSubs)
            subscription.Dispose();
        _linguaSubs.Clear();
        EqView?.StopDebounceTimer();
        _bgApplyDebounceTimer?.Stop();
        LogView?.Stop();
        DisposeWindowImages();
        SetBackgroundImageSource(null, "");
        _backgroundImages.Dispose();
    }
    private void DisposeSukiWindow()
    {
        if (_sukiWindowDisposed)
            return;

        _sukiWindowDisposed = true;
        Dispose();
    }
}
