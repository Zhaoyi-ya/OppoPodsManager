namespace OppoPodsManager.Communication.Abstractions;

public interface IConnectionFactory
{
    string Transport { get; }

    Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken);
}
