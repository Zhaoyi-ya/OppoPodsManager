using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OppoPodsManager.Communication;
using OppoPodsManager.Communication.Windows;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.UI.MainWindow;
using OppoPodsManager.UI.Toast;
using OppoPodsManager.UI.Tray;
using OppoPodsManager.Assets.UserSettings;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Control.Updates;
using OppoPodsManager.Assets.Localization;

namespace OppoPodsManager;

public sealed partial class App : Application
{
    public static bool IsMinimizedStartup() => false;
    private readonly FrontendState _frontendState = new();
    private ControlManager? _controlManager;
    private IBrandManager? _modelCatalogProvider;
    private ToastNotificationService? _toastNotifications;
    private TrayIconController? _trayIcon;
    private SettingsManager? _settings;
    private ApplicationLog? _log;
    private UpdateService? _updateService;
    private CancellationTokenSource? _startupCancellation;

    public override void Initialize()
    {
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OppoPodsManager",
            "settings.json");
        _settings ??= new SettingsManager(settingsPath);
        TranslationCatalog.SetLanguage(_settings.Current.Language);
        LanguageManager.ApplyConfiguredCulture(_settings.Current.Language);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OppoPodsManager",
                "settings.json");
            _settings ??= new SettingsManager(settingsPath);
            _log = new ApplicationLog(Path.Combine(Path.GetDirectoryName(settingsPath)!, "Logs"));
            _log.Info("App", "开始初始化桌面生命周期和设备控制器。");
            var communication = new CommunicationController(
                [new WindowsRfcommFactory()],
                [new WindowsBluetoothDiscovery()]);
            // 由品牌管理器提供官方型号目录，启动层只负责把它注入界面。
            _modelCatalogProvider = new OppoManager();
            _controlManager = new ControlManager(_frontendState, new DeviceScanner(communication));
            _updateService = new UpdateService(_settings);
            _toastNotifications = new ToastNotificationService(_frontendState, _settings);
            desktop.MainWindow = new MainWindow(_frontendState, _controlManager, _modelCatalogProvider, _settings, _log, _updateService);
            _log.Info("App", "主窗口和托盘控制器创建完成。");
            _startupCancellation = new CancellationTokenSource();
            desktop.MainWindow.Opened += (_, _) => _ = ConnectInitialDeviceAsync(_startupCancellation.Token);
            _trayIcon = new TrayIconController(desktop, _frontendState, _controlManager, _settings, _log, () => desktop.MainWindow);
            desktop.Exit += async (_, _) =>
            {
                _startupCancellation?.Cancel();
                _startupCancellation?.Dispose();
                _startupCancellation = null;
                _toastNotifications?.Dispose();
                _trayIcon?.Dispose();
                if (_controlManager is not null)
                    await _controlManager.DisposeAsync();
                if (_modelCatalogProvider is not null)
                    await _modelCatalogProvider.DisposeAsync();
                _updateService?.Dispose();
                _updateService = null;
                _log?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
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
