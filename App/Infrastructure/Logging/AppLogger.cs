using System;
using System.IO;
using System.Text;

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

    static Log()
    {
        // VSCode 终端默认 UTF-8，Windows 控制台默认 GBK → 中文乱码。
        // 显式设置确保中文日志正确显示。
        Console.OutputEncoding = Encoding.UTF8;
    }

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
        D(tag, $"{context} EXCEPTION: {DescribeException(ex)}");
    }

    /// <summary>
    /// 把异常链展开成单行可读描述：TypeName(HRESULT=0x..., "解码"): message  &lt;- InnerType: inner message ...
    /// AggregateException 会展开其 InnerExceptions（单个直接展开，多个用 " | " 连接）。
    /// 关键作用：避免只打印顶层 "AggregateException: One or more errors occurred ()" 而吞掉真正原因。
    /// </summary>
    public static string DescribeException(Exception ex)
    {
        var sb = new StringBuilder();
        AppendException(sb, ex, depth: 0);
        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        if (ex is AggregateException agg)
        {
            var flat = agg.Flatten();
            if (flat.InnerExceptions.Count == 1)
            {
                AppendException(sb, flat.InnerExceptions[0], depth);
                return;
            }
            sb.Append("AggregateException[").Append(flat.InnerExceptions.Count).Append("]{ ");
            for (int i = 0; i < flat.InnerExceptions.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                AppendException(sb, flat.InnerExceptions[i], depth);
            }
            sb.Append(" }");
            return;
        }

        sb.Append(ex.GetType().Name);

        int hr = ex.HResult;
        if (hr != 0)
        {
            sb.Append("(HRESULT=0x").Append(hr.ToString("X8"));
            var decoded = DescribeHResult(hr);
            if (decoded != null) sb.Append(", ").Append(decoded);
            sb.Append(')');
        }

        if (!string.IsNullOrEmpty(ex.Message))
            sb.Append(": ").Append(ex.Message.Replace("\r", " ").Replace("\n", " ").Trim());

        if (ex.InnerException != null && depth < 6)
        {
            sb.Append("  <- ");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    /// <summary>把 HRESULT 解码成可读说明；0x8007xxxx 形态内嵌 Win32/WSA 错误码。</summary>
    public static string? DescribeHResult(int hr)
    {
        if ((hr & 0xFFFF0000) == unchecked((int)0x80070000))
        {
            int win32 = hr & 0xFFFF;
            var name = DescribeWsaOrWin32(win32);
            return name != null ? $"Win32={win32}({name})" : $"Win32={win32}";
        }

        return hr switch
        {
            unchecked((int)0x8000000E) => "E_ILLEGAL_METHOD_CALL(对象状态非法/已释放)",
            unchecked((int)0x80004004) => "E_ABORT(操作被中止)",
            unchecked((int)0x8001010E) => "RPC_E_WRONG_THREAD(跨线程访问 STA 对象)",
            unchecked((int)0x800710DF) => "ERROR_DEVICE_NOT_AVAILABLE(设备不可用/未就绪)",
            unchecked((int)0x8007048F) => "ERROR_DEVICE_NOT_CONNECTED(设备未连接)",
            _ => null
        };
    }

    /// <summary>WSA/Win32 错误码 → 可读名称。用于 SppTransport 里裸露的 WSAErr 数字。</summary>
    public static string? DescribeWsaOrWin32(int code) => code switch
    {
        10013 => "WSAEACCES 权限被拒(设备可能被独占/被手机占用)",
        10035 => "WSAEWOULDBLOCK 非阻塞暂不可完成",
        10036 => "WSAEINPROGRESS 操作进行中",
        10037 => "WSAEALREADY 操作已在进行(socket 已发起过 connect,须新建)",
        10038 => "WSAENOTSOCK 句柄不是 socket",
        10047 => "WSAEAFNOSUPPORT 地址族不支持",
        10048 => "WSAEADDRINUSE 地址已被占用",
        10049 => "WSAEADDRNOTAVAIL 地址不可用",
        10050 => "WSAENETDOWN 网络/适配器已关闭",
        10051 => "WSAENETUNREACH 网络不可达",
        10053 => "WSAECONNABORTED 连接被中止",
        10054 => "WSAECONNRESET 连接被对端重置",
        10057 => "WSAENOTCONN socket 未连接",
        10060 => "WSAETIMEDOUT 连接超时(设备无响应/信道忙)",
        10061 => "WSAECONNREFUSED 连接被拒绝(服务未监听/信道错误)",
        10064 => "WSAEHOSTDOWN 主机已关闭",
        10065 => "WSAEHOSTUNREACH 主机不可达(耳机不在范围/未开机)",
        10024 => "WSAEMFILE 句柄耗尽",
        10022 => "WSAEINVAL 参数非法",
        _ => null
    };

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
