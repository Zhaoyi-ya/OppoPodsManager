using OppoPodsManager.Core.Brands;
using OppoPodsManager.Application.State;
using OppoPodsManager.Core.Devices;
using OppoPodsManager.Core.Connections;

namespace OppoPodsManager.Application.Connection;

/// <summary>
/// Owns the current brand session lifecycle. Application services and UI bind to this,
/// not to platform transports or brand protocol internals.
/// </summary>
public sealed class HeadsetSessionCoordinator : IAsyncDisposable
{
    private readonly ConnectionOrchestrator _orchestrator;
    private readonly HeadsetStateStore _store;
    private readonly object _gate = new();
    private int _generation;
    private IBrandSession? _subscribedSession;

    public HeadsetSessionCoordinator(
        ConnectionOrchestrator orchestrator,
        HeadsetStateStore store)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IBrandSession? CurrentSession => _store.CurrentSession;

    public event Action? SessionChanged;

    public HeadsetState? CurrentState => _store.CurrentSession?.State;

    public DeviceCapabilities? CurrentCapabilities => _store.CurrentSession?.Capabilities;

    public HeadsetStateSnapshot CurrentSnapshot => _store.Snapshot;

    public async ValueTask<BrandConnectionResult?> ConnectAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        var result = await _orchestrator.ConnectAsync(cancellationToken);
        if (generation != Volatile.Read(ref _generation))
        {
            if (result is not null)
                await result.Session.DisposeAsync();
            return null;
        }

        if (result is null)
            return null;

        await ReplaceSessionAsync(result.Session);
        await result.Session.InitializeAsync(cancellationToken);
        return result;
    }

    public async ValueTask<BrandConnectionResult?> ConnectDeviceAsync(
        RawDeviceCandidate device,
        CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _generation);
        var result = await _orchestrator.ConnectDeviceAsync(device, cancellationToken);
        if (generation != Volatile.Read(ref _generation))
        {
            if (result is not null)
                await result.Session.DisposeAsync();
            return null;
        }

        if (result is null)
            return null;

        await ReplaceSessionAsync(result.Session);
        await result.Session.InitializeAsync(cancellationToken);
        return result;
    }

    public ValueTask<bool> ProbeDeviceAsync(
        RawDeviceCandidate device,
        CancellationToken cancellationToken) =>
        _orchestrator.ProbeDeviceAsync(device, cancellationToken);

    public async ValueTask DisconnectAsync()
    {
        Interlocked.Increment(ref _generation);
        await ReplaceSessionAsync(null);
    }

    private async ValueTask ReplaceSessionAsync(IBrandSession? session)
    {
        IBrandSession? previous;
        lock (_gate)
        {
            previous = _store.CurrentSession;
            if (_subscribedSession is not null)
                _subscribedSession.StateChanged -= OnSessionStateChanged;
            _store.SetSession(session);
            _subscribedSession = session;
            if (_subscribedSession is not null)
                _subscribedSession.StateChanged += OnSessionStateChanged;
        }

        if (previous is not null)
            await previous.DisposeAsync();

        SessionChanged?.Invoke();
    }

    private void OnSessionStateChanged() => SessionChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
