using System.Collections.Concurrent;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Core.Transport;

// 在原始字节连接上提供帧编码、串行请求和响应路由能力。
public sealed class ConnectionLink : ICommandRequester, IAsyncDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(4);
    private readonly IRawConnection _connection;
    private readonly IFrameCodec _codec;
    private readonly FrameRouter _router;
    private readonly SemaphoreSlim _receiveGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly CancellationTokenSource _connectionCancellation = new();
    // 在途请求所等待的响应命令字计数。用于区分“入站帧是请求的应答”还是“设备主动推送”。
    private readonly ConcurrentDictionary<ushort, int> _pendingResponses = new();
    private bool _disposed;

    public ConnectionLink(IRawConnection connection, IFrameCodec codec, FrameRouter router)
    {
        _connection = connection;
        _codec = codec;
        _router = router;
        _connection.DataReceived += OnDataReceived;
        _connection.Disconnected += OnConnectionDisconnected;
    }

    public event EventHandler? Disconnected;
    // 原始字节透传：供品牌层捕获非二进制协议帧（如华为 HFP 风格的 AT 文本行
    // +HUAWEIBATTERY=），这些不会通过 IFrameCodec 解码，也无法经 FrameRouter 路由。
    public event EventHandler<ReadOnlyMemory<byte>>? RawDataReceived;
    public FrameRouter Router => _router;

    // 会话活性打点（TickCount64，毫秒）：供 ControlManager 的会话看门狗判定
    // “发送后持续无响应”的死会话。空闲但健康的会话（无发送无接收）不会被误判。
    public long LastSendTicks => Volatile.Read(ref _lastSendField);
    public long LastReceiveTicks => Volatile.Read(ref _lastReceiveField);
    // 请求应答打点：仅在 RequestAsync 收到“与本请求命令字匹配的响应帧”时更新，用于握手验证。
    // 与 LastReceiveTicks（收到任何字节即打点）不同——其它品牌设备的裸通道可能发非协议噪声字节，
    // 令 LastReceiveTicks != 0 却从未应答本协议请求；LastResponseTicks == 0 才是可靠的“死通道”判据。
    public long LastResponseTicks => Volatile.Read(ref _lastResponseField);

    private void MarkSend() => Volatile.Write(ref _lastSendField, Environment.TickCount64);
    private void MarkReceive() => Volatile.Write(ref _lastReceiveField, Environment.TickCount64);
    private void MarkResponse() => Volatile.Write(ref _lastResponseField, Environment.TickCount64);
    private long _lastSendField;
    private long _lastReceiveField;
    private long _lastResponseField;

    public async Task SendAsync(ushort command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Debug("Protocol", $"发送数据帧：command=0x{command:X4}，bytes={payload.Length}，payload={FormatPayload(payload.Span)}。");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected)
            throw new InvalidOperationException("The raw connection is not connected.");
        MarkSend();
        await _connection.SendAsync(_codec.Encode(command, payload.Span), cancellationToken);
    }

    // 设备主动请求的单向应答（如 vivo 0x8509 时间请求 → 0x0509 应答）。
    // 复用请求串行门，确保它与 RequestAsync 的发送不会在字节层面交错，避免损坏 RFCOMM 流。
    public async Task SendFireAndForgetAsync(ushort command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected)
            throw new InvalidOperationException("The raw connection is not connected.");
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            MarkSend();
            await _connection.SendAsync(_codec.Encode(command, payload.Span), cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<ProtocolFrame> RequestAsync(
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Debug("Protocol", $"开始请求：command=0x{command:X4}，response=0x{responseCommand:X4}，bytes={payload.Length}。");
        await _requestGate.WaitAsync(cancellationToken);
        MarkPending(responseCommand, 1);
        try
        {
            var completion = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = _router.Subscribe(responseCommand, frame =>
            {
                MarkResponse();
                completion.TrySetResult(frame);
            });
            await SendAsync(command, payload, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionCancellation.Token);
            timeout.CancelAfter(ResponseTimeout);
            try
            {
                var frame = await completion.Task.WaitAsync(timeout.Token);
                ApplicationLog.Current?.Debug("Protocol", $"收到响应：command=0x{command:X4}，response=0x{responseCommand:X4}，bytes={frame.Payload.Length}。");
                return frame;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_connectionCancellation.IsCancellationRequested)
            {
                ApplicationLog.Current?.Error("Protocol", $"响应超时：command=0x{command:X4}，response=0x{responseCommand:X4}。");
                throw new TimeoutException($"The device did not respond to command 0x{command:X4}.");
            }
        }
        finally
        {
            MarkPending(responseCommand, -1);
            _requestGate.Release();
        }
    }

    // 绕过帧编解码，直接发送原始字节（如 AT 文本命令）。供品牌层在不破坏二进制协议流的前提下
    // 发送无法用 codec 表达的探测/查询。调用方需自行保证不与二进制请求在字节层面交错。
    public async Task SendRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected)
            throw new InvalidOperationException("The raw connection is not connected.");
        MarkSend();
        await _connection.SendAsync(data, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connection.DataReceived -= OnDataReceived;
        _connection.Disconnected -= OnConnectionDisconnected;
        _connectionCancellation.Cancel();
        try
        {
            await _connection.DisconnectAsync(CancellationToken.None);
        }
        finally
        {
            await _connection.DisposeAsync();
            _connectionCancellation.Dispose();
            _receiveGate.Dispose();
            _requestGate.Dispose();
        }
    }

    private async void OnDataReceived(object? sender, ReadOnlyMemory<byte> bytes)
    {
        // 任何字节到达都视为链路活性证据（早于解析：即使解析失败也说明设备在回话）。
        MarkReceive();
        // 先透传原始字节，供品牌层抓非二进制帧（AT 文本等）。
        RawDataReceived?.Invoke(this, bytes);
        try
        {
            await _receiveGate.WaitAsync();
            try
            {
                foreach (var frame in _codec.Decode(bytes.Span))
                {
                    // 没有在途请求等待该命令字 ⇒ 这一帧是设备自发的。单独记一行，便于验证
                    // “注册通知握手后耳机是否真的开始主动上报”，响应帧不重复记录（已有“收到响应”）。
                    if (!_pendingResponses.TryGetValue(frame.Command, out var waiting) || waiting <= 0)
                    {
                        ApplicationLog.Current?.Debug("Protocol",
                            $"设备主动推送帧：command=0x{frame.Command:X4}，bytes={frame.Payload.Length}，payload={FormatPayload(frame.Payload.Span)}。");
                    }

                    _router.Route(frame);
                }
            }
            finally
            {
                _receiveGate.Release();
            }
        }
        catch (ObjectDisposedException) { }
        catch
        {
            SignalDisconnected();
        }
    }

    private void MarkPending(ushort responseCommand, int delta)
        => _pendingResponses.AddOrUpdate(responseCommand, Math.Max(delta, 0), (_, count) => Math.Max(count + delta, 0));

    private static string FormatPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return "(空)";

        // 主动推送帧多为短状态包，超长时截断，避免固件/日志类大包刷屏。
        const int limit = 32;
        var shown = payload.Length <= limit ? payload : payload[..limit];
        var text = Convert.ToHexString(shown);
        return payload.Length <= limit ? text : $"{text}…(共{payload.Length}字节)";
    }

    private void OnConnectionDisconnected(object? sender, EventArgs args)
        => SignalDisconnected();

    private void SignalDisconnected()
    {
        if (_connectionCancellation.IsCancellationRequested)
            return;
        _connectionCancellation.Cancel();
        ApplicationLog.Current?.Info("Protocol", "底层连接已断开，正在取消等待中的协议请求。");
        Disconnected?.Invoke(this, EventArgs.Empty);
    }
}

public interface ICommandChannel
{
    Task SendAsync(ushort command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}

public interface ICommandRequester : ICommandChannel
{
    Task<ProtocolFrame> RequestAsync(ushort command, ushort responseCommand, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
}
