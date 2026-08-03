using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Communication.Windows;

// 使用原项目验证过的 Winsock RFCOMM 通道承载原始 Melody 字节流。
[SupportedOSPlatform("windows")]
public sealed class WindowsSppConnection : IRawConnection
{
    public static readonly Guid MelodyServiceId = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private const int AfBth = 32;
    private const int SockStream = 1;
    private const int BthProtoRfcomm = 3;
    private const int SolSocket = 0xFFFF;
    private const int SoReceiveTimeout = 0x1006;
    private const int SoError = 0x1007;
    private const int WsaWouldBlock = 10035;
    private const int WsaTimedOut = 10060;
    private const int FionBio = unchecked((int)0x8004667E);
    private static readonly IntPtr InvalidSocket = new(-1);
    private static readonly object WsaGate = new();
    private static int _wsaStarted;

    private readonly DeviceCandidate _candidate;
    private readonly Guid _serviceId;
    private readonly object _socketGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IntPtr _socket;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private int _connected;
    private int _disposed;

    public WindowsSppConnection(DeviceCandidate candidate, Guid? serviceId)
    {
        _candidate = candidate;
        _serviceId = serviceId ?? MelodyServiceId;
    }

    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    public event EventHandler? Disconnected;

    // 在工作线程内执行短时的原生连接尝试，避免阻塞 Avalonia UI 线程。
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsConnected)
            return;
        if (!ulong.TryParse(_candidate.BluetoothAddress, System.Globalization.NumberStyles.HexNumber, null, out var address))
            throw new InvalidOperationException("RFCOMM requires a Bluetooth device address.");

        var socket = await Task.Run(() => ConnectCore(address, cancellationToken), cancellationToken);
        lock (_socketGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || cancellationToken.IsCancellationRequested)
            {
                closesocket(socket);
                cancellationToken.ThrowIfCancellationRequested();
                throw new ObjectDisposedException(nameof(WindowsSppConnection));
            }
            _socket = socket;
            Volatile.Write(ref _connected, 1);
            _readCancellation = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
        }
        ApplicationLog.Current?.Info("Bluetooth", $"RFCOMM 已连接：{_candidate.DisplayName} ({address:X12})。");
    }

    // 串行写入并处理底层可能发生的部分发送。
    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new InvalidOperationException("RFCOMM is not connected.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var offset = 0;
            var bytes = data.ToArray();
            while (offset < bytes.Length)
            {
                IntPtr socket;
                lock (_socketGate) socket = _socket;
                var remaining = offset == 0 ? bytes : bytes[offset..];
                var sent = send(socket, remaining, remaining.Length, 0);
                if (sent <= 0)
                    throw new InvalidOperationException($"RFCOMM send failed ({WSAGetLastError()}).");
                offset += sent;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // 关闭 socket 会立即解除 recv 阻塞，随后等待读取任务安全退出。
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var wasConnected = Interlocked.Exchange(ref _connected, 0) != 0;
        _readCancellation?.Cancel();
        CloseSocket();
        var readTask = _readTask;
        if (readTask is not null)
        {
            try { await readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { ApplicationLog.Current?.Error("Bluetooth", "RFCOMM 读取任务结束异常。", exception); }
        }
        _readCancellation?.Dispose();
        _readCancellation = null;
        _readTask = null;
        if (wasConnected)
            ApplicationLog.Current?.Info("Bluetooth", "RFCOMM 已断开。");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync(CancellationToken.None);
        _writeGate.Dispose();
    }

    // 连续读取原始字节块，帧边界由 ConnectionLink 的 FrameCodec 维护。
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                IntPtr socket;
                lock (_socketGate) socket = _socket;
                var received = await Task.Run(() => recv(socket, buffer, buffer.Length, 0), cancellationToken);
                if (received > 0)
                {
                    DataReceived?.Invoke(this, buffer.AsMemory(0, received).ToArray());
                    continue;
                }
                if (received < 0 && (WSAGetLastError() == WsaTimedOut || WSAGetLastError() == WsaWouldBlock))
                    continue;
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Bluetooth", "RFCOMM 接收失败。", exception);
        }
        finally
        {
            if (Interlocked.Exchange(ref _connected, 0) != 0 && Volatile.Read(ref _disposed) == 0 && !cancellationToken.IsCancellationRequested)
                Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // 依次采用 SDP UUID、通道 15 和通道 1，兼容不同固件的服务暴露方式。
    private IntPtr ConnectCore(ulong address, CancellationToken cancellationToken)
    {
        EnsureWsaStarted();
        foreach (var attempt in new[] { (_serviceId, 0u), (Guid.Empty, 15u), (Guid.Empty, 1u) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = TryConnect(address, attempt.Item1, attempt.Item2);
            if (socket != IntPtr.Zero)
                return socket;
        }
        throw new InvalidOperationException("Melody RFCOMM service is unavailable.");
    }

    private static IntPtr TryConnect(ulong address, Guid serviceId, uint port)
    {
        var nativeSocket = socket(AfBth, SockStream, BthProtoRfcomm);
        if (nativeSocket == IntPtr.Zero || nativeSocket == InvalidSocket)
            return IntPtr.Zero;
        var endpoint = new SockAddrBth { Family = AfBth, Address = address, ServiceClassId = serviceId, Port = port };
        var endpointSize = Marshal.SizeOf<SockAddrBth>();
        var endpointPointer = Marshal.AllocHGlobal(endpointSize);
        try
        {
            Marshal.StructureToPtr(endpoint, endpointPointer, false);
            uint nonBlocking = 1;
            ioctlsocket(nativeSocket, FionBio, ref nonBlocking);
            if (connect(nativeSocket, endpointPointer, endpointSize) != 0 && WSAGetLastError() != WsaWouldBlock)
                return CloseFailedSocket(nativeSocket);
            var write = new FdSet { Count = 1, Socket = nativeSocket };
            var errors = new FdSet { Count = 1, Socket = nativeSocket };
            var timeout = new TimeVal { Seconds = 0, Microseconds = 500_000 };
            if (select(0, IntPtr.Zero, ref write, ref errors, ref timeout) <= 0)
                return CloseFailedSocket(nativeSocket);
            var error = 0;
            var errorSize = sizeof(int);
            if (getsockopt(nativeSocket, SolSocket, SoError, ref error, ref errorSize) != 0 || error != 0)
                return CloseFailedSocket(nativeSocket);
            nonBlocking = 0;
            ioctlsocket(nativeSocket, FionBio, ref nonBlocking);
            var receiveTimeout = 400;
            setsockopt(nativeSocket, SolSocket, SoReceiveTimeout, ref receiveTimeout, sizeof(int));
            return nativeSocket;
        }
        finally
        {
            Marshal.FreeHGlobal(endpointPointer);
        }
    }

    private void CloseSocket()
    {
        IntPtr socket;
        lock (_socketGate)
        {
            socket = _socket;
            _socket = IntPtr.Zero;
        }
        if (socket != IntPtr.Zero)
            closesocket(socket);
    }

    private static IntPtr CloseFailedSocket(IntPtr socket)
    {
        closesocket(socket);
        return IntPtr.Zero;
    }

    private static void EnsureWsaStarted()
    {
        if (Volatile.Read(ref _wsaStarted) != 0)
            return;
        lock (WsaGate)
        {
            if (_wsaStarted != 0)
                return;
            var data = Marshal.AllocHGlobal(512);
            try
            {
                if (WSAStartup(0x0202, data) != 0)
                    throw new InvalidOperationException("WSAStartup failed.");
                Volatile.Write(ref _wsaStarted, 1);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockAddrBth
    {
        public ushort Family;
        public ulong Address;
        public Guid ServiceClassId;
        public uint Port;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FdSet { public uint Count; public IntPtr Socket; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal { public int Seconds; public int Microseconds; }

    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int WSAStartup(ushort version, IntPtr data);
    [DllImport("ws2_32.dll")] private static extern IntPtr socket(int addressFamily, int type, int protocol);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int connect(IntPtr socket, IntPtr address, int addressLength);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int send(IntPtr socket, byte[] buffer, int length, int flags);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int recv(IntPtr socket, byte[] buffer, int length, int flags);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int closesocket(IntPtr socket);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int setsockopt(IntPtr socket, int level, int option, ref int value, int length);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int getsockopt(IntPtr socket, int level, int option, ref int value, ref int length);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int ioctlsocket(IntPtr socket, int command, ref uint argument);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int select(int ignored, IntPtr read, ref FdSet write, ref FdSet errors, ref TimeVal timeout);
    [DllImport("ws2_32.dll")] private static extern int WSAGetLastError();
}
