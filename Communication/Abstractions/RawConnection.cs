namespace OppoPodsManager.Communication.Abstractions;

public interface IRawConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    // 底层传输意外中断时通知协议层取消等待中的请求。
    event EventHandler? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}
