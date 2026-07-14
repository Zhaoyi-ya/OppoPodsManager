using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace OppoPodsManager.Util;

/// <summary>日志管理器：内存只保留最近日志，历史日志定期落盘，并支持 ZIP 导出。</summary>
internal sealed class LogManager : IDisposable
{
    private readonly object _lock = new();
    private readonly List<string> _memoryBuffer = new(512);
    private readonly List<string> _pendingDiskLines = new(1024);
    private readonly string _logDir;
    private readonly string _currentSessionFile;
    private int _version;
    private Timer? _flushTimer;

    private const int FlushThreshold = 2000;
    private const int FlushIntervalMs = 300_000;
    private const int MemoryKeep = 500;

    public LogManager()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDir = Path.Combine(localData, "OppoPodsManager", "Logs");
        Directory.CreateDirectory(_logDir);
        _currentSessionFile = Path.Combine(_logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        _flushTimer = new Timer(_ => FlushToDisk(), null, FlushIntervalMs, FlushIntervalMs);
    }

    public int Version
    {
        get
        {
            lock (_lock)
                return _version;
        }
    }

    /// <summary>追加一行已经格式化好的日志，内存中最多保留最近 500 条。</summary>
    public void AppendRawLine(string line)
    {
        lock (_lock)
        {
            _memoryBuffer.Add(line);
            if (_memoryBuffer.Count > MemoryKeep)
                _memoryBuffer.RemoveRange(0, _memoryBuffer.Count - MemoryKeep);

            _pendingDiskLines.Add(line);
            _version++;

            if (_pendingDiskLines.Count >= FlushThreshold)
                FlushToDiskLocked();
        }
    }

    /// <summary>追加一行带时间戳和分类标签的日志。</summary>
    public void AppendLog(string tag, string message)
    {
        AppendRawLine($"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}");
    }

    /// <summary>获取内存中的最近日志行，用于 UI 显示。</summary>
    public List<string> GetRecentLines(out int version)
    {
        lock (_lock)
        {
            version = _version;
            return new List<string>(_memoryBuffer);
        }
    }

    /// <summary>获取本会话完整日志（已落盘文件 + 当前内存缓冲），用于兼容旧导出逻辑。</summary>
    public List<string> GetFullSessionLog()
    {
        lock (_lock)
        {
            FlushToDiskLocked();
            try
            {
                if (File.Exists(_currentSessionFile))
                    return File.ReadAllLines(_currentSessionFile).ToList();
            }
            catch
            {
                // 读取失败则退回内存缓冲。
            }

            return new List<string>(_memoryBuffer);
        }
    }

    private void FlushToDisk()
    {
        lock (_lock)
            FlushToDiskLocked();
    }

    private void FlushToDiskLocked()
    {
        if (_pendingDiskLines.Count == 0)
            return;

        try
        {
            File.AppendAllLines(_currentSessionFile, _pendingDiskLines);
            _pendingDiskLines.Clear();
        }
        catch
        {
            // 写入失败静默忽略，避免日志系统影响主程序。
        }
    }

    /// <summary>导出所有历史日志为 ZIP 文件。</summary>
    public void ExportLogsAsZip(string zipPath, string? extraHeader = null)
    {
        lock (_lock)
        {
            FlushToDiskLocked();

            if (File.Exists(zipPath))
                File.Delete(zipPath);

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

            if (!string.IsNullOrEmpty(extraHeader))
            {
                var infoEntry = zip.CreateEntry("system_info.txt", CompressionLevel.Optimal);
                using var writer = new StreamWriter(infoEntry.Open());
                writer.Write(extraHeader);
            }

            foreach (var file in Directory.GetFiles(_logDir, "session_*.txt"))
            {
                var name = Path.GetFileName(file);
                zip.CreateEntryFromFile(file, name, CompressionLevel.Optimal);
            }
        }
    }

    /// <summary>清理 N 天前的旧日志文件，避免历史日志无限积累。</summary>
    public void CleanOldLogs(int daysToKeep = 7)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-daysToKeep);
            foreach (var file in Directory.GetFiles(_logDir, "session_*.txt"))
            {
                if (File.GetCreationTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // 清理失败静默忽略。
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;
        FlushToDisk();
    }
}
