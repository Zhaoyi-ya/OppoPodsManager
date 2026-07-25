using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OppoPodsManager.Application.Control;
using OppoPodsManager.Composition;
using OppoPodsManager.Infrastructure;
using OppoPodsManager.Localization;

namespace OppoPodsManager;

public partial class App : Avalonia.Application
{
    private MainWindow? _silentMainWindow;

    /// <summary>
    /// Composition-root service used by the desktop UI and application lifetime.
    /// </summary>
    internal static HeadsetControlService? HeadsetControl { get; set; }

    /// <summary>
    /// Desktop control backend: connection lifecycle, discovery, and feature policy.
    /// </summary>
    internal static HeadsetDesktopController? DesktopController { get; set; }

    internal static bool IsMinimizedStartup() =>
        Array.Exists(Environment.GetCommandLineArgs(), IsMinimizedArgument);

    private static bool IsMinimizedArgument(string arg) =>
        string.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "-minimized", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "/minimized", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)
        || string.Equals(arg, "/tray", StringComparison.OrdinalIgnoreCase);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LanguageManager.ApplyConfiguredCulture(SettingsManager.GetString("Language"));
        HeadsetControl = ServiceRegistration.CreateHeadsetControlService();
        DesktopController = new HeadsetDesktopController(new HeadsetUiSession(HeadsetControl));
        Log.D("APP", "分层架构服务已注册");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += OnShutdownRequested;
            var mainWindow = new MainWindow();
            if (IsMinimizedStartup())
            {
                // 静默启动只初始化后台逻辑和托盘，不把窗口交给桌面生命周期自动显示。
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _silentMainWindow = mainWindow;
            }
            else
            {
                desktop.MainWindow = mainWindow;
                mainWindow.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        // DesktopController owns the session + HeadsetControlService lifetime.
        // QuitApplication may have already cleared this after a timed stop.
        var controller = DesktopController;
        if (controller is null)
            return;

        DesktopController = null;
        HeadsetControl = null;
        try
        {
            await controller.StopAsync(TimeSpan.FromMilliseconds(1200)).ConfigureAwait(false);
            await controller.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancel during shutdown is expected.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Ex("APP", "OnShutdownRequested dispose controller", ex);
        }
    }
}
