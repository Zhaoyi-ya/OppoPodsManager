using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Communication.Windows;

// Windows 经典蓝牙 RFCOMM 原始字节连接。帧编码和协议路由由 Control 层负责。
[SupportedOSPlatform("windows")]
public sealed class WindowsRfcommConnection : IRawConnection
{
    // 部分固件只以标准 SPP UUID 暴露 RFCOMM，需要按它去查真实通道。
    private static readonly Guid GenericSppServiceId = new("00001101-0000-1000-8000-00805F9B34FB");
    // SDP 公共浏览组根：serviceId 为 null（全盘枚举）时用它做 service search。
    // NULL service class 的查询在 Windows 上会退化（Begin 成功但枚举不出任何服务）；
    // 所有服务默认都注册在公共浏览组下，按 browse root 搜索等价于遍历该设备的全部 SDP 服务。
    private static readonly Guid PublicBrowseGroupRootId = new("00001002-0000-1000-8000-00805F9B34FB");
    private const int AfBth = 32;
    private const int SockStream = 1;
    private const int BthProtoRfcomm = 3;
    private const int SolSocket = 0xFFFF;
    private const int SoReceiveTimeout = 0x1006;
    private const int WsaWouldBlock = 10035;
    private const int WsaTimedOut = 10060;
    private const int FionBio = unchecked((int)0x8004667E);
    private const int SoError = 0x1007;            // SO_ERROR
    private const int NsBth = 16;                  // NS_BTH：蓝牙命名空间
    private const uint LupFlushCache = 0x1000;     // 强制重新向远端设备查询 SDP
    private const uint LupReturnAll = 0x0FF0;      // 返回名称/类型/地址/BLOB 等全部字段（含 CSADDR_INFO）
    private const int ScanTimeoutMicros = 150_000; // 通道扫描时单次连接预算（150ms）
    private static readonly IntPtr InvalidSocket = new(-1);
    private static readonly object WsaGate = new();
    private static int _wsaStarted;

    private readonly DeviceCandidate _candidate;
    private readonly Guid _serviceId;
    private readonly bool _allowBareChannels;
    private readonly int _preferredChannel;
    private readonly object _socketGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IntPtr _socket;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private int _connected;
    private int _disposed;

    public WindowsRfcommConnection(DeviceCandidate candidate, Guid serviceId, bool allowBareChannels = true, int preferredChannel = 0)
    {
        _candidate = candidate;
        _serviceId = serviceId;
        _allowBareChannels = allowBareChannels;
        _preferredChannel = preferredChannel;
    }

    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    public event EventHandler? Disconnected;

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
                throw new ObjectDisposedException(nameof(WindowsRfcommConnection));
            }
            _socket = socket;
            Volatile.Write(ref _connected, 1);
            _readCancellation = new CancellationTokenSource();
            _readTask = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsConnected)
            throw new InvalidOperationException("RFCOMM is not connected.");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var bytes = data.ToArray();
            var offset = 0;
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

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var wasConnected = Interlocked.Exchange(ref _connected, 0) != 0;
        _readCancellation?.Cancel();
        CloseSocket();
        if (_readTask is not null)
        {
            try { await _readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch (OperationCanceledException) { }
        }
        _readCancellation?.Dispose();
        _readCancellation = null;
        _readTask = null;
        if (wasConnected)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync(CancellationToken.None);
        _writeGate.Dispose();
    }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (Interlocked.Exchange(ref _connected, 0) != 0 && !cancellationToken.IsCancellationRequested)
                Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    // 连接策略严格限定为：SDP 解析的真实通道、品牌服务 UUID 自动解析通道(端口0)、裸通道 1、裸通道 15。
    // 品牌服务 UUID 的尝试顺序由 ControlManager 按蓝牙名称及验证结果决定。
    private IntPtr ConnectCore(ulong address, CancellationToken cancellationToken)
    {
        EnsureWsaStarted();

        // 诊断：把 Windows 为该地址缓存的经典 SDP 服务列出来，确认设备到底有没有暴露 RFCOMM/SPP。
        DumpCachedBluetoothServices(address);

        // 先用 SDP 查询品牌 GAIA 服务在远端设备上的真实 RFCOMM 通道。Windows 蓝牙缓存缺失
        // （设备未配对 / SDP 未缓存）时，端口0(按服务UUID解析)会失败，仅猜测裸通道 1/15 极易落到
        // 非 GAIA 的 RFCOMM 服务上——能建链但不回任何 GAIA 帧，表现即“全命令超时”。解析到的真实
        // 通道优先于硬编码猜测，可根治该问题；查询失败/超时则退回既有兜底，不影响现有可用路径。
        var resolvedChannel = QueryRfcommChannel(address, _serviceId);

        var attempts = new List<(string Label, Guid ServiceId, uint Port, int TimeoutMicros)>();
        // 品牌首选通道优先：部分型号（如华为 6i/Pro/Pro2/Pro3/Pro5/SE2/SE4/Studio/FreeClip2/LacePro2）
        // 的控制服务固定在 RFCOMM channel 1，SDP 缓存缺失时端口 0 解析会失败，直连该通道可自愈。
        // 若该通道非目标服务，后续 SDP/枚举/裸通道仍会继续尝试，不会阻塞。
        if (_preferredChannel > 0)
        {
            attempts.Add(("Channel-pref", Guid.Empty, (uint)_preferredChannel, 500_000));
            ApplicationLog.Current?.Info("Bluetooth",
                $"RFCOMM 首选通道：address={address:X12}，channel={_preferredChannel}。");
        }
        if (resolvedChannel.HasValue)
        {
            attempts.Add(("Service-UUID(resolved)", Guid.Empty, resolvedChannel.Value, 500_000));
            ApplicationLog.Current?.Info("Bluetooth",
                $"RFCOMM SDP 解析：address={address:X12}，serviceId={_serviceId}，解析到通道={resolvedChannel.Value}。");
        }
        attempts.Add(("Service-UUID", _serviceId, 0u, 500_000));

        // P1：枚举设备广播的全部 SDP RFCOMM 通道（不只品牌 UUID）。部分型号（如 FreeBuds Pro 4/5）
        // 不暴露标准 SPP UUID，仅在私有/未知服务下挂 RFCOMM 通道，必须遍历全部 SDP 记录才能连上。
        // 对应参考实现的「secure/insecure SPP → 设备 SDP 全部 UUID → 隐藏通道」回退链的前两段。
        var discovered = EnumerateRfcommChannels(address, null);
        foreach (var channel in discovered)
        {
            if (attempts.Any(a => a.ServiceId == Guid.Empty && a.Port == channel))
                continue;
            attempts.Add(($"SDP-chan-{channel}", Guid.Empty, channel, 500_000));
        }
        if (discovered.Count > 0)
            ApplicationLog.Current?.Info("Bluetooth",
                $"RFCOMM SDP 全盘枚举：address={address:X12}，发现通道={string.Join("/", discovered)}。");

        if (_allowBareChannels)
        {
            // 裸通道 1/15 仅作兜底：能建链但不回 GAIA 帧时即是死通道，由上层握手判定后禁用并重试。
            attempts.Add(("Channel-1", Guid.Empty, 1u, 500_000));
            attempts.Add(("Channel-15", Guid.Empty, 15u, 500_000));
        }

        ApplicationLog.Current?.Debug("Bluetooth",
            $"RFCOMM 连接策略：address={address:X12}，目标 serviceId={_serviceId}，端口={(resolvedChannel.HasValue ? resolvedChannel.Value + "/" : "")}0{(_allowBareChannels ? "/1/15" : "")}，SDP 枚举={discovered.Count}。");

        var lastWsa = 0;
        foreach (var (label, serviceId, port, timeout) in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var socket = TryConnect(address, serviceId, port, timeout, out var wsaError);
            if (socket != IntPtr.Zero)
            {
                var uuidText = serviceId == Guid.Empty ? "空(裸通道)" : serviceId.ToString();
                ApplicationLog.Current?.Info("Bluetooth",
                    $"RFCOMM 连接成功：[{label}]，serviceId={uuidText}，port={port}。");
                return socket;
            }
            lastWsa = wsaError;
            ApplicationLog.Current?.Debug("Bluetooth",
                $"RFCOMM 连接尝试失败：[{label}] serviceId={(serviceId == Guid.Empty ? "空" : serviceId.ToString())} port={port} WSA={wsaError}。");
        }

        var bareSuffix = _allowBareChannels ? "及裸通道 1/15" : "（已禁用裸通道兜底，仅尝试端口 0）";
        throw new BluetoothConnectException(
            $"RFCOMM 服务不可用（地址 {address:X12}，目标服务 {_serviceId}）。已尝试服务 UUID 的端口 0{bareSuffix}，" +
            $"最后一次失败 WSA 错误={lastWsa}（{BluetoothConnectException.DescribeWsa(lastWsa)}）。请确认耳机已与 Windows 配对并处于连接状态，且蓝牙串口未被其它程序占用。",
            wsaError: lastWsa);
    }

    // 诊断辅助：读取 Windows 为该蓝牙地址缓存的经典 SDP 服务（配对设备会被写入
    // HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{地址}）。
    // 若此处能看到 00001101（串口 SPP）或 edf00000 等子键，说明设备确实暴露了经典 RFCOMM；
    // 若整条记录不存在或为空，则说明该设备没有经典 SPP 端点，RFCOMM 连接注定失败。
    private static void DumpCachedBluetoothServices(ulong address)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Services\{address:X12}");
            if (key is null)
            {
                ApplicationLog.Current?.Info("Bluetooth",
                    $"RFCOMM 诊断：地址 {address:X12} 在 BTHPORT 服务缓存中无记录（设备未配对，或经典 SDP 未缓存）。");
                return;
            }
            var names = key.GetSubKeyNames();
            var friendly = names.Select(static n =>
            {
                var ok = Guid.TryParse(n, out var g);
                var shortName = ok ? g.ToString("D").Substring(0, 8) : n;
                var known = shortName.ToUpperInvariant() switch
                {
                    "00001101" => "串口SPP",
                    "0000079A" => "Melody(OPPO)",
                    "EDF00000" => "Edifier自定义",
                    _ => string.Empty
                };
                return ok ? $"{shortName}{(known.Length > 0 ? $"({known})" : "")}" : n;
            });
            ApplicationLog.Current?.Info("Bluetooth",
                $"RFCOMM 诊断：地址 {address:X12} 缓存的经典服务数={names.Length}：{string.Join("，", friendly)}");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Bluetooth", $"RFCOMM 诊断：读取服务缓存失败：{exception.Message}");
        }
    }

    // 用原生 Winsock WSALookupService（NS_BTH 命名空间）查询远端设备 SDP 记录，
    // 从返回的 CSADDR_INFO 中读取真实 RFCOMM 通道。用强类型结构体封送（非手写偏移），
    // 避免 WSAQUERYSETW / CSADDR_INFO 偏移量或步长算错导致的堆损坏与通道解析失败。
    // 任何异常或查询失败都返回空列表，交由上层通道扫描兜底。
    // serviceId 为 null 时按 SDP 公共浏览组根（00001002）搜索，遍历设备广播的全部
    // SDP 服务（P1 的多通道回退）。NULL service class 的查询在 Windows 上会退化，
    // Begin 成功也枚举不出任何结果，故必须换用 browse root 做 service search。
    private static uint? QueryRfcommChannel(ulong address, Guid serviceId)
        => EnumerateRfcommChannels(address, serviceId).Cast<uint?>().FirstOrDefault();

    private static IReadOnlyList<uint> EnumerateRfcommChannels(ulong address, Guid? serviceId)
    {
        EnsureWsaStarted();
        // 全盘枚举时用公共浏览组根替代 NULL service class：所有服务默认注册在公共浏览组下，
        // 按 browse root 搜索等价于遍历该设备的全部 SDP 服务。
        var effectiveServiceId = serviceId ?? PublicBrowseGroupRootId;
        IntPtr guidPtr = IntPtr.Zero;
        IntPtr ctxPtr = IntPtr.Zero;
        IntPtr qsPtr = IntPtr.Zero;
        IntPtr buf = IntPtr.Zero;
        IntPtr hLookup = IntPtr.Zero;
        var channels = new List<uint>();
        try
        {
            qsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WsaQuerySet>());
            var qs = new WsaQuerySet
            {
                DwSize = Marshal.SizeOf<WsaQuerySet>(),
                DwNameSpace = NsBth,
                LpServiceClassId = guidPtr = Marshal.AllocHGlobal(16),
            };
            Marshal.Copy(effectiveServiceId.ToByteArray(), 0, guidPtr, 16);

            // 将目标地址格式化为 "XX:XX:XX:XX:XX:XX"，限定 SDP 查询到该设备。
            var addressText = string.Create(17, address, (span, addr) =>
            {
                var text = addr.ToString("X12");
                for (var i = 0; i < 6; i++)
                {
                    span[i * 3] = text[i * 2];
                    span[i * 3 + 1] = text[i * 2 + 1];
                    if (i < 5)
                        span[i * 3 + 2] = ':';
                }
            });
            qs.LpszContext = ctxPtr = Marshal.StringToHGlobalUni(addressText);
            Marshal.StructureToPtr(qs, qsPtr, false);

            // 返回内容标志必须在 Begin 就传入：多数 Windows 版本上结果集在 Begin 阶段已确定，
            // 只在 Next 传 LUP_RETURN_ALL 会得到空结果（此前“SDP 枚举=0”的直接原因之一）。
            const uint lookupFlags = LupFlushCache | LupReturnAll;
            if (WSALookupServiceBegin(qsPtr, lookupFlags, out hLookup) != 0)
            {
                var beginError = WSAGetLastError();
                ApplicationLog.Current?.Debug("Bluetooth",
                    $"SDP 查询启动失败：address={address:X12}，serviceId={effectiveServiceId}，WSA={beginError}（{BluetoothConnectException.DescribeWsa(beginError)}）。");
                return channels; // SDP 查询失败时返回空列表，由上层通道扫描兜底（不能返回 null，Cast 会抛 ArgumentNullException）
            }

            var bufSize = 4096;
            buf = Marshal.AllocHGlobal(bufSize);
            var csSize = Marshal.SizeOf<CsAddrInfo>();
            while (true)
            {
                var size = bufSize;
                if (WSALookupServiceNext(hLookup, lookupFlags, ref size, buf) != 0)
                {
                    var err = WSAGetLastError();
                    if (err is 10108 or 10110) // WSA_E_NO_MORE / WSAENOMORE：没有更多结果
                        break;
                    if (err == 10040) // WSAEFAULT：缓冲区不足，扩容后重试
                    {
                        bufSize = Math.Max(size, bufSize * 2);
                        Marshal.FreeHGlobal(buf);
                        buf = Marshal.AllocHGlobal(bufSize);
                        continue;
                    }
                    ApplicationLog.Current?.Debug("Bluetooth",
                        $"SDP 查询中断：address={address:X12}，serviceId={effectiveServiceId}，WSA={err}。");
                    break;
                }

                var result = Marshal.PtrToStructure<WsaQuerySet>(buf);
                var csaBase = result.LpcsaBuffer;
                for (var i = 0; i < result.DwNumberOfCsAddrs && csaBase != IntPtr.Zero; i++)
                {
                    var cs = Marshal.PtrToStructure<CsAddrInfo>(csaBase + i * csSize);
                    var sockaddrPtr = cs.RemoteAddr.LpSockaddr;
                    if (sockaddrPtr == IntPtr.Zero)
                        continue;
                    var sa = Marshal.PtrToStructure<SockAddrBth>(sockaddrPtr);
                    if (sa.Port is > 0 and <= 30)
                        channels.Add((uint)sa.Port);
                }
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Bluetooth", $"SDP 通道查询异常（serviceId={serviceId}）：{exception.Message}");
        }
        finally
        {
            if (hLookup != IntPtr.Zero)
            {
                try { WSALookupServiceEnd(hLookup); } catch { }
            }
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (ctxPtr != IntPtr.Zero) Marshal.FreeHGlobal(ctxPtr);
            if (guidPtr != IntPtr.Zero) Marshal.FreeHGlobal(guidPtr);
            if (qsPtr != IntPtr.Zero) Marshal.FreeHGlobal(qsPtr);
        }
        return channels;
    }

    private static IntPtr TryConnect(ulong address, Guid serviceId, uint port, int timeoutMicros, out int wsaError)
    {
        wsaError = 0;
        var nativeSocket = socket(AfBth, SockStream, BthProtoRfcomm);
        if (nativeSocket == IntPtr.Zero || nativeSocket == InvalidSocket)
        {
            wsaError = WSAGetLastError();
            return IntPtr.Zero;
        }
        var endpoint = new SockAddrBth { Family = AfBth, Address = address, ServiceClassId = serviceId, Port = port };
        var endpointSize = Marshal.SizeOf<SockAddrBth>();
        var endpointPointer = Marshal.AllocHGlobal(endpointSize);
        try
        {
            Marshal.StructureToPtr(endpoint, endpointPointer, false);
            uint nonBlocking = 1;
            ioctlsocket(nativeSocket, FionBio, ref nonBlocking);
            if (connect(nativeSocket, endpointPointer, endpointSize) != 0 && WSAGetLastError() != WsaWouldBlock)
            {
                wsaError = WSAGetLastError();
                return CloseFailedSocket(nativeSocket);
            }
            var write = new FdSet { Count = 1, Array = new IntPtr[64] };
            write.Array[0] = nativeSocket;
            var errors = new FdSet { Count = 1, Array = new IntPtr[64] };
            errors.Array[0] = nativeSocket;
            var timeout = new TimeVal
            {
                Seconds = timeoutMicros / 1_000_000,
                Microseconds = timeoutMicros % 1_000_000,
            };
            var selectResult = select(0, IntPtr.Zero, ref write, ref errors, ref timeout);
            if (selectResult <= 0)
            {
                // select 返回 0 = 等待超时（并未置错误码，WSAGetLastError 会返回 0）；-1 才是真错误。
                // 把超时显式映射成 WSAETIMEDOUT(10060)，否则上层 BluetoothConnectException 会报
                // "WSA=0（未知）"，误导为程序错误，也吃不到 DescribeWsa 的"连接超时"友好文案。
                wsaError = selectResult == 0 ? WsaTimedOut : WSAGetLastError();
                return CloseFailedSocket(nativeSocket);
            }
            var error = 0;
            var errorSize = sizeof(int);
            if (getsockopt(nativeSocket, SolSocket, SoError, ref error, ref errorSize) != 0 || error != 0)
            {
                wsaError = error != 0 ? error : WSAGetLastError();
                return CloseFailedSocket(nativeSocket);
            }
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
        lock (_socketGate) { socket = _socket; _socket = IntPtr.Zero; }
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

    // 原生 SOCKADDR_BTH（x64 自然对齐，40 字节；port 偏移 32）。
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SockAddrBth
    {
        public ushort Family;
        public ulong Address;
        public Guid ServiceClassId;
        public uint Port;
    }

    // 原生 fd_set：fd_count + 最多 64 个 SOCKET 的数组（共 516 字节）。
    // 旧版只留了一个指针（12 字节），select() 会越界写入后续堆内存，导致 0xc0000374 堆损坏。
    [StructLayout(LayoutKind.Sequential)]
    private struct FdSet
    {
        public uint Count;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public IntPtr[] Array;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeVal { public int Seconds; public int Microseconds; }

    // 原生 WSAQUERYSETW（x64 自然对齐，112 字节），仅含指针/整型，可直接按值封送。
    [StructLayout(LayoutKind.Sequential)]
    private struct WsaQuerySet
    {
        public int DwSize;
        public IntPtr LpszServiceInstanceName;
        public IntPtr LpServiceClassId;
        public IntPtr LpszComment;
        public int DwNameSpace;
        public IntPtr LpNsProviderId;
        public IntPtr LpszContext;
        public int DwNumberOfProtocols;
        public IntPtr LpafpProtocols;
        public IntPtr LpszQueryString;
        public int DwNumberOfCsAddrs;
        public IntPtr LpcsaBuffer;
        public int DwOutputFlags;
        public IntPtr LpBlob;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SocketAddress
    {
        public IntPtr LpSockaddr;
        public int ISockaddrLength;
    }

    // 原生 CSADDR_INFO：两个 SOCKET_ADDRESS（各 16 字节）+ 两个整型 = 40 字节。
    [StructLayout(LayoutKind.Sequential)]
    private struct CsAddrInfo
    {
        public SocketAddress LocalAddr;
        public SocketAddress RemoteAddr;
        public int ISocketType;
        public int IProtocol;
    }

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
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int WSALookupServiceBegin(IntPtr querySet, uint flags, out IntPtr handle);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int WSALookupServiceNext(IntPtr handle, uint flags, ref int bufferLength, IntPtr results);
    [DllImport("ws2_32.dll", SetLastError = true)] private static extern int WSALookupServiceEnd(IntPtr handle);
}
