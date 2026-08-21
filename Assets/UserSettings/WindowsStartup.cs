using Microsoft.Win32;
using System.Diagnostics;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Assets.UserSettings;

// 负责将应用写入或移出当前用户的 Windows 登录启动项。
public static class WindowsStartup
{
    private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "OppoPodsManager";

    // 同步自启动配置；开发环境没有可执行文件时只保留设置，不写入无效路径。
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
            return;

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !executablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return;

        key.SetValue(ValueName, $"\"{executablePath}\"");
    }

    // 统一处理注册表启动项失败，避免界面层承担系统设置异常。
    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            SetEnabled(enabled);
            ApplicationLog.Current?.Info("Settings", $"Windows 开机启动已更新：enabled={enabled}。");
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Settings", "设置 Windows 开机自启失败。", exception);
            Trace.TraceError("设置 Windows 开机自启失败：" + exception);
            return false;
        }
    }
}
