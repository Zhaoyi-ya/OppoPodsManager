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
    public FrameRouter Router => _router;

    public async Task SendAsync(ushort command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Debug("Protocol", $"发送数据帧：command=0x{command:X4}，bytes={payload.Length}，payload={FormatPayload(payload.Span)}。");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected)
            throw new InvalidOperationException("The raw connection is not connected.");
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
            using var subscription = _router.Subscribe(responseCommand, frame => completion.TrySetResult(frame));
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
