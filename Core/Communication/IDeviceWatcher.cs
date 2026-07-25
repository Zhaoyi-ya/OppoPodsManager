using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Core.Communication;

public interface IDeviceWatcher : IAsyncDisposable
{
    event Action<IReadOnlyList<RawDeviceCandidate>>? DevicesChanged;

    ValueTask StartAsync(CancellationToken cancellationToken);

    ValueTask StopAsync();
}
