using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using SukiUI;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{
    // 标记本次关闭是「窗口重载（应用层面重建）」而非退出应用：关闭策略不得把它当成退出请求。
    private bool _reloadingWindow;

    private void AdaptToPlatform()
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

    // 用户拨动「窗口模糊」开关。
    // Acrylic 是窗口级表现，只在窗口构造期（AdaptToPlatform）应用：DWM 透明提示与背景着色器
    // 在运行期就地改写并不总能可靠生效，因此这里直接**关闭并重建主窗口**，
    // 等价于「关到托盘再唤起」那条已验证可行的路径，保证全部界面内容按新设置重新加载。
    private void ToggleAcrylicBlur(bool on)
    {
        ApplyAcrylicBlurSilently(on);
        RequestWindowReload();
    }

    // 请求应用层关闭当前窗口并重建。
    // 注意：此处**不弹 Toast**。窗口重建后立刻入队的 Toast 会命中 SukiUI 宿主/管理器的时序问题：
    // 新旧窗口的宿主争抢同一个 SukiToast 控件，抛 "already has a visual parent"（真机已复现）。
    // ToastManager 虽已改为每窗口一份，但「重建瞬间入队」这一时机本身仍不可靠，故不在此弹提示。
    // 重载本身即是最直观的反馈，设置效果由新窗口的 AdaptToPlatform 直接体现。
    private void RequestWindowReload()
    {
        if (_requestWindowReload is null)
        {
            // 未接入应用层（AOT 无参构造 / 设计期）：单窗口、不重建，保持原有的提示行为，下次启动生效。
            _logManager?.Debug("UI", "窗口未接入重载回调，Acrylic 设置将在下次启动生效。");
            ShowAcrylicToast(ReadUiBool("AcrylicBlur", false));
            return;
        }

        _logManager?.Debug("UI", "重新加载主窗口以应用 Acrylic 设置。");
        _reloadingWindow = true;
        _requestWindowReload();
    }

    private void ShowAcrylicToast(bool on)
        => ToastManager.CreateToast()
            .WithTitle(on
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicEnabled)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicDisabled))
            .WithContent(on
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicEnabledMsg)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_AcrylicDisabledMsg))
            .Dismiss().After(TimeSpan.FromSeconds(3)).Queue();

    // Acrylic 开关的持久化与背景联动：写设置、复位背景、同步背景设置可用状态，不弹提示。
    // 窗口级表现由窗口重建后的 AdaptToPlatform 应用（见 ToggleAcrylicBlur）。
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
        // 例外：窗口重载（如切换 Acrylic 模糊）是应用内部的关闭+重建，不代表用户要退出，
        // 因此跳过退出分支，按下方正常路径关闭，由应用层随后重建窗口。
        if (!_reloadingWindow
            && _shouldKeepWindowAlive?.Invoke() != true && _requestApplicationExit is not null)
        {
            e.Cancel = true;
            _realClose = true;
            DisposeRuntimeUiResources();
            _requestApplicationExit();
            return;
        }

        // 启用关闭到托盘（或窗口重载）时，关闭主窗口本身但保留托盘和设备会话。
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
