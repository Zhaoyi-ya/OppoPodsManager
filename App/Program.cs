using System;
using Avalonia;
#if WINDOWS
using Avalonia.Win32;
#endif

namespace OppoPodsManager;

static class Program
{
    // Avalonia configuration, don't remove; used by Previewer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect();

#if WINDOWS
        var useSoftwareRendering = !string.Equals(
            Environment.GetEnvironmentVariable("OPPO_PODS_GPU_RENDERING"), "1", StringComparison.Ordinal)
            || string.Equals(Environment.GetEnvironmentVariable("OPPO_PODS_SOFTWARE_RENDERING"), "1", StringComparison.Ordinal);
        if (useSoftwareRendering)
        {
            var options = new Win32PlatformOptions
            {
                RenderingMode = [Avalonia.Win32RenderingMode.Software],
            };
            if (string.Equals(Environment.GetEnvironmentVariable("OPPO_PODS_REDIRECTION_COMPOSITION"), "1", StringComparison.Ordinal))
                options.CompositionMode = [Avalonia.Win32CompositionMode.RedirectionSurface];
            builder = builder.With(options);
        }
#endif

        return builder.LogToTrace();
    }

    /// <summary>Avalonia 桌面入口点（AOT 兼容）。</summary>
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
}
