using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析电量通知和查询响应，并维护左右耳及充电盒状态。
public sealed class Battery : IDisposable
{
    private readonly BusinessState _state;
    private readonly Notifier _notifier;

    public Battery(BusinessState state, Notifier notifier)
    {
        _state = state;
        _notifier = notifier;
        _notifier.NotificationReceived += OnNotificationReceived;
    }

    public void Apply(ReadOnlySpan<byte> payload)
    {
        if (!TryParse(payload, out var left, out var right, out var chargingCase))
            return;

        _state.SetBattery(left, right, chargingCase);
    }

    public void Dispose()
    {
        _notifier.NotificationReceived -= OnNotificationReceived;
    }

    public static bool TryParse(
        ReadOnlySpan<byte> payload,
        out BatteryLevel? left,
        out BatteryLevel? right,
        out BatteryLevel? chargingCase)
    {
        left = null;
        right = null;
        chargingCase = null;
        var data = payload;
        // 0x8106 查询响应带 status，0x0204 通知则直接从电量列表开始。
        if (data.Length >= 2 && data[0] == 0)
            data = data[1..];
        if (data.Length < 1)
            return false;

        var count = data[0];
        if (count == 4 && data.Length >= 7)
        {
            left = CreateLevel(data[1], data[2] != 0);
            right = CreateLevel(data[3], data[4] != 0);
            chargingCase = CreateLevel(data[5], data[6] != 0);
            return true;
        }

        if (count is > 0 and <= 8 && data.Length >= 1 + count * 2)
        {
            for (var index = 0; index < count; index++)
            {
                ApplyEntry(data[1 + index * 2], data[2 + index * 2], ref left, ref right, ref chargingCase);
            }

            return left is not null || right is not null || chargingCase is not null;
        }

        // 部分设备省略数量字段，直接连续给出 component/raw 对。
        for (var index = 0; index + 1 < data.Length; index += 2)
            ApplyEntry(data[index], data[index + 1], ref left, ref right, ref chargingCase);

        return left is not null || right is not null || chargingCase is not null;
    }

    // 将一条 component/raw 电量记录合并到当前三段电量结果。
    private static void ApplyEntry(
        byte component,
        byte raw,
        ref BatteryLevel? left,
        ref BatteryLevel? right,
        ref BatteryLevel? chargingCase)
    {
        var percentage = (byte)(raw & 0x7F);
        // Melody 用 0 表示耳机或充电盒不在位，不能误显示为真实的 0% 电量。
        if (percentage == 0)
            return;
        var level = new BatteryLevel(percentage, (raw & 0x80) != 0);
        switch (component)
        {
            case 1:
                left = level;
                break;
            case 2:
                right = level;
                break;
            case 3:
                chargingCase = level;
                break;
        }
    }

    // 设备用 0 表示耳机或充电盒不在位，转换为空值让界面显示未知占位。
    private static BatteryLevel? CreateLevel(byte percentage, bool charging)
        => percentage == 0 ? null : new BatteryLevel(percentage, charging);

    private void OnNotificationReceived(object? sender, NotificationReceived notification)
    {
        if (notification.EventId == Notifier.BatteryEvent)
            Apply(notification.Data.Span);
    }
}
