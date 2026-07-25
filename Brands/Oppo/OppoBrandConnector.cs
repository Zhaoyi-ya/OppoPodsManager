using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Establishes the Oppo protocol session after the platform has opened a raw link.
/// Brand identity is confirmed by the read-only product-id handshake, not by name alone.
/// </summary>
public sealed class OppoBrandConnector : IBrandConnector
{
    private const ushort QueryProductId = 0x0103;
    private const ushort ProductIdResponse = 0x8103;
    private readonly OppoDeviceProfileCatalog _catalog;

    public OppoBrandConnector(OppoDeviceProfileCatalog? catalog = null)
    {
        _catalog = catalog ?? new OppoDeviceProfileCatalog();
    }

    public DeviceBrand Brand => DeviceBrand.Oppo;

    public async ValueTask<BrandHandshakeResult> TryHandshakeAsync(
        IRawConnection connection,
        BrandHandshakeContext context,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<BrandHandshakeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new List<byte>();

        void OnData(ReadOnlyMemory<byte> data)
        {
            buffer.AddRange(data.ToArray());
            while (TryReadFrame(buffer, out var command, out var payload))
            {
                if (command != ProductIdResponse || payload.Length != 4 || payload[0] != 0)
                    continue;

                var productId = ((int)payload[1] | payload[2] << 8 | payload[3] << 16).ToString("X6");
                var entry = _catalog.FindByProductId(productId);
                if (entry is null)
                {
                    completion.TrySetResult(new BrandHandshakeResult(
                        BrandHandshakeStatus.Inconclusive,
                        ProtocolVersion: "Melody",
                        Error: $"未找到 Oppo productId={productId} 的白名单型号"));
                    continue;
                }

                var loader = new OppoProfileLoader(_catalog);
                var staticCapabilities = loader.FromJson(
                    entry.Value,
                    connection.Device.AdvertisedName ?? productId);
                var identity = new DeviceIdentity(
                    connection.Device.StableId,
                    DeviceBrand.Oppo,
                    productId,
                    staticCapabilities.ModelName,
                    connection.Device.BluetoothAddress,
                    connection.Device.PlatformDeviceId);

                completion.TrySetResult(new BrandHandshakeResult(
                    BrandHandshakeStatus.Matched,
                    identity,
                    staticCapabilities,
                    "Melody"));
            }
        }

        connection.DataReceived += OnData;
        try
        {
            await connection.SendAsync(
                OppoFrameCodec.Encode(QueryProductId, []),
                cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(context.TotalTimeout);
            await using (timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token)))
            {
                try
                {
                    return await completion.Task;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return new BrandHandshakeResult(
                        BrandHandshakeStatus.NotMatched,
                        ProtocolVersion: "Melody",
                        Error: "Oppo productId 握手超时");
                }
            }
        }
        finally
        {
            connection.DataReceived -= OnData;
        }
    }

    public ValueTask<IBrandSession> CreateSessionAsync(
        IRawConnection connection,
        BrandHandshakeResult handshake,
        CancellationToken cancellationToken)
    {
        if (!handshake.IsMatched || handshake.Identity is null || handshake.Capabilities is null)
            throw new InvalidOperationException("只有握手成功后才能创建 Oppo 会话。");

        IBrandSession session = new OppoSession(
            connection,
            handshake.Identity,
            handshake.Capabilities);
        return ValueTask.FromResult(session);
    }

    private static bool TryReadFrame(
        List<byte> buffer,
        out ushort command,
        out byte[] payload)
    {
        command = 0;
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

        var frameLength = buffer[1] + 2;
        if (frameLength < 9 || frameLength > 4096)
        {
            buffer.RemoveAt(0);
            return false;
        }
        if (buffer.Count < frameLength)
            return false;

        command = (ushort)(buffer[4] | buffer[5] << 8);
        var payloadLength = buffer[7] | buffer[8] << 8;
        if (payloadLength > frameLength - 9)
        {
            buffer.RemoveRange(0, frameLength);
            return false;
        }

        payload = buffer.GetRange(9, payloadLength).ToArray();
        buffer.RemoveRange(0, frameLength);
        return true;
    }
}
