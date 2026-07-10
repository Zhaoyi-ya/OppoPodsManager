using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace OppoPodsManager.Util;

/// <summary>日志管理器：定期落盘、内存缓冲、简化版/完整版切换、ZIP 导出。</summary>
internal sealed class LogManager : IDisposable
{
    private readonly object _lock = new();
    private readonly List<string> _memoryBuffer = new(512); // 内存缓冲最近 N 条
    private readonly string _logDir;
    private string _currentSessionFile;
    private int _memoryLineCount;
    private Timer? _flushTimer;
    private const int FlushThreshold = 1000; // 累计 N 条触发落盘
    private const int FlushIntervalMs = 300_000; // 或每 5 分钟落盘
    private const int MemoryKeep = 500; // 内存保留最近 500 条

    public LogManager()
    {
        // 日志目录：%LocalAppData%\OppoPodsManager\Logs
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _logDir = Path.Combine(localData, "OppoPodsManager", "Logs");
        Directory.CreateDirectory(_logDir);

        // 会话文件名：Logs\session_yyyyMMdd_HHmmss.txt
        _currentSessionFile = Path.Combine(_logDir, $"session_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        // 定时器：每 60 秒自动落盘
        _flushTimer = new Timer(_ => FlushToDisk(), null, FlushIntervalMs, FlushIntervalMs);
    }

    /// <summary>追加一行日志（格式：HH:mm:ss.fff [TAG] message）。</summary>
    public void AppendLog(string tag, string message)
    {
        lock (_lock)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {message}";
            _memoryBuffer.Add(line);
            _memoryLineCount++;

            // 达到阈值 → 落盘
            if (_memoryLineCount >= FlushThreshold)
                FlushToDisk();
        }
    }

    /// <summary>获取内存中的最近日志行（用于 UI 显示）。</summary>
    public List<string> GetRecentLines()
    {
        lock (_lock)
            return new List<string>(_memoryBuffer);
    }

    /// <summary>获取本会话完整日志（已落盘文件 + 当前内存缓冲），用于反馈导出。</summary>
    public List<string> GetFullSessionLog()
    {
        lock (_lock)
        {
            // 先落盘，保证磁盘文件是最新的
            FlushToDisk();

            var result = new List<string>();
            try
            {
                if (File.Exists(_currentSessionFile))
                    result.AddRange(File.ReadAllLines(_currentSessionFile));
            }
            catch
            {
                // 读取失败则退回内存缓冲
            }
            // 追加落盘后仍留在内存的最近行（FlushToDisk 会保留 MemoryKeep 条，避免重复需去重）
            // 由于 FlushToDisk 已写入磁盘，这里内存中的行已在文件里，无需重复追加。
            return result.Count > 0 ? result : new List<string>(_memoryBuffer);
        }
    }

    /// <summary>将内存缓冲写入当前会话文件，并清理旧缓冲（保留最近 N 条）。</summary>
    private void FlushToDisk()
    {
        lock (_lock)
        {
            if (_memoryBuffer.Count == 0) return;

            try
            {
                File.AppendAllLines(_currentSessionFile, _memoryBuffer);

                // 清理：只保留最近 MemoryKeep 条
                if (_memoryBuffer.Count > MemoryKeep)
                {
                    var keep = _memoryBuffer.Skip(_memoryBuffer.Count - MemoryKeep).ToList();
                    _memoryBuffer.Clear();
                    _memoryBuffer.AddRange(keep);
                }
                else
                {
                    _memoryBuffer.Clear();
                }

                _memoryLineCount = 0;
            }
            catch
            {
                // 写入失败静默忽略（避免日志错误导致崩溃）
            }
        }
    }

    /// <summary>导出所有历史日志为 ZIP 文件。</summary>
    /// <param name="zipPath">目标 ZIP 路径（如：桌面\OPPOPods_logs_yyyyMMdd_HHmmss.zip）。</param>
    /// <param name="extraHeader">可选：额外写入 ZIP 内 system_info.txt 的系统信息文本。</param>
    public void ExportLogsAsZip(string zipPath, string? extraHeader = null)
    {
        lock (_lock)
        {
            // 先强制落盘当前缓冲
            FlushToDisk();

            try
            {
                // 删除已存在的同名 ZIP
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                // 打包所有 session_*.txt 文件
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

                // 写入系统信息文件（反馈用）
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
            catch
            {
                // 创建 ZIP 失败静默忽略
            }
        }
    }

    /// <summary>清理 N 天前的旧日志文件（避免无限积累）。</summary>
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
            // 清理失败静默忽略
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        FlushToDisk(); // 最终落盘
    }
}
