using System.Collections.Concurrent;
using OppoPodsManager.Core.Communication;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Adapts a platform raw byte connection into the command/frame transport shape
/// expected by the Oppo protocol stack (cmd + payload). Framing stays brand-side.
/// </summary>
public sealed class OppoFramedTransport : IDisposable
{
    private readonly IRawConnection _connection;
    private readonly List<byte> _framer = new();
    private readonly ConcurrentQueue<(ushort Cmd, byte[] Payload)> _rx = new();
    private readonly object _gate = new();
    private bool _disposed;

    public OppoFramedTransport(IRawConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.DataReceived += OnData;
        _connection.Disconnected += OnDisconnected;
    }

    public string? DeviceName => _connection.Device.AdvertisedName;
    public bool IsConnected => _connection.IsConnected;
    public string? LastError => _connection.LastError;
    public event Action<ushort, byte[]>? FrameReceived;
    public event Action? Disconnected;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return result.Succeeded;
    }

    public async Task SendAsync(ushort cmd, byte[] payload, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = OppoFrameCodec.Encode(cmd, payload ?? []);
        await _connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    public void Send(ushort cmd, byte[] payload) =>
        SendAsync(cmd, payload, CancellationToken.None).GetAwaiter().GetResult();

    public void Poll(int timeoutMs)
    {
        var end = DateTime.UtcNow.AddMilliseconds(Math.Max(0, timeoutMs));
        while (true)
        {
            while (_rx.TryDequeue(out var frame))
                FrameReceived?.Invoke(frame.Cmd, frame.Payload);
            if (!IsConnected || DateTime.UtcNow >= end)
                break;
            Thread.Sleep(20);
        }

        while (_rx.TryDequeue(out var frame))
            FrameReceived?.Invoke(frame.Cmd, frame.Payload);
    }

    public async Task DisconnectAsync()
    {
        await _connection.DisconnectAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connection.DataReceived -= OnData;
        _connection.Disconnected -= OnDisconnected;
        _ = _connection.DisposeAsync().AsTask();
    }

    private void OnData(ReadOnlyMemory<byte> data)
    {
        lock (_gate)
        {
            var bytes = data.Span;
            for (var i = 0; i < bytes.Length; i++)
                _framer.Add(bytes[i]);

            while (TryDecode(_framer, out var cmd, out var payload))
                _rx.Enqueue((cmd, payload));
        }
    }

    private void OnDisconnected() => Disconnected?.Invoke();

    private static bool TryDecode(List<byte> buffer, out ushort cmd, out byte[] payload)
    {
        cmd = 0;
        payload = [];
        var start = buffer.IndexOf(0xAA);
        if (start < 0)
        {
            buffer.Clear();
            return false;
        }

        if (start > 0)
            buffer.RemoveRange(0, start);
        if (buffer.Count < 2)
            return false;

        var frameLen = buffer[1] + 2;
        if (frameLen < 9 || frameLen > 512)
        {
            buffer.RemoveAt(0);
            return false;
        }

        if (buffer.Count < frameLen)
            return false;

        cmd = (ushort)(buffer[4] | (buffer[5] << 8));
        var payloadLen = buffer[7] | (buffer[8] << 8);
        if (payloadLen < 0 || payloadLen > frameLen - 9)
        {
            buffer.RemoveRange(0, frameLen);
            return false;
        }

        payload = buffer.GetRange(9, payloadLen).ToArray();
        buffer.RemoveRange(0, frameLen);
        return true;
    }
}
