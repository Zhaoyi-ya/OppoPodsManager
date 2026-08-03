using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Assets.UserSettings;
using OppoPodsManager.Control.Logging;
using StatusWindow = OppoPodsManager.UI.MiniWindow.Status.SmallWindow;

namespace OppoPodsManager.UI.Tray;

// 管理 Windows 托盘入口，并让主窗口可以在不退出蓝牙会话的情况下恢复显示。
public sealed class TrayIconController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly FrontendState _frontendState;
    private readonly ControlManager _controlManager;
    private readonly CommandDispatcher _commandDispatcher;
    private readonly SettingsManager _settings;
    private readonly Func<Window?> _mainWindow;
    private readonly Avalonia.Controls.TrayIcon _icon;
    private DispatcherTimer? _clickTimer;
    private StatusWindow? _miniWindow;
    private bool _disposed;

    public TrayIconController(
        IClassicDesktopStyleApplicationLifetime desktop,
        FrontendState frontendState,
        ControlManager controlManager,
        SettingsManager settings,
        ApplicationLog log,
        Func<Window?> mainWindow)
    {
        _desktop = desktop;
        _frontendState = frontendState;
        _controlManager = controlManager;
        _commandDispatcher = new CommandDispatcher(controlManager, log);
        _settings = settings;
        _mainWindow = mainWindow;
        _icon = new Avalonia.Controls.TrayIcon
        {
            ToolTipText = TranslationCatalog.Get("Tray_Tooltip"),
            Icon = LoadIcon(),
            Menu = TrayMenu.Create(_frontendState.Snapshot, ActiveDeviceManager, _commandDispatcher, ShowMainWindow, ExitApplication)
        };
        _icon.Clicked += OnClicked;
        _frontendState.Changed += OnFrontendStateChanged;
        _settings.Changed += OnSettingsChanged;
        Avalonia.Controls.TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });
    }

    // 单击切换状态小窗；双击在计时窗口内恢复主窗口。
    private void OnClicked(object? sender, EventArgs args)
    {
        ApplicationLog.Current?.Info("Tray", $"托盘图标点击：pendingDoubleClick={_clickTimer is not null}。");
        if (_clickTimer is null)
        {
            _clickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (_, _) =>
            {
                _clickTimer?.Stop();
                _clickTimer = null;
                ToggleMiniWindow();
            });
            _clickTimer.Start();
            return;
        }

        _clickTimer.Stop();
        _clickTimer = null;
        ShowMainWindow();
    }

    // 业务快照变化时更新菜单勾选状态和悬停文本。
    private void OnFrontendStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
                return;
            _icon.ToolTipText = BuildTooltip(snapshot);
            _icon.Menu = TrayMenu.Create(snapshot, ActiveDeviceManager, _commandDispatcher, ShowMainWindow, ExitApplication);
        });
    }

    // 构造原项目风格的托盘提示，显示型号和已知电量摘要。
    private static string BuildTooltip(BusinessSnapshot snapshot)
    {
        var name = DeviceText.DeviceName(snapshot.Identity?.ModelName, snapshot.DeviceName);
        if (!snapshot.IsConnected)
            return name;

        var parts = new List<string>();
        AddBattery(parts, "L", snapshot.LeftBattery);
        AddBattery(parts, "R", snapshot.RightBattery);
        AddBattery(parts, "C", snapshot.CaseBattery);
        return parts.Count == 0 ? name : $"{name}\n{string.Join(" ", parts)}";
    }

    // 将单个电池快照转换为托盘提示片段。
    private static void AddBattery(List<string> parts, string channel, BatteryLevel? battery)
    {
        if (battery is { } value)
            parts.Add($"{channel}:{value.Percent}%{(value.IsCharging ? "⚡" : "")}");
    }

    // 设置或语言变化后刷新已经创建的小窗，保持其与主窗口外观一致。
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _miniWindow is null)
                return;

            _miniWindow.RefreshAppearance();
            _miniWindow.RefreshAncLabels();
        });
    }

    // 延迟创建的小窗只在用户单击托盘时分配窗口资源。
    private void ToggleMiniWindow()
    {
        ApplicationLog.Current?.Info("Tray", $"切换状态小窗：visible={_miniWindow?.IsVisible == true}。");
        if (_miniWindow is { IsVisible: true })
        {
            _miniWindow.Hide();
            return;
        }

        _miniWindow ??= CreateMiniWindow();
        _miniWindow.Show();
        _miniWindow.Activate();
        var screen = _miniWindow.Screens?.Primary;
        if (screen is null)
            return;
        var scale = _miniWindow.RenderScaling <= 0 ? 1d : _miniWindow.RenderScaling;
        var frame = _miniWindow.FrameSize;
        var width = (frame?.Width ?? _miniWindow.Width) * scale;
        var height = (frame?.Height ?? _miniWindow.Height) * scale;
        _miniWindow.Position = new PixelPoint(
            (int)Math.Round(screen.WorkingArea.Right - width),
            (int)Math.Round(screen.WorkingArea.Bottom - height));
    }

    // 小窗关闭后不保留不可复用的 Window 对象。
    private StatusWindow CreateMiniWindow()
    {
        // Next 小窗只接收快照、控制器和设置，不直接接触通信对象。
        var window = new StatusWindow(
            _frontendState,
            _controlManager,
            _settings,
            () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _miniWindow?.Hide();
            }));
        window.Closed += (_, _) => _miniWindow = null;
        return window;
    }

    private IBrandManager? ActiveDeviceManager => _controlManager.ActiveManager;

    private void ShowMainWindow()
    {
        ApplicationLog.Current?.Info("Tray", "托盘请求显示主窗口。");
        _miniWindow?.Hide();
        var window = _mainWindow();
        if (window is null)
            return;

        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    // 显式关闭应用，确保桌面生命周期触发控制层释放。
    private void ExitApplication()
    {
        ApplicationLog.Current?.Info("Tray", "托盘请求退出应用。");
        _desktop.Shutdown();
    }

    // 从嵌入资源读取图标，避免发布目录缺少图标文件。
    private static WindowIcon LoadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://OppoPodsManager/Assets/VisualAssets/tuopan.ico"));
        return new WindowIcon(new Bitmap(stream));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _icon.Clicked -= OnClicked;
        _frontendState.Changed -= OnFrontendStateChanged;
        _settings.Changed -= OnSettingsChanged;
        _clickTimer?.Stop();
        _miniWindow?.Close();
        _icon.Dispose();
    }
}
