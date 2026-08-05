namespace OppoPodsManager.Communication.Abstractions;

public interface IRawConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    event EventHandler? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}
