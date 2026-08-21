using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析佩戴状态通知和查询响应。
public sealed class WearStatus : IDisposable
{
    private readonly BusinessState _state;
    private readonly Notifier _notifier;

    public WearStatus(BusinessState state, Notifier notifier)
    {
        _state = state;
        _notifier = notifier;
        _notifier.NotificationReceived += OnNotificationReceived;
    }

    public void Apply(ReadOnlySpan<byte> payload)
    {
        var data = payload;
        // 查询响应含 status；通知通常直接以数量字段开头。
        if (data.Length >= 3 && data[0] == 0)
            data = data[1..];

        var current = _state.Snapshot().Wear;
        var left = current.Left;
        var right = current.Right;
        if (data.Length >= 3 && data[0] is > 0 and <= 2 && data.Length >= 1 + data[0] * 2)
        {
            for (var index = 0; index < data[0]; index++)
                ApplyEntry(data[1 + index * 2], data[2 + index * 2], ref left, ref right);
        }
        else
        {
            for (var index = 0; index + 1 < data.Length; index += 2)
                ApplyEntry(data[index], data[index + 1], ref left, ref right);
        }

        _state.SetWear(new WearSnapshot(left, right));
    }

    public void Dispose() => _notifier.NotificationReceived -= OnNotificationReceived;

    private void OnNotificationReceived(object? sender, NotificationReceived notification)
    {
        if (notification.EventId == Notifier.WearEvent)
            Apply(notification.Data.Span);
    }

    private static EarWearState ParseState(byte value) => value switch
    {
        0 => EarWearState.Disconnected,
        1 or 5 => EarWearState.Removed,
        3 or 7 => EarWearState.Worn,
        4 => EarWearState.InCase,
        _ => EarWearState.Unknown
    };

    // 按耳机组件编号写入左右耳的最新佩戴状态。
    private static void ApplyEntry(byte component, byte rawState, ref EarWearState left, ref EarWearState right)
    {
        var state = ParseState(rawState);
        if (component == 1)
            left = state;
        else if (component == 2)
            right = state;
    }
}
