using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Platforms.Common;

/// <summary>
/// Platform adapter seam. OS-specific implementations supply the delegates;
/// this class owns common lifecycle, event forwarding, and disposal semantics.
/// It deliberately carries raw bytes only and has no brand/protocol knowledge.
/// </summary>
public sealed class DelegateRawConnection : IRawConnection
{
    private readonly Func<CancellationToken, ValueTask<ConnectionResult>> _connect;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
    private readonly Func<ValueTask> _disconnect;
    private readonly Func<ValueTask> _dispose;
    private int _disposed;

    public DelegateRawConnection(
        RawDeviceCandidate device,
        ConnectionProfile profile,
        Func<CancellationToken, ValueTask<ConnectionResult>> connect,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        Func<ValueTask> disconnect,
        Func<ValueTask>? dispose = null)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _disconnect = disconnect ?? throw new ArgumentNullException(nameof(disconnect));
        _dispose = dispose ?? disconnect;
    }

    public RawDeviceCandidate Device { get; }

    public ConnectionProfile Profile { get; }

    public bool IsConnected { get; private set; }

    public string? LastError { get; private set; }

    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    public event Action? Disconnected;

    public async ValueTask<ConnectionResult> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var result = await _connect(cancellationToken);
        IsConnected = result.Succeeded;
        LastError = result.Error;
        return result;
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected)
            throw new InvalidOperationException("原始连接尚未建立。");

        await _send(data, cancellationToken);
    }

    public async ValueTask DisconnectAsync()
    {
        if (!IsConnected)
            return;

        await _disconnect();
        IsConnected = false;
        Disconnected?.Invoke();
    }

    public void PublishReceived(ReadOnlyMemory<byte> data)
    {
        if (IsConnected)
            DataReceived?.Invoke(data);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _dispose();
        }
        finally
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }
    }
}
