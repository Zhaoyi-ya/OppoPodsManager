using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OppoPodsManager.Communication;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Subsystems.Desktop;
using OppoPodsManager.Control.Subsystems.Notifications;
using OppoPodsManager.Control.Brands.Oppo;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Brands.Vivo;
using OppoPodsManager.Control.Brands.Edifier;
using OppoPodsManager.Control.Brands.Huawei;
using OppoPodsManager.Control.Brands.Xiaomi;
using OppoPodsManager.Control.Brands.Apple;
using OppoPodsManager.UI.MainWindow;
using OppoPodsManager.UI.Toast;
using OppoPodsManager.UI.Tray;
using OppoPodsManager.Assets.UserSettings;
using OppoPodsManager.Assets.Oplus;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Subsystems.Updates;
using OppoPodsManager.Assets.Localization;

namespace OppoPodsManager;

public sealed partial class App : Application
{
    public static bool IsMinimizedStartup() => false;
    private readonly FrontendState _frontendState = new();
    private ControlManager? _controlManager;
    private ModelCatalog? _modelCatalog;
    private ToastNotificationService? _toastNotifications;
    private TrayIconController? _trayIcon;
    private SettingsManager? _settings;
    private ApplicationLog? _log;
    private CommandDispatcher? _commandDispatcher;
    private NotificationCoordinator? _notificationCoordinator;
    private UpdateCoordinator? _updateCoordinator;
    private DesktopLinkService? _desktopLinks;
    private FeedbackExportService? _feedbackExporter;
    private CancellationTokenSource? _startupCancellation;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private MainWindow? _mainWindow;
    private Task _windowMemoryReclaimTask = Task.CompletedTask;

    public override void Initialize()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OppoPodsManager",
            "settings.json");
        _settings ??= new SettingsManager(settingsPath);
        LanguageManager.ApplyConfiguredCulture(_settings.Current.Language);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktopLifetime = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OppoPodsManager",
                "settings.json");
            _settings ??= new SettingsManager(settingsPath);
            _log = new ApplicationLog(Path.Combine(Path.GetDirectoryName(settingsPath)!, "Logs"));
            _log.Info("App", "开始初始化桌面生命周期和设备控制器。");
            _desktopLinks = new DesktopLinkService(_log);
            _feedbackExporter = new FeedbackExportService(_log);
            var communication = CommunicationBootstrap.CreateDefault();
            // 应用层只创建一份官方型号目录，供控制层识别和设置页筛选共同使用。
            _modelCatalog = DeviceModelData.LoadCatalog();
            var settingsStore = new SettingsStore(_settings);
            _controlManager = new ControlManager(
                _frontendState,
                new DeviceScanner(communication),
                [new OppoManagerFactory(_modelCatalog), new VivoManagerFactory(), new EdifierManagerFactory(), new HuaweiManagerFactory(), new XiaomiManagerFactory(_modelCatalog), new AppleManagerFactory()],
                settingsStore);
            _controlManager.StartMonitoring();
            // 由应用层创建唯一的命令调度器，所有界面入口共享同一控制层调用边界。
            _commandDispatcher = new CommandDispatcher(_controlManager, _log);
            // 由控制层统一判断连接和低电量通知，界面层只负责渲染通知请求。
            _notificationCoordinator = new NotificationCoordinator(_frontendState);
            _updateCoordinator = new UpdateCoordinator(_settings);
            _toastNotifications = new ToastNotificationService(_notificationCoordinator, _settings);
            _startupCancellation = new CancellationTokenSource();
            _mainWindow = CreateMainWindow();
            desktop.MainWindow = _mainWindow;
            _log.Info("App", "主窗口和托盘控制器创建完成。");
            _trayIcon = new TrayIconController(
                desktop,
                _frontendState,
                _controlManager,
                _commandDispatcher,
                _settings,
                _log,
                GetOrCreateMainWindow);
            desktop.Exit += async (_, _) =>
            {
                _startupCancellation?.Cancel();
                _startupCancellation?.Dispose();
                _startupCancellation = null;
                _toastNotifications?.Dispose();
                _notificationCoordinator?.Dispose();
                _notificationCoordinator = null;
                _trayIcon?.Dispose();
                _trayIcon = null;
                await _windowMemoryReclaimTask;
                if (_controlManager is not null)
                    await _controlManager.DisposeAsync();
                _updateCoordinator?.Dispose();
                _updateCoordinator = null;
                _desktopLinks = null;
                _feedbackExporter = null;
            _log?.Dispose();
            _log = null;
            _commandDispatcher = null;
            _mainWindow = null;
                _desktopLifetime = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    // 创建一个只属于当前显示周期的主窗口，托盘和设备会话不随窗口创建。
    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow(
            _frontendState,
            _controlManager ?? throw new InvalidOperationException("控制器尚未初始化。"),
            _modelCatalog ?? throw new InvalidOperationException("型号目录尚未初始化。"),
            _settings ?? throw new InvalidOperationException("设置管理器尚未初始化。"),
            _log ?? throw new InvalidOperationException("日志服务尚未初始化。"),
            _commandDispatcher ?? throw new InvalidOperationException("命令调度器尚未初始化。"),
            _updateCoordinator,
            _desktopLinks,
            _feedbackExporter,
            RequestApplicationExit,
            ShouldKeepMainWindowAlive);
        window.Opened += OnMainWindowOpened;
        window.Closed += OnMainWindowClosed;
        return window;
    }

    // 主窗口每次打开时只负责触发一次尚未建立的初始设备连接。
    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        var token = _startupCancellation?.Token ?? CancellationToken.None;
        _ = ConnectInitialDeviceAsync(token);
    }

    // 主窗口关闭后解除桌面生命周期引用，使其控件和渲染资源可以回收。
    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _mainWindow))
            return;

        if (_desktopLifetime is not null && ReferenceEquals(_desktopLifetime.MainWindow, sender))
            _desktopLifetime.MainWindow = null;
        _mainWindow = null;
        _log?.Info("App", "主窗口已关闭，窗口资源已从托盘生命周期中释放。");
        _windowMemoryReclaimTask = ReclaimClosedWindowMemoryAsync();
    }

    // 托盘需要主窗口时按需创建，避免恢复已关闭的 Window 实例。
    private Window? GetOrCreateMainWindow()
    {
        if (_desktopLifetime is null || _startupCancellation?.IsCancellationRequested == true)
            return null;

        if (_mainWindow is null)
        {
            _mainWindow = CreateMainWindow();
            _desktopLifetime.MainWindow = _mainWindow;
        }

        return _mainWindow;
    }

    // 响应未启用关闭到托盘时的主窗口关闭请求。
    private void RequestApplicationExit()
    {
        _desktopLifetime?.Shutdown();
    }

    // 从应用设置读取主窗口关闭策略，避免主窗口直接依赖设置键和托盘实现。
    private bool ShouldKeepMainWindowAlive()
        => _settings?.Current.MinimizeToTray == true;

    // 等待 Closed 事件完成后再回收旧视觉树，避免同步阻塞窗口关闭过程。
    private async Task ReclaimClosedWindowMemoryAsync()
    {
        await Task.Yield();
        await Task.Run(static () =>
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        });
        _log?.Debug("App", "主窗口关闭后的托管和原生视觉资源回收已完成。");
    }

    // 启动后自动连接首个已连接的 Melody 耳机，复刻原项目的免手动重连体验。
    private async Task ConnectInitialDeviceAsync(CancellationToken cancellationToken)
    {
        if (_controlManager is null || _controlManager.ActiveManager is not null)
            return;

        try
        {
            if (await _controlManager.ConnectFirstAvailableAsync(cancellationToken))
                _log?.Info("Startup", "自动连接和设备初始读取已完成。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // 自动连接失败不阻止窗口和手动重连入口继续可用。
            _log?.Error("Startup", "自动连接耳机失败。", exception);
        }
    }
}
