using Avalonia;

namespace OppoPodsManager;

static class Program
{
    // Avalonia configuration, don't remove; used by Previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>Avalonia 桌面入口点（AOT 兼容）。</summary>
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
