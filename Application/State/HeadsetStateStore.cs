using OppoPodsManager.Core.Brands;

namespace OppoPodsManager.Application.State;

public sealed class HeadsetStateStore
{
    private readonly object _gate = new();
    private IBrandSession? _session;

    public IBrandSession? CurrentSession
    {
        get { lock (_gate) return _session; }
    }

    public HeadsetStateSnapshot Snapshot =>
        HeadsetStateSnapshot.From(CurrentSession);

    public void SetSession(IBrandSession? session)
    {
        lock (_gate)
            _session = session;
    }
}
