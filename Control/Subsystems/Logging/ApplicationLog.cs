using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace OppoPodsManager.Control.Subsystems.Logging;

// 为通信、控制和界面提供统一的线程安全日志服务。
public sealed class ApplicationLog : TraceListener, IDisposable
{
    private const int MaximumEntries = 2_000;
    private readonly object _gate = new();
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly string _directory;
    private readonly string _filePath;
    private StreamWriter? _writer;
    private bool _disposed;

    // 提供给未采用依赖注入的底层模块使用的当前应用日志实例。
    public static ApplicationLog? Current { get; private set; }

    public ApplicationLog(string directory)
    {
        // 统一终端输出编码，避免 Windows 代码页把中文后端日志显示为乱码。
        ConfigureConsoleEncoding();
        _directory = directory;
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "OppoPodsManager.log");
        _writer = CreateWriter(_filePath);
        Current = this;
        Trace.Listeners.Add(this);
        Write(LogLevel.Information, "App", "日志服务已启动。");
    }

    public event EventHandler<LogEntry>? EntryAdded;

    // 返回稳定副本，供 UI 或反馈导出操作在任意线程读取。
    public IReadOnlyList<LogEntry> Snapshot() => _entries.ToArray();

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);

    public void Info(string category, string message) => Write(LogLevel.Information, category, message);

    public void Error(string category, string message, Exception? exception = null)
        => Write(LogLevel.Error, category, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    // 将当前文本日志、环境信息和可选诊断摘要压缩为单个 ZIP 文件。
    public void ExportZip(string targetPath, string? diagnosticSummary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        Flush();
        using var archive = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        var log = archive.CreateEntry("OppoPodsManager.log", CompressionLevel.Optimal);
        using (var output = log.Open())
        using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            foreach (var entry in Snapshot())
                writer.WriteLine(entry.ToString());

        var environment = archive.CreateEntry("environment.txt", CompressionLevel.Optimal);
        using var environmentWriter = new StreamWriter(environment.Open(), new UTF8Encoding(false));
        environmentWriter.WriteLine($"UTC: {DateTimeOffset.UtcNow:O}");
        environmentWriter.WriteLine($"OS: {Environment.OSVersion}");
        environmentWriter.WriteLine($"Runtime: {Environment.Version}");
        environmentWriter.WriteLine($"Process: {Environment.ProcessPath}");

        if (!string.IsNullOrWhiteSpace(diagnosticSummary))
        {
            var diagnostics = archive.CreateEntry("diagnostic.txt", CompressionLevel.Optimal);
            using var diagnosticWriter = new StreamWriter(diagnostics.Open(), new UTF8Encoding(false));
            diagnosticWriter.Write(diagnosticSummary);
        }
    }

    // 封装日志压缩导出异常，调用方只处理成功或失败的显示结果。
    public bool TryExportZip(
        string targetPath,
        out string? errorMessage,
        string? diagnosticSummary = null)
    {
        errorMessage = null;
        try
        {
            ExportZip(targetPath, diagnosticSummary);
            Info("Logging", "日志压缩包导出完成：file=" + targetPath + "。");
            return true;
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            Error("Logging", "日志压缩包导出失败：file=" + targetPath + "。", exception);
            return false;
        }
    }

    // 捕获 Avalonia 和 .NET Trace 输出，使第三方组件异常也进入反馈日志。
    public override void Write(string? message) => AppendTrace(message);

    public override void WriteLine(string? message) => AppendTrace(message);

    private void AppendTrace(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            Write(LogLevel.Debug, "Trace", message.Trim());
    }

    private void Write(LogLevel level, string category, string message)
    {
        if (_disposed)
            return;

        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        _entries.Enqueue(entry);
        while (_entries.Count > MaximumEntries && _entries.TryDequeue(out _)) { }

        lock (_gate)
        {
            _writer?.WriteLine(entry.ToString());
            _writer?.Flush();
        }
        // 同步输出到 stderr，便于在 VS Code 中看到后端协议和控制层日志。
        Console.Error.WriteLine(entry.ToString());
        EntryAdded?.Invoke(this, entry);
    }

    // 强制写入缓冲区，导出前调用可确保最新事件不会遗漏。
    public override void Flush()
    {
        lock (_gate)
            _writer?.Flush();
    }

    private static StreamWriter CreateWriter(string path)
        => new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

    // 将重定向到 VSCode 的标准输出和错误输出固定为 UTF-8。
    private static void ConfigureConsoleEncoding()
    {
        try
        {
            var utf8 = new UTF8Encoding(false);
            Console.OutputEncoding = utf8;
            Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8, 1024, leaveOpen: true)
            {
                AutoFlush = true
            });
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"无法设置终端 UTF-8 编码：{exception}");
        }
    }

    // 释放监听器与文件句柄，并保留 TraceListener 的标准释放语义。
    protected override void Dispose(bool disposing)
    {
        if (!disposing || _disposed)
        {
            base.Dispose(disposing);
            return;
        }
        Write(LogLevel.Information, "App", "日志服务已停止。");
        _disposed = true;
        Trace.Listeners.Remove(this);
        if (ReferenceEquals(Current, this))
            Current = null;
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
        base.Dispose(disposing);
    }
}

// 表示可显示和可导出的单条应用日志。
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message)
{
    public override string ToString() => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] [{Category}] {Message}";
}

// 保持日志严重程度独立于 UI 和第三方日志库。
public enum LogLevel
{
    Debug,
    Information,
    Error
}
