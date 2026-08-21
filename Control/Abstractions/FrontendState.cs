
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;
namespace OppoPodsManager.Control.Abstractions;
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
        SoundEffectSceneSnapshot.Empty,
        FeatureStateSnapshot.Empty,
        MultiDeviceSnapshot.Empty,
        [],
        DateTimeOffset.MinValue);
    private long _publishedRevision;
    private int _interactiveSurfaceCount;
    public event EventHandler<BusinessSnapshot>? Changed;
    public event EventHandler<bool>? InteractivePollingChanged;
    public BusinessSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool HasInteractiveSurface => Volatile.Read(ref _interactiveSurfaceCount) > 0;
    public void Publish(BusinessSnapshot snapshot)
    {
        var revision = Interlocked.Increment(ref _publishedRevision);
        var normalized = snapshot with { Revision = revision };
        Volatile.Write(ref _snapshot, normalized);
        ApplicationLog.Current?.Debug("State", $"发布状态：revision={normalized.Revision}，connected={normalized.IsConnected}，device={normalized.DeviceName ?? ""}，listeners={Changed?.GetInvocationList().Length ?? 0}。");
        foreach (var handler in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((EventHandler<BusinessSnapshot>)handler)(this, normalized); }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Error("State", "状态监听器处理失败。", exception);
            }
        }
    }
    // 清空当前品牌状态，供切换到尚未支持的品牌或没有活动后端时使用。
    public void Clear()
    {
        var previous = Volatile.Read(ref _snapshot);
        var cleared = new BusinessSnapshot(
            previous.Revision + 1,
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
            SoundEffectSceneSnapshot.Empty,
            FeatureStateSnapshot.Empty,
            MultiDeviceSnapshot.Empty,
            [],
            DateTimeOffset.UtcNow);
        Publish(cleared);
    }
    public IDisposable AcquireInteractiveSurface()
    {
        var count = Interlocked.Increment(ref _interactiveSurfaceCount);
        ApplicationLog.Current?.Info("Polling", $"获取交互界面租约：count={count}。");
        if (count == 1)
            RaiseInteractivePollingChanged(true);
        return new InteractiveSurfaceLease(this);
    }
    private void ReleaseInteractiveSurface()
    {
        var count = Interlocked.Decrement(ref _interactiveSurfaceCount);
        ApplicationLog.Current?.Info("Polling", $"释放交互界面租约：count={count}。");
        if (count == 0)
            RaiseInteractivePollingChanged(false);
    }
    private void RaiseInteractivePollingChanged(bool enabled)
    {
        foreach (var handler in InteractivePollingChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((EventHandler<bool>)handler)(this, enabled); }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Error("Polling", "交互轮询状态监听器处理失败。", exception);
            }
        }
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
