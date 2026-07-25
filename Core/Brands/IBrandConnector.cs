using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Core.Brands;

public interface IBrandConnector
{
    DeviceBrand Brand { get; }

    ValueTask<BrandHandshakeResult> TryHandshakeAsync(
        IRawConnection connection,
        BrandHandshakeContext context,
        CancellationToken cancellationToken);

    ValueTask<IBrandSession> CreateSessionAsync(
        IRawConnection connection,
        BrandHandshakeResult handshake,
        CancellationToken cancellationToken);
}
