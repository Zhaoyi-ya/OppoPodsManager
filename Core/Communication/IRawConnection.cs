using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Core.Communication;

public interface IRawConnection : IAsyncDisposable
{
    RawDeviceCandidate Device { get; }

    ConnectionProfile Profile { get; }

    bool IsConnected { get; }

    string? LastError { get; }

    event Action<ReadOnlyMemory<byte>>? DataReceived;

    event Action? Disconnected;

    ValueTask<ConnectionResult> ConnectAsync(CancellationToken cancellationToken);

    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask DisconnectAsync();
}
