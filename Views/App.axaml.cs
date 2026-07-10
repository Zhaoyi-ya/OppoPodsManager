using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OppoPodsManager;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = Environment.GetCommandLineArgs();

            var mainWindow = new MainWindow();
            // 静默启动：设为 MainWindow 但不 Show()，避免任务栏闪现。
            // MainWindow 构造函数会检测 --minimized 并保持隐藏 + ShowInTaskbar=false。
            desktop.MainWindow = mainWindow;
            if (!args.Contains("--minimized"))
                mainWindow.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
