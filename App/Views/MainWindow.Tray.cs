using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using OppoPodsManager.Infrastructure;
using OppoPodsManager.Localization;
using CoreCaps = OppoPodsManager.Core.Devices.DeviceCapabilities;
using CoreFeature = OppoPodsManager.Core.Devices.DeviceFeature;
using CoreState = OppoPodsManager.Core.Devices.HeadsetState;

namespace OppoPodsManager;

public partial class MainWindow
{
    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private void ToggleFromTray()
    {
        if (IsVisible)
            Hide();
        else
            ShowFromTray();
    }

    private void UpdateTrayTooltip(string text)
    {
        if (_trayIcon != null)
            _trayIcon.ToolTipText = text;
    }

    private void SetupTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIcon
            {
                Icon = _iconConnected ?? _iconDisconnected,
                ToolTipText = LanguageManager.Instance.GetString(LanguageManager.Instance.Tray_Tooltip),
                IsVisible = true
            };
            _trayIcon.Clicked += OnTrayClicked;

            var icons = new TrayIcons { _trayIcon };
            if (global::Avalonia.Application.Current != null)
                TrayIcon.SetIcons(global::Avalonia.Application.Current, icons);

            RebuildTrayMenu();
        }
        catch (Exception ex)
        {
            Log.Ex("UI", "SetupTrayIcon", ex);
        }
    }

    private void OnTrayClicked(object? s, EventArgs e)
    {
        if (_trayClickTimer == null)
        {
            // 首次点击 → 启动 400ms 定时器
            _trayClickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background,
                (_, _) =>
                {
                    _trayClickTimer?.Stop();
                    // 超时 → 单击 → 显示小窗
                    ShowSmallWindow();
                });
            _trayClickTimer.Start();
        }
        else
        {
            // 第二次点击 → 双击 → 显示大窗
            _trayClickTimer.Stop();
            _trayClickTimer = null;
            ShowBigWindow();
        }
    }

    /// <summary>大 UI 设置变更时同步刷新已有小窗外观。</summary>
    private void RefreshSmallWindowAppearance()
    {
        if (_smallWindow == null)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_smallWindow == null)
                return;
            try { _smallWindow.RefreshAppearance(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        });
    }

    private void ShowSmallWindow()
    {
        _trayClickTimer = null;

        if (_smallWindow != null && _smallWindow.IsVisible)
        {
            _smallWindow.Hide();
            return;
        }

        if (_smallWindow != null)
        {
            // 复用已有小窗时先刷新外观（标题栏、模糊、背景）
            _smallWindow.RefreshAppearance();
        }

        if (_smallWindow == null)
        {
            _smallWindow = new SmallWindow(_pods, () =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_smallWindow == null) return;
                    // Show 后 300ms 内忽略 Deactivated，避免 Show/Activate 过程中瞬时焦点丢失导致循环
                    if ((DateTime.Now - _smallWindowShownAt).TotalMilliseconds < 300) return;
                    try { _smallWindow.Hide(); }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                    _trayClickTimer = null;
                });
            });
            // 窗口被用户点 X 关闭后置 null，下次重新创建（Close 后不能再 Show）
            _smallWindow.Closed += (_, _) => _smallWindow = null;
        }

        // 先 Show 再定位：Show 后 FrameSize 才有值，能拿到含装饰的实际窗口尺寸
        _smallWindow.Show();
        _smallWindow.Activate();
        _smallWindowShownAt = DateTime.Now;

        // 定位到屏幕右下角（紧贴任务栏上方）
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            // FrameSize 含窗口装饰（阴影/边框），比 Width/Height 更准确
            var frame = _smallWindow.FrameSize;
            var w = (int)Math.Ceiling((frame?.Width  ?? _smallWindow.Width)  * scale);
            var h = (int)Math.Ceiling((frame?.Height ?? _smallWindow.Height) * scale);
            _smallWindow.Position = new PixelPoint(
                area.X + area.Width  - w,
                area.Y + area.Height - h);
        }
    }

    private void ShowBigWindow()
    {
        _trayClickTimer = null;
        ShowFromTray();
    }

    /// <summary>按当前连接状态与设备能力重建托盘右键菜单（NativeMenu 原生菜单）。</summary>
    private void RebuildTrayMenu()
    {
        if (_trayIcon == null) return;
        _trayIcon.ToolTipText = LanguageManager.Instance.GetString(LanguageManager.Instance.Tray_Tooltip);
        var s = _pods.State;
        var caps = _modelOverride != null
            ? _controller.ForceModel(_modelOverride)
            : _pods.Caps;

        var signature = BuildTrayMenuSignature(s, caps);
        if (_trayIcon.Menu != null && signature == _trayMenuSignature)
            return;
        _trayMenuSignature = signature;

        var menu = new NativeMenu();
        _trayAncMap.Clear();

        if (IsStateConnected(s))
        {
            // ANC 模式切换
            if (caps.AncOptions.Count > 0)
            {
                foreach (var opt in caps.AncOptions)
                {
                    if (opt.Children.Count > 0)
                    {
                        foreach (var child in opt.Children)
                        {
                            var active = _ancLevel == child.Key;
                            var item = new NativeMenuItem((active ? "✓ " : "    ") + DeviceUiLabels.AncLabel(child.Key));
                            _trayAncMap[item] = (child.Key, opt.Key, true);
                            item.Click += TrayAncItem_Click;
                            menu.Add(item);
                        }
                    }
                    else
                    {
                        var active = _ancMain == opt.Key;
                        var item = new NativeMenuItem((active ? "✓ " : "") + DeviceUiLabels.AncLabel(opt.Key));
                        _trayAncMap[item] = (opt.Key, "", false);
                        item.Click += TrayAncItem_Click;
                        menu.Add(item);
                    }
                }
                menu.Add(new NativeMenuItemSeparator());
            }

            // 功能开关
            if (caps.HasGameMode)
            {
                var item = new NativeMenuItem((s.GamingEnabled ? "✓ " : "") + LanguageManager.Instance.GetString(LanguageManager.Instance.Feature_GameMode));
                item.Click += (_, _) => { _controller.SetGameMode(!s.GamingEnabled); };
                menu.Add(item);
            }
            if (caps.HasSpatialSound)
            {
                var item = new NativeMenuItem((s.SpatialAudioEnabled ? "✓ " : "") + LanguageManager.Instance.GetString(LanguageManager.Instance.Feature_SpatialSound));
                item.Click += (_, _) => _controller.SetSpatial(!s.SpatialAudioEnabled);
                menu.Add(item);
            }
            if (caps.HasDualDevice)
            {
                var item = new NativeMenuItem((FeatureOn(s, CoreFeature.DualDevice) ? "✓ " : "") + LanguageManager.Instance.GetString(LanguageManager.Instance.Feature_DualDevice));
                item.Click += (_, _) => _controller.SetDualDevice(!FeatureOn(s, CoreFeature.DualDevice));
                menu.Add(item);
            }
            if (caps.HasGameMode || caps.HasSpatialSound || caps.HasDualDevice)
                menu.Add(new NativeMenuItemSeparator());
        }

        var showItem = new NativeMenuItem(LanguageManager.Instance.GetString(LanguageManager.Instance.Tray_ShowMain));
        showItem.Click += (_, _) => ShowFromTray();
        menu.Add(showItem);
        menu.Add(new NativeMenuItemSeparator());
        var quitItem = new NativeMenuItem(LanguageManager.Instance.GetString(LanguageManager.Instance.Tray_Quit));
        quitItem.Click += (_, _) => QuitApplication();
        menu.Add(quitItem);
        _trayIcon.Menu = menu;
    }

    private string BuildTrayMenuSignature(CoreState s, CoreCaps caps)
    {
        var ancSignature = string.Join("|", caps.AncOptions.Select(opt =>
            opt.Children.Count > 0
                ? $"{opt.Key}>" + string.Join(",", opt.Children.Select(c => $"{c.Key}:{c.Label}"))
                : $"{opt.Key}:{opt.Label}"));
        return string.Join(";",
            IsStateConnected(s),
            caps.ModelId,
            caps.ModelName,
            caps.HasGameMode,
            caps.HasSpatialSound,
            caps.HasDualDevice,
            s.GamingEnabled,
            s.SpatialAudioEnabled,
            FeatureOn(s, CoreFeature.DualDevice),
            _ancMain,
            _ancLevel,
            ancSignature);
    }

    /// <summary>托盘 ANC 菜单项点击：从字典查键，避免闭包捕获问题。</summary>
    private void TrayAncItem_Click(object? sender, EventArgs e)
    {
        if (sender is not NativeMenuItem item || !_trayAncMap.TryGetValue(item, out var info))
            return;
        if (!_pods.IsConnected) return;
        _ancUserSetAt = DateTime.Now;
        if (info.isChild)
        {
            _ancMain = info.parentKey;
            _ancLevel = info.key;
        }
        else
        {
            _ancMain = info.key;
            _ancLevel = "";
        }
        _controller.SetAnc(info.key);
        HighlightAnc();
        RebuildTrayMenu();
    }
}
