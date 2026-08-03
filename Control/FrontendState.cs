using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control;

// 管理窗口可见性租约和面向界面的最新业务快照。
public sealed class FrontendState
{
    private BusinessSnapshot _snapshot = new(
        0,
        null,
        false,
        null,
        null,
        null,
        null,
        WearSnapshot.Empty,
        NoiseSnapshot.Empty,
        EqualizerSnapshot.Empty,
        GameSnapshot.Empty,
        SpatialAudioSnapshot.Empty,
        FeatureStateSnapshot.Empty,
        MultiDeviceSnapshot.Empty,
        [],
        DateTimeOffset.MinValue);
    private int _interactiveSurfaceCount;

    public event EventHandler<BusinessSnapshot>? Changed;
    public event EventHandler<bool>? InteractivePollingChanged;

    public BusinessSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public bool HasInteractiveSurface => Volatile.Read(ref _interactiveSurfaceCount) > 0;

    public void Publish(BusinessSnapshot snapshot)
    {
        var previous = Volatile.Read(ref _snapshot);
        if (snapshot.Revision <= previous.Revision)
        {
            ApplicationLog.Current?.Debug("State", $"忽略过期状态：revision={snapshot.Revision}，current={previous.Revision}。");
            return;
        }

        Volatile.Write(ref _snapshot, snapshot);
        ApplicationLog.Current?.Debug("State", $"发布状态：revision={snapshot.Revision}，connected={snapshot.IsConnected}，device={snapshot.DeviceName ?? ""}，listeners={Changed?.GetInvocationList().Length ?? 0}。");
        Changed?.Invoke(this, snapshot);
    }

    public IDisposable AcquireInteractiveSurface()
    {
        var count = Interlocked.Increment(ref _interactiveSurfaceCount);
        ApplicationLog.Current?.Info("Polling", $"获取交互界面租约：count={count}。");
        if (count == 1)
            InteractivePollingChanged?.Invoke(this, true);

        return new InteractiveSurfaceLease(this);
    }

    private void ReleaseInteractiveSurface()
    {
        var count = Interlocked.Decrement(ref _interactiveSurfaceCount);
        ApplicationLog.Current?.Info("Polling", $"释放交互界面租约：count={count}。");
        if (count == 0)
            InteractivePollingChanged?.Invoke(this, false);
    }

    private sealed class InteractiveSurfaceLease(FrontendState state) : IDisposable
    {
        private FrontendState? _state = state;

        public void Dispose()
        {
            Interlocked.Exchange(ref _state, null)?.ReleaseInteractiveSurface();
        }
    }
}
