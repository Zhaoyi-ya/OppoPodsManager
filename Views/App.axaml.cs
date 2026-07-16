using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OppoPodsManager;

public partial class App : Application
{
    private MainWindow? _silentMainWindow;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
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
}
