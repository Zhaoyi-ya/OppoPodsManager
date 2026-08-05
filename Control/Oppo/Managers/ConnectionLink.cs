using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control.Oppo.Managers;

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
        ApplicationLog.Current?.Debug("Protocol", $"发送数据帧：command=0x{command:X4}，bytes={payload.Length}。");
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connection.IsConnected)
            throw new InvalidOperationException("The raw connection is not connected.");
        await _connection.SendAsync(_codec.Encode(command, payload.Span), cancellationToken);
    }

    public async Task<ProtocolFrame> RequestAsync(
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Debug("Protocol", $"开始请求：command=0x{command:X4}，response=0x{responseCommand:X4}，bytes={payload.Length}。");
        await _requestGate.WaitAsync(cancellationToken);
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
                    _router.Route(frame);
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
