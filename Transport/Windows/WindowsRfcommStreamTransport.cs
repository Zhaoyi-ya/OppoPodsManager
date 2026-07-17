using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace OppoPodsManager;

/// <summary>
/// 经典蓝牙 RFCOMM 传输（WinRT StreamSocket）。
/// 发现走 RfcommServiceFinder（按服务 UUID / 配对名枚举），连接用
/// StreamSocket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName)。
/// 帧格式与 Winsock SPP 一致（SppFrameCodec，0xAA 外壳）。
/// 作为 WindowsConnectionTransport 的首选路径；失败后由总控尝试 Winsock 和目标 GATT。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsRfcommStreamTransport : IPodTransport
{
    public static readonly int ConnectTimeoutMs = 3000;   // StreamSocket.ConnectAsync 硬超时上限

    // 目标设备地址（48 位）。0 = 不限（沿用"品牌名优先/首个"旧行为）；非 0 = 精确连接该耳机。
    private readonly ulong _targetAddr;

    /// <summary>默认不限目标（连第一个匹配）。多耳机切换时用带地址的重载。</summary>
    public WindowsRfcommStreamTransport() : this(0) { }
    public WindowsRfcommStreamTransport(ulong targetAddr) { _targetAddr = targetAddr; }

    private readonly IFrameCodec _codec = new SppFrameCodec();
    private readonly List<byte> _framer = new();         // 帧累积缓冲
    private readonly ConcurrentQueue<PodFrame> _rxQueue = new();  // 已解码队列（Poll 交付）
    private readonly object _sendLock = new();             // 写保护
    private readonly object _lifecycleLock = new();

    private RfcommDeviceService? _service;   // RFCOMM 服务（发现结果）
    private StreamSocket? _socket;           // WinRT TCP 式 socket
    private DataWriter? _writer;             // OutputStream 写器
    private DataReader? _reader;             // InputStream 读器
    private CancellationTokenSource? _readCts;
    private CancellationTokenSource? _connectCts;
    private StreamSocket? _connectingSocket;
    private Task? _readLoop;
    private bool _disposed;

    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    /// <summary>发现 RFCOMM 服务 → 建立 StreamSocket 连接 → 写入器/读取器就绪 → 启动后台读循环。</summary>
    public bool Connect()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        CancellationTokenSource cts;
        lock (_lifecycleLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsRfcommStreamTransport));
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = cts = new CancellationTokenSource(ConnectTimeoutMs);
        }
        try
        {
            Log.D("RFSOCK", $"Connect: 开始 (超时预算={ConnectTimeoutMs}ms)");
            var connectTask = ConnectAsyncCore(cts.Token);
            if (!BoundedTaskWait.Wait(connectTask, ConnectTimeoutMs, CancelConnectingSocket, out var connected))
            {
                LastError = $"RFCOMM 连接超时 (耗时{sw.ElapsedMilliseconds}ms)";
                Log.Result("RFSOCK", "Connect", false, LastError);
                return false;
            }
            if (!connected)
            {
                Cleanup();
                if (string.IsNullOrEmpty(LastError))
                    LastError = $"RFCOMM 连接失败 (耗时{sw.ElapsedMilliseconds}ms, ConnectAsyncCore 返回 false)";
                Log.Result("RFSOCK", "Connect", false, LastError);
                return false;
            }

            lock (_lifecycleLock)
            {
                if (!ReferenceEquals(_connectCts, cts) || cts.IsCancellationRequested)
                {
                    LastError = "RFCOMM 连接已取消";
                    CleanupLocked();
                    return false;
                }
                IsConnected = true;
                StartReadLoopLocked();
            }
            LastError = null;
            Log.Result("RFSOCK", "Connect", true, $"name=\"{DeviceName}\" 耗时{sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = $"RFCOMM 连接已取消或超时 (耗时{sw.ElapsedMilliseconds}ms)";
            Cleanup();
            Log.Result("RFSOCK", "Connect", false, LastError);
            return false;
        }
        catch (ObjectDisposedException) when (cts.IsCancellationRequested)
        {
            LastError = $"RFCOMM 连接已取消或超时 (耗时{sw.ElapsedMilliseconds}ms)";
            Cleanup();
            Log.Result("RFSOCK", "Connect", false, LastError);
            return false;
        }
        catch (Exception e)
        {
            LastError = Log.DescribeException(e);
            Log.Ex("RFSOCK", $"Connect (耗时{sw.ElapsedMilliseconds}ms)", e);
            Cleanup();
            return false;
        }
    }

    /// <summary>异步连接核心：发现服务 → 打开 StreamSocket → 初始化 Writer/Reader。</summary>
    private async Task<bool> ConnectAsyncCore(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RfcommDeviceService? service = null;
        StreamSocket? socket = null;
        DataWriter? writer = null;
        DataReader? reader = null;
        try
        {
            service = await RfcommServiceFinder.FindServiceAsync(_targetAddr, cancellationToken);
            Log.D("RFSOCK", $"Connect: Uncached SDP 耗时{sw.ElapsedMilliseconds}ms target={_targetAddr:X12}");
            if (service?.Device == null || service.Device.BluetoothAddress != _targetAddr)
            {
                LastError = service == null
                    ? $"目标 {_targetAddr:X12} 未发现 OPPO SPP RFCOMM 服务"
                    : $"RFCOMM 服务地址不匹配：目标={_targetAddr:X12} 实际={service.Device.BluetoothAddress:X12}";
                return false;
            }

            var dev = service.Device;
            var deviceName = dev.Name ?? "OPPO 耳机";
            Log.D("RFSOCK", $"Connect: 命中目标服务 name=\"{deviceName}\" host={service.ConnectionHostName} svc={service.ConnectionServiceName}");

            socket = new StreamSocket();
            lock (_lifecycleLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _connectingSocket = socket;
            }
            await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName)
                .AsTask(cancellationToken);
            writer = new DataWriter(socket.OutputStream);
            reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

            cancellationToken.ThrowIfCancellationRequested();
            lock (_lifecycleLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _service = service;
                _socket = socket;
                _writer = writer;
                _reader = reader;
                if (ReferenceEquals(_connectingSocket, socket))
                    _connectingSocket = null;
                DeviceName = deviceName;
                service = null;
                socket = null;
                writer = null;
                reader = null;
                _framer.Clear();
                while (_rxQueue.TryDequeue(out _)) { }
            }
            Log.D("RFSOCK", $"Connect: StreamSocket 就绪 (总耗时{sw.ElapsedMilliseconds}ms)");
            return true;
        }
        finally
        {
            try { if (writer != null) { writer.DetachStream(); writer.Dispose(); } } catch { }
            try { if (reader != null) { reader.DetachStream(); reader.Dispose(); } } catch { }
            try { socket?.Dispose(); } catch { }
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_connectingSocket, socket))
                    _connectingSocket = null;
            }
            try { service?.Dispose(); } catch { }
        }
    }

    private void CancelConnectingSocket()
    {
        StreamSocket? connectingSocket;
        lock (_lifecycleLock)
        {
            try { _connectCts?.Cancel(); } catch { }
            connectingSocket = _connectingSocket;
            _connectingSocket = null;
        }
        try { connectingSocket?.Dispose(); } catch { }
    }

    /// <summary>启动后台读循环（Task.Run 在新线程运行 ReadLoopAsync）。</summary>
    private void StartReadLoopLocked()
    {
        _readCts = new CancellationTokenSource();
        var ct = _readCts.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(ct), ct);
    }

    /// <summary>后台读循环：持续从 InputStream 读字节，解帧后入队，由 Poll 交付上层。</summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var reader = _reader;
        if (reader == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                uint got = await reader.LoadAsync(512).AsTask(ct);
                if (got == 0) { Log.D("RFSOCK", "ReadLoop: 对端关闭 (load=0)"); break; }

                var chunk = new byte[got];
                reader.ReadBytes(chunk);
                lock (_framer)
                {
                    for (int i = 0; i < chunk.Length; i++)
                    {
                        _framer.Add(chunk[i]);
                        while (_codec.TryDecode(_framer, out var frame))
                            _rxQueue.Enqueue(frame);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常取消（CTS 触发）*/ }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.D("RFSOCK", $"ReadLoop 异常判定断开: {Log.DescribeException(ex)}");
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                Log.D("RFSOCK", "ReadLoop: 结束 -> 触发断开");
                OnDisconnected();
            }
        }
    }

    /// <summary>编码并发送一帧（DataWriter → OutputStream，写锁保护）。</summary>
    public void Send(ushort cmd, byte[] payload)
    {
        var writer = _writer;
        if (writer == null) { Log.D("RFSOCK", $"Send cmd=0x{cmd:X4} 失败: 未连接"); return; }

        byte[] bytes = _codec.Encode(cmd, payload);
        try
        {
            lock (_sendLock)
            {
                writer.WriteBytes(bytes);
                RunSync(async () => { await writer.StoreAsync(); return true; }, 3000);
            }
            Log.D("RFSOCK", $"Send cmd=0x{cmd:X4} payload={payload?.Length ?? 0}B -> {bytes.Length}B");
        }
        catch (Exception ex) { Log.Ex("RFSOCK", $"Send cmd=0x{cmd:X4}", ex); }
    }

    /// <summary>在 timeoutMs 预算内取出已入队的帧交付上层（后台读循环异步入队，这里同步取出）。</summary>
    public void Poll(int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            while (_rxQueue.TryDequeue(out var frame))
            {
                Log.D("RFSOCK", $"Poll: 交付帧 cmd=0x{frame.Cmd:X4} payload={frame.Payload.Length}B");
                FrameReceived?.Invoke(frame);
            }
            if (!IsConnected) return;
            if (DateTime.UtcNow >= end) break;
            Thread.Sleep(20);
        }
        // 收尾：交付循环末尾可能刚入队的帧
        while (_rxQueue.TryDequeue(out var frame))
        {
            Log.D("RFSOCK", $"Poll: 交付帧 cmd=0x{frame.Cmd:X4} payload={frame.Payload.Length}B");
            FrameReceived?.Invoke(frame);
        }
    }

    /// <summary>关闭连接标记并触发断开事件（幂等，链路断开时由读循环调用）。</summary>
    private void OnDisconnected()
    {
        if (!IsConnected) return;
        IsConnected = false;
        Disconnected?.Invoke();
    }

    /// <summary>标记断开并释放所有 WinRT 资源。</summary>
    public void Close()
    {
        IsConnected = false;
        try { _connectCts?.Cancel(); } catch { }
        try { _connectingSocket?.Dispose(); } catch { }
        Cleanup();
    }

    /// <summary>逐一释放 Writer/Reader/Socket/Service 并置 null。</summary>
    private void Cleanup()
    {
        lock (_lifecycleLock)
            CleanupLocked();
    }

    private void CleanupLocked()
    {
        try { _connectCts?.Cancel(); } catch { }
        try { _readCts?.Cancel(); } catch { }
        try { if (_writer != null) { _writer.DetachStream(); _writer.Dispose(); } } catch { }
        try { if (_reader != null) { _reader.DetachStream(); _reader.Dispose(); } } catch { }
        try { _socket?.Dispose(); } catch { }
        try { _service?.Dispose(); } catch { }
        try { _connectCts?.Dispose(); } catch { }
        _writer = null;
        _connectingSocket = null;
        _reader = null;
        _socket = null;
        _service = null;
        _connectCts = null;
        _readCts = null;
        _readLoop = null;
    }

    /// <summary>RunSync 的结构化结果：区分成功 / 超时 / 抛异常，便于日志给出准确原因。</summary>
    private readonly struct SyncOutcome
    {
        public bool Ok { get; init; }
        public bool TimedOut { get; init; }
        public Exception? Error { get; init; }
    }

    /// <summary>
    /// 在给定超时内同步等待异步 Task&lt;bool>。
    /// 返回结构化结果：真正超时(TimedOut) 与 任务抛异常(Error) 是两种不同故障，
    /// 原实现把两者都压成 false，导致上层永远只报“连接超时”，掩盖了真实 HRESULT。
    /// </summary>
    private static SyncOutcome RunSync(Func<Task<bool>> op, int timeoutMs)
    {
        Task<bool>? task = null;
        try
        {
            task = op();
            if (!task.Wait(timeoutMs))
            {
                // 超时：任务仍在跑，挂一个续延吞掉后续异常，避免 UnobservedTaskException
                task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                return new SyncOutcome { Ok = false, TimedOut = true };
            }
            return new SyncOutcome { Ok = task.Result };
        }
        catch (Exception ex)
        {
            // task.Wait 抛出的通常是 AggregateException，Log 层会展开到真正 InnerException
            Log.Ex("RFSOCK", "RunSync", ex);
            return new SyncOutcome { Ok = false, Error = ex };
        }
    }

    /// <summary>释放全部 WinRT 资源（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
    }
}

/// <summary>目标锁定的 OPPO SPP 服务发现；仅由 WindowsRfcommStreamTransport 使用。</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class RfcommServiceFinder
{
    public static async Task<RfcommDeviceService?> FindServiceAsync(
        ulong targetAddr,
        CancellationToken cancellationToken)
    {
        var svc = await FindByServiceUuidAsync(targetAddr, cancellationToken);
        if (svc != null) { Log.D("BT", "RfcommFinder: 命中(服务 UUID 枚举)"); return svc; }

        svc = await FindByPairedDeviceAsync(targetAddr, cancellationToken);
        if (svc != null) Log.D("BT", "RfcommFinder: 命中(配对经典设备)");
        return svc;
    }

    private static async Task<RfcommDeviceService?> FindByServiceUuidAsync(
        ulong targetAddr,
        CancellationToken cancellationToken)
    {
        try
        {
            var serviceId = RfcommServiceId.FromUuid(OppoProtocol.OppoSppUuid);
            string selector = RfcommDeviceService.GetDeviceSelector(serviceId);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken);
            Log.D("BT", $"RfcommFinder: 服务 UUID 枚举到 {devices.Count} 个候选 target={targetAddr:X12}");

            foreach (var di in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var svc = await TryOpenAsync(di.Id, cancellationToken);
                if (svc?.Device != null && svc.Device.BluetoothAddress == targetAddr)
                {
                    Log.D("BT", $"RfcommFinder: 目标地址匹配 name=\"{di.Name}\"");
                    return await ResolveFreshAsync(svc, cancellationToken);
                }
                svc?.Dispose();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) { Log.Ex("BT", "RfcommFinder.FindByServiceUuidAsync", ex); }
        return null;
    }

    private static async Task<RfcommDeviceService?> FindByPairedDeviceAsync(
        ulong targetAddr,
        CancellationToken cancellationToken)
    {
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken);
            var serviceId = RfcommServiceId.FromUuid(OppoProtocol.OppoSppUuid);

            foreach (var di in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dev = await BluetoothDevice.FromIdAsync(di.Id).AsTask(cancellationToken);
                if (dev == null || dev.BluetoothAddress != targetAddr) continue;

                var result = await dev.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken);
                if (result.Services.Count > 0) return result.Services[0];
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) { Log.Ex("BT", "RfcommFinder.FindByPairedDeviceAsync", ex); }
        return null;
    }

    private static async Task<RfcommDeviceService?> ResolveFreshAsync(
        RfcommDeviceService cached,
        CancellationToken cancellationToken)
    {
        try
        {
            var dev = cached.Device;
            if (dev == null) return cached;

            var serviceId = RfcommServiceId.FromUuid(OppoProtocol.OppoSppUuid);
            var fresh = await dev.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);
            if (fresh.Services.Count > 0)
            {
                var svc = fresh.Services[0];
                Log.D("BT", $"RfcommFinder: Uncached SDP svc={svc.ConnectionServiceName}");
                cached.Dispose();
                return svc;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex) { Log.Ex("BT", "RfcommFinder.ResolveFreshAsync", ex); }
        return cached;
    }

    private static async Task<RfcommDeviceService?> TryOpenAsync(
        string deviceId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RfcommDeviceService.FromIdAsync(deviceId).AsTask(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Ex("BT", $"RfcommFinder.TryOpenAsync id={deviceId}", ex);
            return null;
        }
    }
}
