using System.Diagnostics;
using OppoPodsManager.Util;

namespace OppoPodsManager;

/// <summary>捕获 Trace.WriteLine 输出（含 Log.D/Ex/Result），转发到 LogManager。</summary>
internal sealed class LogTraceListener : TraceListener
{
    private readonly LogManager _logManager;

    public LogTraceListener(LogManager logManager) => _logManager = logManager;

    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (message != null)
            _logManager.AppendRawLine(message);
    }
}
