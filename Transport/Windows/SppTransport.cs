using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace OppoPodsManager;

/// <summary>
/// 经典蓝牙 SPP / RFCOMM 传输实现（Winsock2 AF_BTH）。
/// 负责：设备发现、连接（带超时）、收发字节，并用 SppFrameCodec 做帧编解码。
/// 纯 P/Invoke，AOT 友好。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SppTransport : IPodTransport
{
    private IntPtr _socket = IntPtr.Zero;
    private readonly object _lock = new();
    private readonly byte[] _recvBuf = new byte[512];
    private readonly List<byte> _framer = new();
    private readonly IFrameCodec _codec = new SppFrameCodec();
    private readonly IDeviceLocator _locator;

    /// <summary>默认用 WinRT 发现（WindowsRfcommLocator，内部回退注册表）；可注入其它 IDeviceLocator。</summary>
    /// <summary>默认使用 WinRT RFCOMM 发现器（RfcommServiceFinder）；可注入其它 IDeviceLocator。</summary>
    public SppTransport() : this(new WindowsRfcommLocator()) { }
    public SppTransport(IDeviceLocator locator) { _locator = locator; }
    private bool _disposed;

    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    // WinSock2 P/Invoke 声明
    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSAStartup(ushort version, IntPtr data);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int WSACleanup();

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr s, IntPtr addr, int addrLen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int send(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int recv(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int closesocket(IntPtr s);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int getsockopt(IntPtr s, int level, int optname, ref int optval, ref int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int ioctlsocket(IntPtr s, int cmd, ref uint argp);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int select(int nfds, IntPtr readfds, ref FdSet writefds, ref FdSet exceptfds, ref TimeVal timeout);

    [DllImport("ws2_32.dll")]
    private static extern int WSAGetLastError();

    private const int AF_BTH = 32;
    private const int SOCK_STREAM = 1;
    private const int BTHPROTO_RFCOMM = 3;
    // Winsock 的 socket() 失败返回 INVALID_SOCKET = (SOCKET)(~0)，即指针宽度全 1
    private static readonly IntPtr InvalidSocket = new(-1);

    // Winsock 常量
    private const int SOL_SOCKET = 0xFFFF;
    private const int SO_RCVTIMEO = 0x1006;
    private const int SO_ERROR = 0x1007;
    private const int WSAEWOULDBLOCK = 10035;
    private const int WSAETIMEDOUT = 10060;
    private const int FIONBIO = unchecked((int)0x8004667E);
    private const int RecvTimeoutMs = 400;      // recv 单次阻塞上限，保证 ReadResponses 超时生效
    private const int ConnectTimeoutMs = 4000;  // 单次 connect 尝试上限

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal
    {
        public int tv_sec;
        public int tv_usec;
    }

    // 仅需单 socket，fd_count=1，slot0 存 socket 句柄（x64 布局与 Windows fd_set 一致）
    [StructLayout(LayoutKind.Sequential)]
    private struct FdSet
    {
        public uint fd_count;
        public IntPtr fd_array0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockAddrBth
    {
        public ushort family;
        public ulong btAddr;
        public Guid serviceClassId;
        public uint port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WSAData
    {
        public ushort version;
        public ushort highVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)] public string systemStatus;
        public ushort maxSockets;
        public ushort maxUdpDg;
        public IntPtr vendorInfo;
    }

    // WSAStartup 全进程只需一次；用静态标志保证幂等，避免重连时反复启动/引用计数失衡
    private static int _wsaStarted;
    private static void EnsureWsaStarted()
    {
        if (Interlocked.CompareExchange(ref _wsaStarted, 1, 0) != 0) return;
        var wsaPtr = Marshal.AllocHGlobal(512);
        try { WSAStartup(0x0202, wsaPtr); }
        finally { Marshal.FreeHGlobal(wsaPtr); }
    }

    /// <summary>发现设备 → 创建 BTH socket → 带超时非阻塞 connect → 恢复阻塞模式读取。</summary>
    public bool Connect()
    {
        try
        {
            Log.D("BT", "Connect: 开始 (Winsock SPP 回退层)");
            EnsureWsaStarted();
            CloseSocket();

            var swLoc = System.Diagnostics.Stopwatch.StartNew();
            var (addr, name) = _locator.Locate();
            Log.D("BT", $"Connect: 设备定位耗时{swLoc.ElapsedMilliseconds}ms -> addr={addr:X12} name=\"{name}\"");
            if (addr == 0)
            {
                LastError = "未找到已配对的 OPPO 设备";
                Log.Result("BT", "Connect", false, LastError);
                return false;
            }
            DeviceName = name ?? ("耳机 " + addr.ToString("X12"));
            Log.D("BT", $"Connect: 目标 addr={addr:X12} name=\"{DeviceName}\"");

            // 按优先级尝试多种连接方式（UUID+port 组合），部分设备需要特定参数。
            // 每种方式在 TryConnect 内部各自创建全新 socket：Winsock 对一个已发起过
            // connect 并失败/超时的 socket 再次 connect 会返回 WSAEALREADY(10037)，
            // 故必须“一次尝试一 socket”，失败即关闭重建，否则后续尝试全部无效。
            bool ok = TryConnect(addr, OppoProtocol.OppoSppUuid, 0)
                   || TryConnect(addr, OppoProtocol.OppoSppUuid, 15)
                   || TryConnect(addr, Guid.Empty, 15)
                   || TryConnect(addr, Guid.Empty, 1);
            if (!ok)
            {
                CloseSocket();
                LastError = "无法连接耳机（已尝试 4 种连接方式，各方式失败原因见上方 TryConnect 日志）";
                Log.Result("BT", "Connect", false, LastError);
                return false;
            }

            // 接收超时，保证 Poll 的时间预算生效（否则 recv 无限阻塞）
            int rcvTimeout = RecvTimeoutMs;
            setsockopt(_socket, SOL_SOCKET, SO_RCVTIMEO, ref rcvTimeout, sizeof(int));

            _framer.Clear();
            IsConnected = true;
            LastError = null;
            Log.Result("BT", "Connect", true, $"name=\"{DeviceName}\"");
            return true;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log.Ex("BT", "Connect", e);
            return false;
        }
    }

    /// <summary>关闭当前 socket 并清零句柄（幂等）</summary>
    private void CloseSocket()
    {
        lock (_lock)
        {
            if (_socket != IntPtr.Zero)
            {
                closesocket(_socket);
                _socket = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// 尝试通过指定参数建立 BTH RFCOMM 连接（带超时，避免不可达设备长时间阻塞）。
    /// 每次调用创建全新 socket，成功则赋给 _socket 并返回 true；失败则立即关闭该 socket，
    /// 保证下一种连接方式从干净状态发起（否则复用已失败 socket 会得到 WSAEALREADY=10037）。
    /// </summary>
    private bool TryConnect(ulong btAddr, Guid serviceUuid, uint port)
    {
        // 每种尝试用统一标签，便于在日志里对齐“同一次尝试”的各行输出
        string uuidTag = serviceUuid == Guid.Empty ? "empty" : serviceUuid.ToString("D").Substring(0, 8);
        string label = $"TryConnect(addr={btAddr:X12}, uuid={uuidTag}, port={port})";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 每种连接方式独占一个新建 socket
        IntPtr sock = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
        if (sock == InvalidSocket || sock == IntPtr.Zero)
        {
            int se = WSAGetLastError();
            Log.Result("BT", $"{label} socket()", false, Wsa(se));
            return false;
        }
        Log.D("BT", $"{label} 新建 socket=0x{sock.ToInt64():X}");

        var sa = new SockAddrBth
        {
            family = AF_BTH,
            btAddr = btAddr,
            serviceClassId = serviceUuid,
            port = port
        };
        int saSize = Marshal.SizeOf(sa);
        var saPtr = Marshal.AllocHGlobal(saSize);
        Marshal.StructureToPtr(sa, saPtr, false);

        bool connected = false;
        try
        {
            // 切到非阻塞，发起 connect 后用 select 等待可写，超时即判失败
            uint nonBlocking = 1;
            ioctlsocket(sock, FIONBIO, ref nonBlocking);

            int result = connect(sock, saPtr, saSize);
            if (result == 0)
            {
                Log.Result("BT", label, true, $"立即连通 (耗时{sw.ElapsedMilliseconds}ms)");
                connected = RestoreBlocking(sock);
                return connected;
            }

            int err = WSAGetLastError();
            if (err != WSAEWOULDBLOCK)
            {
                // 非 WOULDBLOCK 即真正的 connect 失败（如 10037/10060/10050），直接给出解码
                Log.Result("BT", label, false, $"connect {Wsa(err)} (耗时{sw.ElapsedMilliseconds}ms)");
                return false;
            }

            // 进入等待：非阻塞 connect 已发起，用 select 等 socket 可写
            var writefds = new FdSet { fd_count = 1, fd_array0 = sock };
            var exceptfds = new FdSet { fd_count = 1, fd_array0 = sock };
            var tv = new TimeVal { tv_sec = ConnectTimeoutMs / 1000, tv_usec = (ConnectTimeoutMs % 1000) * 1000 };

            int sel = select(0, IntPtr.Zero, ref writefds, ref exceptfds, ref tv);
            if (sel == 0)
            {
                Log.Result("BT", label, false, $"select 超时 (预算{ConnectTimeoutMs}ms, 实耗{sw.ElapsedMilliseconds}ms; 设备无响应/信道忙/被手机占用)");
                return false;
            }
            if (sel < 0)
            {
                Log.Result("BT", label, false, $"select 错误 {Wsa(WSAGetLastError())} (耗时{sw.ElapsedMilliseconds}ms)");
                return false;
            }

            // 用 SO_ERROR 确认 connect 结果（select 可写也可能是失败，须复核）
            int soError = 0, len = sizeof(int);
            int gso = getsockopt(sock, SOL_SOCKET, SO_ERROR, ref soError, ref len);
            if (gso != 0)
            {
                Log.Result("BT", label, false, $"getsockopt(SO_ERROR) 失败 {Wsa(WSAGetLastError())} (耗时{sw.ElapsedMilliseconds}ms)");
                return false;
            }
            if (soError != 0)
            {
                Log.Result("BT", label, false, $"SO_ERROR={Wsa(soError)} (耗时{sw.ElapsedMilliseconds}ms)");
                return false;
            }

            Log.Result("BT", label, true, $"耗时{sw.ElapsedMilliseconds}ms");
            connected = RestoreBlocking(sock);
            return connected;
        }
        finally
        {
            Marshal.FreeHGlobal(saPtr);
            if (connected)
            {
                // 成功：本 socket 成为活动连接
                _socket = sock;
            }
            else
            {
                // 失败：关闭本次尝试的 socket，避免句柄泄漏与 WSAEALREADY
                closesocket(sock);
            }
        }
    }

    /// <summary>把 WSA 错误码格式化为 "WSAErr=码(名称)"，名称来自 Log 的解码表。</summary>
    private static string Wsa(int code)
    {
        var name = Log.DescribeWsaOrWin32(code);
        return name != null ? $"WSAErr={code}({name})" : $"WSAErr={code}";
    }

    /// <summary>connect 成功后恢复阻塞模式（recv 靠 SO_RCVTIMEO 控制超时）</summary>
    private static bool RestoreBlocking(IntPtr sock)
    {
        uint blocking = 0;
        ioctlsocket(sock, FIONBIO, ref blocking);
        return true;
    }
    /// <summary>编码并发送一帧（Winsock send，socket 锁保护）。</summary>
    public void Send(ushort cmd, byte[] payload)
    {
        var bytes = _codec.Encode(cmd, payload);
        lock (_lock)
        {
            if (_socket == IntPtr.Zero)
            {
                Log.D("BT", $"Send cmd=0x{cmd:X4} 失败: socket 已关闭");
                return;
            }
            int sent = send(_socket, bytes, bytes.Length, 0);
            if (sent < 0)
                Log.Result("BT", $"Send cmd=0x{cmd:X4} ({bytes.Length}B)", false, Wsa(WSAGetLastError()));
            else
                Log.D("BT", $"Send cmd=0x{cmd:X4} payload={payload?.Length ?? 0}B -> {sent}B");
        }
    }

    /// <summary>在 timeoutMs 预算内循环 recv 字节，解帧后交付绑定事件。</summary>
    public void Poll(int timeoutMs)
    {
        if (_socket == IntPtr.Zero) return;
        var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < endTime)
        {
            int got = recv(_socket, _recvBuf, _recvBuf.Length, 0);
            if (got == 0) { Log.D("BT", "Poll: 对端关闭连接 (recv=0)"); OnDisconnected(); return; }  // 对端正常关闭
            if (got < 0)
            {
                int err = WSAGetLastError();
                // 超时/暂时无数据：本轮结束，正常返回
                if (err == WSAETIMEDOUT || err == WSAEWOULDBLOCK) return;
                Log.D("BT", $"Poll: recv 错误 {Wsa(err)}, 判定断开");
                OnDisconnected();
                return;
            }

            Log.D("BT", $"Poll: recv {got}B");
            for (int i = 0; i < got; i++)
            {
                _framer.Add(_recvBuf[i]);
                while (_codec.TryDecode(_framer, out var frame))
                {
                    Log.D("BT", $"Poll: 解出帧 cmd=0x{frame.Cmd:X4} payload={frame.Payload.Length}B");
                    FrameReceived?.Invoke(frame);
                }
            }
        }
    }

    private void OnDisconnected()
    {
        Log.D("BT", "OnDisconnected: 链路断开");
        IsConnected = false;
        CloseSocket();
        Disconnected?.Invoke();
    }

    /// <summary>断开连接并关闭 socket（幂等）。</summary>
    public void Close()
    {
        IsConnected = false;
        CloseSocket();
    }

    /// <summary>释放传输资源（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
    }
}
