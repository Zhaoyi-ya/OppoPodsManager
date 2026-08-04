using OppoPodsManager.Control.Logging;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Notifications;

// 表示设备状态变化需要显示的桌面通知类型。
public enum DeviceNotificationKind
{
    Connected,
    Disconnected,
    LowBattery,
    CriticalBattery
}

// 携带通知类型和触发时的完整业务快照。
public sealed record DeviceNotificationRequest(
    BusinessSnapshot Snapshot,
    DeviceNotificationKind Kind);

// 根据业务快照边沿生成设备通知，不依赖任何 Avalonia 控件或窗口。
public sealed class NotificationCoordinator : IDisposable
{
    private readonly FrontendState _frontendState;
    private BusinessSnapshot _previous;
    private bool _disposed;

    public NotificationCoordinator(FrontendState frontendState)
    {
        _frontendState = frontendState;
        _previous = frontendState.Snapshot;
        _frontendState.Changed += OnStateChanged;
    }

    // 向桌面通知适配层发布已经分类的设备通知。
    public event EventHandler<DeviceNotificationRequest>? NotificationRaised;

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
    {
        if (_disposed)
            return;

        var previous = _previous;
        _previous = snapshot;

        if (previous.IsConnected != snapshot.IsConnected)
        {
            Raise(snapshot, snapshot.IsConnected
                ? DeviceNotificationKind.Connected
                : DeviceNotificationKind.Disconnected);
            return;
        }

        if (!snapshot.IsConnected)
            return;

        var previousLevel = LowestEarbudBattery(previous);
        var currentLevel = LowestEarbudBattery(snapshot);
        if (currentLevel is null
            || currentLevel > 20
            || previousLevel is not null && previousLevel <= 20)
            return;

        Raise(snapshot, currentLevel <= 10
            ? DeviceNotificationKind.CriticalBattery
            : DeviceNotificationKind.LowBattery);
    }

    // 只使用左右耳电量判断低电量，避免充电盒低电单独触发原项目没有的提示。
    private static byte? LowestEarbudBattery(BusinessSnapshot snapshot)
    {
        var levels = new[]
        {
            snapshot.LeftBattery?.Percent,
            snapshot.RightBattery?.Percent
        }
        .Where(value => value is not null)
        .Select(value => value!.Value)
        .ToArray();

        return levels.Length == 0 ? null : levels.Min();
    }

    // 发布已经完成边沿判断的通知请求，并记录通知链路日志。
    private void Raise(BusinessSnapshot snapshot, DeviceNotificationKind kind)
    {
        ApplicationLog.Current?.Info(
            "Notification",
            $"设备通知生成：kind={kind}，revision={snapshot.Revision}。");
        NotificationRaised?.Invoke(this, new DeviceNotificationRequest(snapshot, kind));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _frontendState.Changed -= OnStateChanged;
        NotificationRaised = null;
    }
}
