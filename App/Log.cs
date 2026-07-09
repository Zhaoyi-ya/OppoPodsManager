using System;
using System.IO;

namespace OppoPodsManager;

/// <summary>
/// 调试日志：同时输出到 stderr（终端可见）和文件 ~/.config/OppoPodsManager/app.log。
/// </summary>
public static class Log
{
    private static readonly string? LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OppoPodsManager");
    private static readonly string? LogPath = LogDir != null ? Path.Combine(LogDir, "app.log") : null;
    private static readonly object _lock = new();

    public static void D(string tag, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";
        Write(line);
    }

    public static void Result(string tag, string operation, bool ok, string? detail = null)
    {
        var status = ok ? "OK" : "FAIL";
        var tail = string.IsNullOrEmpty(detail) ? "" : " — " + detail;
        D(tag, $"{operation}: {status}{tail}");
    }

    public static void Ex(string tag, string context, Exception ex)
    {
        D(tag, $"{context} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
    }

    private static void Write(string line)
    {
        System.Diagnostics.Trace.WriteLine(line);
        Console.Error.WriteLine(line);
        try
        {
            if (LogDir == null) return;
            if (LogPath == null) return;
            lock (_lock)
            {
                if (!Directory.Exists(LogDir!)) Directory.CreateDirectory(LogDir!);
                File.AppendAllText(LogPath, line + "\n");
            }
        }
        catch { }
    }
}
