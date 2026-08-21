using System.Diagnostics;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Subsystems.Desktop;

// 统一处理桌面端外部链接打开和操作日志，避免窗口直接调用系统进程。
public sealed class DesktopLinkService
{
    private readonly ApplicationLog? _log;

    public DesktopLinkService(ApplicationLog? log)
    {
        _log = log;
    }

    // 使用 Windows 外壳打开用户选择的链接。
    public bool TryOpen(string url, string operation)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _log?.Error("Desktop", $"{operation}失败：链接为空。");
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _log?.Info("Desktop", $"{operation}成功：url={url}。");
            return true;
        }
        catch (Exception exception)
        {
            _log?.Error("Desktop", $"{operation}失败：url={url}。", exception);
            return false;
        }
    }
}
