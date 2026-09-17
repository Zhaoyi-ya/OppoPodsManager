using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            InstallGlobalExceptionHandlers();
            _desktopLinks = new DesktopLinkService(_log);
            _feedbackExporter = new FeedbackExportService(_log);
            var communication = CommunicationBootstrap.CreateDefault();
            // 应用层只创建一份官方型号目录，供控制层识别和设置页筛选共同使用。
            _modelCatalog = DeviceModelData.LoadCatalog();
            var settingsStore = new SettingsStore(_settings);
            _controlManager = new ControlManager(
                _frontendState,
                new DeviceScanner(communication),
                [new OppoManagerFactory(_modelCatalog), new VivoManagerFactory(), new EdifierManagerFactory(), new HuaweiManagerFactory(), new XiaomiManagerFactory(), new AppleManagerFactory()],
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

    // 全局异常兜底。
    // 背景：界面上偶发的「Avalonia.Win32 内 Index was out of range、无法计算异常堆栈跟踪」会直接
    // 终止进程，且因异常来自平台层的 native 回调，调试器也拿不到托管堆栈——只能看到进程猝死、
    // 日志停在半行。这里统一接管：把完整异常（含堆栈）写进日志，并拦截 UI 线程异常避免整个应用
    // 被非致命问题（如提示窗定位/关闭失败）带走。
    private void InstallGlobalExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            _log?.Error("App", "UI 线程未处理异常（已拦截，应用继续运行）。", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // 非 UI 线程的终止性异常：拦不住进程退出，但保证堆栈落盘供排查。
            _log?.Error("App", "未处理的终止性异常。", e.ExceptionObject as Exception);
            _log?.Flush();
        };

        _log?.Debug("App", "全局异常兜底已安装（UI 线程异常将记录堆栈并拦截）。");
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
            ShouldKeepMainWindowAlive,
            ReloadMainWindow);
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

    // 主窗口「重新加载」：关闭当前窗口后重建（与托盘唤起同一条路径）。
    // 供仅在窗口构造期应用的设置使用——目前是「窗口模糊（Acrylic）」，
    // 因为 DWM 透明提示/背景着色器在运行期就地改写不可靠，重建窗口才能保证全部界面内容按新设置加载。
    private void ReloadMainWindow()
    {
        // 一次性回调：等旧窗口真正关闭（Closed 已触发、本类的窗口引用已在 OnMainWindowClosed 中清空）
        // 之后再重建，避免受 Closed/_mainWindow 清理的先后时序影响而拿到已关闭的实例。
        void OnClosed(object? sender, EventArgs e)
        {
            if (sender is Window closed)
                closed.Closed -= OnClosed;

            // 延后到下一个 UI 调度周期：不在旧窗口的 Closed 事件栈内创建新窗口，
            // 避免与旧窗口的视觉树拆除、资源回收过程交叠。
            Dispatcher.UIThread.Post(ReopenMainWindow);
        }

        var previous = _mainWindow;
        if (previous is null)
        {
            ReopenMainWindow();
            return;
        }

        _log?.Info("App", "重新加载主窗口：关闭旧窗口后重建。");
        previous.Closed += OnClosed;
        previous.Close();
    }

    // 创建并显示新的主窗口（仅在窗口重载路径上调用）。
    private void ReopenMainWindow()
    {
        if (_startupCancellation?.IsCancellationRequested == true)
            return;

        var window = GetOrCreateMainWindow();
        if (window is null)
            return;

        window.Show();
        window.Activate();
        // 不在这里弹 Toast：窗口刚重建时入队会命中 SukiUI 宿主/管理器的时序问题
        // （新旧宿主争抢同一个 Toast 控件，抛 already has a visual parent，真机已复现）。
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
