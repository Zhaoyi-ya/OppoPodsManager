using OppoPodsManager.Core.Brands;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Application.Connection;

public sealed class ConnectionOrchestrator
{
    private readonly IDeviceDiscovery _discovery;
    private readonly IPlatformConnectionFactory _connections;
    private readonly IReadOnlyList<IBrandConnector> _brands;

    public ConnectionOrchestrator(
        IDeviceDiscovery discovery,
        IPlatformConnectionFactory connections,
        IReadOnlyList<IBrandConnector> brands)
    {
        _discovery = discovery;
        _connections = connections;
        _brands = brands;
    }

    public async ValueTask<BrandConnectionResult?> ConnectAsync(
        CancellationToken cancellationToken)
    {
        var devices = await _discovery.DiscoverAsync(cancellationToken);
        foreach (var device in devices)
        {
            var result = await ConnectDeviceAsync(device, cancellationToken);
            if (result is not null)
                return result;
        }

        return null;
    }

    public async ValueTask<BrandConnectionResult?> ConnectDeviceAsync(
        RawDeviceCandidate device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        foreach (var profile in _connections.GetProfiles(device).OrderBy(p => p.Priority))
        {
            var connection = await _connections.OpenAsync(
                device,
                profile,
                cancellationToken);
            var handedOff = false;
            try
            {
                var opened = await connection.ConnectAsync(cancellationToken);
                if (!opened.Succeeded)
                    continue;

                foreach (var brand in _brands)
                {
                    var handshake = await brand.TryHandshakeAsync(
                        connection,
                        new BrandHandshakeContext(
                            device,
                            TimeSpan.FromSeconds(3),
                            TimeSpan.FromSeconds(10)),
                        cancellationToken);

                    if (!handshake.IsMatched)
                        continue;

                    var session = await brand.CreateSessionAsync(
                        connection,
                        handshake,
                        cancellationToken);

                    handedOff = true;
                    return new BrandConnectionResult(session, handshake);
                }
            }
            finally
            {
                if (!handedOff)
                {
                    await connection.DisconnectAsync();
                    await connection.DisposeAsync();
                }
            }
        }

        return null;
    }

    public async ValueTask<bool> ProbeDeviceAsync(
        RawDeviceCandidate device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var result = await ConnectDeviceAsync(device, cancellationToken);
        if (result is null)
            return false;

        await result.Session.DisposeAsync();
        return true;
    }
}
