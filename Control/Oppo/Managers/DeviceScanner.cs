using OppoPodsManager.Communication;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Oppo.Commands;

namespace OppoPodsManager.Control.Oppo.Managers;

public sealed class DeviceScanner
{
    private readonly CommunicationController _communication;

    public DeviceScanner(CommunicationController communication)
    {
        _communication = communication;
    }

    public async Task<IReadOnlyList<DeviceConnectionPlan>> ScanAsync(CancellationToken cancellationToken)
    {
        var candidates = await _communication.DiscoverAsync(cancellationToken);
        return candidates
            .Select(CreatePlan)
            .Where(plan => plan is not null)
            .Cast<DeviceConnectionPlan>()
            .ToArray();
    }

    public async Task<ConnectionLink> OpenAsync(DeviceConnectionPlan plan, CancellationToken cancellationToken)
    {
        var connection = await _communication.OpenAsync(plan.Candidate, plan.Options, cancellationToken);
        return new ConnectionLink(connection, new FrameCodec(), new FrameRouter());
    }

    private static DeviceConnectionPlan? CreatePlan(DeviceCandidate candidate)
    {
        var transport = candidate.Transports.FirstOrDefault(
            value => string.Equals(value, "rfcomm", StringComparison.OrdinalIgnoreCase));
        if (transport is null)
            return null;

        var serviceId = candidate.ServiceIds.FirstOrDefault();
        return new DeviceConnectionPlan(
            candidate,
            new ConnectionOptions(transport, serviceId == Guid.Empty ? null : serviceId, 0));
    }
}

public sealed record DeviceConnectionPlan(DeviceCandidate Candidate, ConnectionOptions Options);
