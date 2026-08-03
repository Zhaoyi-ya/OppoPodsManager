using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Logging;

// 负责生成包含应用日志和设备摘要的反馈压缩包。
public sealed class FeedbackExportService
{
    private readonly ApplicationLog _log;

    public FeedbackExportService(ApplicationLog log)
    {
        _log = log;
    }

    // 将当前运行环境和设备状态导出到指定目录，并返回生成文件信息。
    public FeedbackExportResult Export(string directory, string version, BusinessSnapshot? snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        try
        {
            Directory.CreateDirectory(directory);
            var fileName = $"OPPOPods_feedback_{DateTime.Now:yyyyMMdd_HHmmss}.zip";
            var path = Path.Combine(directory, fileName);
            _log.ExportZip(path, BuildSummary(version, snapshot));
            _log.Info("Feedback", "反馈日志导出完成：file=" + path + "。");
            return FeedbackExportResult.Success(fileName, path);
        }
        catch (Exception exception)
        {
            _log.Error("Feedback", "反馈日志导出失败。", exception);
            return FeedbackExportResult.Failure(exception.Message);
        }
    }

    // 构建设备反馈所需的简短状态摘要，避免日志导出层依赖界面控件。
    private static string BuildSummary(string version, BusinessSnapshot? snapshot)
    {
        var connected = snapshot?.IsConnected == true;
        var model = string.IsNullOrWhiteSpace(snapshot?.Identity?.ModelName)
            ? "unknown"
            : snapshot.Identity.ModelName;
        var battery = connected
            ? string.Join(
                " ",
                new[] { snapshot?.LeftBattery, snapshot?.RightBattery, snapshot?.CaseBattery }
                    .Select((value, index) => value is { } level
                        ? $"{index switch { 0 => "L", 1 => "R", _ => "C" }}{level.Percent}%"
                        : null)
                    .Where(value => value is not null))
            : "N/A";

        return $"""
--- 系统信息 ---
版本: {version}
操作系统: {Environment.OSVersion}
运行时: {Environment.Version}
设备型号: {model}
连接状态: {(connected ? "connected" : "disconnected")}
电量: {battery}
""";
    }
}

// 表示反馈导出服务生成的文件。
public sealed record FeedbackExportResult(
    bool Succeeded,
    string FileName,
    string FilePath,
    string? ErrorMessage)
{
    public static FeedbackExportResult Success(string fileName, string filePath)
        => new(true, fileName, filePath, null);

    public static FeedbackExportResult Failure(string errorMessage)
        => new(false, string.Empty, string.Empty, errorMessage);
}
