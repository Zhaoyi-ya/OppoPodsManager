using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace OppoPodsManager;

public sealed class LinuxRfcommStreamTransport : IPodTransport
{
    private const string Libc = "libc";
    private const int AF_BLUETOOTH = 31;
    private const int SOCK_STREAM = 1;
    private const int BTPROTO_RFCOMM = 3;
    private const int SOL_SOCKET = 1;
    private const int SO_SNDTIMEO = 21;
    private const int SO_RCVTIMEO = 20;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;
    private const int O_NONBLOCK = 2048;
    private const int EAGAIN = 11;

    [DllImport(Libc, SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport(Libc, SetLastError = true)]
    private static extern int connect(int sockfd, ref SockAddrRc addr, uint addrlen);

    [DllImport(Libc, SetLastError = true)]
    private static extern IntPtr read(int fd, byte[] buf, IntPtr count);

    [DllImport(Libc, SetLastError = true)]
    private static extern IntPtr write(int fd, byte[] buf, IntPtr count);

    [DllImport(Libc)]
    private static extern int close(int fd);

    [DllImport(Libc, SetLastError = true)]
    private static extern int setsockopt(int sockfd, int level, int optname, ref TimeVal optval, uint optlen);

    [DllImport(Libc, SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport(Libc, SetLastError = true)]
    private static extern int fcntl(int fd, int cmd);

    [StructLayout(LayoutKind.Sequential)]
    private struct SockAddrRc
    {
        public ushort rc_family;
        public byte b0, b1, b2, b3, b4, b5;
        public byte rc_channel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal
    {
        public long tv_sec;
        public long tv_usec;
    }

    private const int MaxRfcommChannel = 30;
    private const int MaxIdleTimeouts = 200;
    private const int ReadBufferSize = 512;

    // Probe is now the real battery query (0x0106), encoded per-scan for reliability

    private readonly IFrameCodec _codec = new SppFrameCodec();
    private readonly List<byte> _framer = new();
    private readonly ConcurrentQueue<PodFrame> _rxQueue = new();
    private readonly IDeviceLocator _locator;
    private readonly object _sendLock = new();

    private int _socketFd = -1;
    private Thread? _readThread;
    private volatile bool _disposed;
    private volatile bool _readLoopActive;
    private int _idleCounter;

    public LinuxRfcommStreamTransport() : this(new LinuxBluetoothLocator()) { }
    public LinuxRfcommStreamTransport(IDeviceLocator locator) { _locator = locator; }

    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected { get; private set; }
    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    public bool Connect()
    {
        try
        {
            Log.D("LXRFC", "Connect: start");
            IsConnected = false; LastError = null; _disposed = false;

            var (addr, name) = _locator.Locate();
            if (addr == 0) { LastError = "No paired OPPO device found"; return false; }
            DeviceName = name;
            Log.D("LXRFC", $"Connect: found \"{name}\" addr=0x{addr:X12}");

            int fd = ScanAndConnect(addr);
            if (fd < 0) { LastError = $"No OPPO RFCOMM channel (1-{MaxRfcommChannel})"; return false; }

            _socketFd = fd;
            _framer.Clear();
            while (_rxQueue.TryDequeue(out _)) { }

            if (fcntl(_socketFd, F_SETFL, fcntl(_socketFd, F_GETFL) | O_NONBLOCK) < 0)
                Log.D("LXRFC", "Connect: fcntl O_NONBLOCK failed");

            IsConnected = true; LastError = null; _idleCounter = 0;
            StartReadLoop();
            Log.Result("LXRFC", "Connect", true, $"\"{name}\" fd={fd}");
            return true;
        }
        catch (Exception e) { LastError = e.Message; Log.Ex("LXRFC", "Connect", e); CleanupSocket(); return false; }
    }

    private int ScanAndConnect(ulong addr)
    {
        Log.D("LXRFC", $"Scan: starting ch 1-{MaxRfcommChannel}");
        var probeBytes = _codec.Encode(OppoProtocol.CmdBattery, Array.Empty<byte>());
        var recvBuf = new byte[64];
        int errRefused = 0, errTimeout = 0, errOther = 0;
        var openChannels = new List<string>();

        for (int ch = 1; ch <= MaxRfcommChannel; ch++)
        {
            int fd = socket(AF_BLUETOOTH, SOCK_STREAM, BTPROTO_RFCOMM);
            if (fd < 0) { errOther++; continue; }

            var sockAddr = BuildSockAddr(addr, ch);
            var tvConn = new TimeVal { tv_sec = 0, tv_usec = 200000 };
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, ref tvConn, 16);

            int cr = connect(fd, ref sockAddr, 10);
            if (cr < 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == 111) errRefused++;
                else if (err == 110) errTimeout++;
                else { errOther++; Log.D("LXRFC", $"Scan: ch={ch} connect errno={err}"); }
                close(fd); continue;
            }

            IntPtr wrote = write(fd, probeBytes, (IntPtr)probeBytes.Length);
            if (wrote == (IntPtr)(-1) || wrote == IntPtr.Zero) { close(fd); continue; }

            var tvRead = new TimeVal { tv_sec = 0, tv_usec = 200000 };
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, ref tvRead, 16);

            IntPtr got = read(fd, recvBuf, (IntPtr)recvBuf.Length);
            int n = (int)(long)got;

            if (n > 0)
            {
                if (recvBuf[0] == SppFrameCodec.Header)
                {
                    Log.D("LXRFC", $"Scan: ch={ch} PROBE OK! reusing socket fd={fd}");
                    while (true)
                    {
                        Thread.Sleep(30);
                        got = read(fd, recvBuf, (IntPtr)recvBuf.Length);
                        if ((long)got <= 0) break;
                    }
                    return fd;
                }
                string hex = BitConverter.ToString(recvBuf, 0, Math.Min(n, 10));
                openChannels.Add($"ch={ch}:0x{recvBuf[0]:X2}({hex})");
            }
            close(fd);
        }

        Log.D("LXRFC", $"Scan: {MaxRfcommChannel} ch done. refused={errRefused} timeout={errTimeout} other={errOther}");
        if (openChannels.Count > 0) Log.D("LXRFC", $"Scan: open (wrong proto): {string.Join(", ", openChannels)}");
        return -1;
    }

    private static SockAddrRc BuildSockAddr(ulong addr, int channel) => new()
    {
        rc_family = AF_BLUETOOTH,
        b0 = (byte)(addr & 0xFF),
        b1 = (byte)((addr >> 8) & 0xFF),
        b2 = (byte)((addr >> 16) & 0xFF),
        b3 = (byte)((addr >> 24) & 0xFF),
        b4 = (byte)((addr >> 32) & 0xFF),
        b5 = (byte)((addr >> 40) & 0xFF),
        rc_channel = (byte)channel,
    };

    private void StartReadLoop()
    {
        _readLoopActive = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "LXRFC-Read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buf = new byte[ReadBufferSize];
        int fd = _socketFd;
        try
        {
            while (_readLoopActive && !_disposed)
            {
                if (fd < 0) break;
                IntPtr got;
                try { got = read(fd, buf, (IntPtr)buf.Length); }
                catch { break; }
                int n = (int)(long)got;
                if (n > 0)
                {
                    _idleCounter = 0;
                    lock (_framer)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            _framer.Add(buf[i]);
                            // Dispatch frames immediately — don't wait for Poll()
                            while (_codec.TryDecode(_framer, out var frame))
                            {
                                _rxQueue.Enqueue(frame);
                                try { FrameReceived?.Invoke(frame); }
                                catch (Exception ex) { Log.Ex("LXRFC", "ReadLoop dispatch", ex); }
                            }
                        }
                    }
                }
                else if (n == 0) { Log.D("LXRFC", "ReadLoop: peer closed"); break; }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == EAGAIN)
                    {
                        _idleCounter++;
                        if (_idleCounter > MaxIdleTimeouts) { Log.D("LXRFC", "ReadLoop: idle timeout"); break; }
                        Thread.Sleep(50); continue;
                    }
                    Log.D("LXRFC", $"ReadLoop: read errno={err}"); break;
                }
            }
        }
        catch (Exception ex) { if (_readLoopActive) Log.Ex("LXRFC", "ReadLoop", ex); }
        finally { _readLoopActive = false; if (IsConnected) OnDisconnected(); }
    }

    public void Send(ushort cmd, byte[] payload)
    {
        _idleCounter = 0;
        if (!IsConnected || _socketFd < 0) return;
        byte[] bytes;
        lock (_sendLock) { bytes = _codec.Encode(cmd, payload); }
        try
        {
            lock (_sendLock)
            {
                IntPtr w = write(_socketFd, bytes, (IntPtr)bytes.Length);
                if (w == (IntPtr)(-1)) Log.D("LXRFC", $"Send 0x{cmd:X4} failed errno={Marshal.GetLastWin32Error()}");
            }
        }
        catch (Exception ex) { Log.Ex("LXRFC", $"Send 0x{cmd:X4}", ex); }
    }

    public void Poll(int timeoutMs)
    {
        _idleCounter = 0;
        if (!IsConnected) return;
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            while (_rxQueue.TryDequeue(out var frame)) FrameReceived?.Invoke(frame);
            if (!IsConnected || !_readLoopActive) return;
            if (DateTime.UtcNow >= end) break;
            Thread.Sleep(20);
        }
        while (_rxQueue.TryDequeue(out var frame)) FrameReceived?.Invoke(frame);
    }

    private void OnDisconnected() { if (!IsConnected) return; IsConnected = false; Disconnected?.Invoke(); }
    public void Close() { IsConnected = false; _readLoopActive = false; CleanupSocket(); }
    private void CleanupSocket() { var fd = Interlocked.Exchange(ref _socketFd, -1); if (fd >= 0) close(fd); }
    public void Dispose() { if (_disposed) return; _disposed = true; Close(); }
}
