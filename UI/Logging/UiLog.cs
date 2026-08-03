using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.UI.Logging;

// 为界面事件提供统一的日志入口，避免界面依赖旧项目日志实现。
internal static class UiLog
{
    // 兼容窗口现有的短日志调用，并统一转发到应用日志服务。
    public static void D(string category, string message) => Debug(category, message);

    public static void Ex(string category, string message, Exception exception)
        => Error(category, message, exception);

    public static void Debug(string category, string message)
        => ApplicationLog.Current?.Debug(category, message);

    public static void Error(string category, string message, Exception? exception = null)
        => ApplicationLog.Current?.Error(category, message, exception);
}
