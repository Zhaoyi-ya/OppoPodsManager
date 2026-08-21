using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析游戏模式状态通知。
public sealed class GameMode : IDisposable
{
    public const byte LegacyFeatureId = 0x06;
    public const byte ModernFeatureId = 0x28;

    private readonly BusinessState _state;
    private readonly Notifier _notifier;

    public GameMode(BusinessState state, Notifier notifier)
    {
        _state = state;
        _notifier = notifier;
        _notifier.NotificationReceived += OnNotificationReceived;
    }

    public void Apply(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 0)
            _state.SetGame(_state.Snapshot().Game with { IsEnabled = payload[0] != 0 });
    }

    // 按官方新旧协议和已回报的 feature 状态选择游戏模式索引。
    public static byte? ResolveFeatureId(
        DeviceCapability capability,
        FeatureStateSnapshot states)
    {
        if (!capability.SupportsFeature("game-mode"))
            return null;

        if (capability.SupportsFeature("game-sound")
            && states.Values.ContainsKey(ModernFeatureId))
            return ModernFeatureId;
        if (states.Values.ContainsKey(LegacyFeatureId))
            return LegacyFeatureId;
        if (states.Values.ContainsKey(ModernFeatureId))
            return ModernFeatureId;
        return null;
    }

    // 从功能状态快照同步新旧游戏模式的业务状态。
    public static bool TryRead(
        FeatureStateSnapshot states,
        out bool enabled)
    {
        if (states.TryGetValue(ModernFeatureId, out enabled))
            return true;
        return states.TryGetValue(LegacyFeatureId, out enabled);
    }

    public void Dispose() => _notifier.NotificationReceived -= OnNotificationReceived;

    private void OnNotificationReceived(object? sender, NotificationReceived notification)
    {
        if (notification.EventId == Notifier.GameModeEvent)
            Apply(notification.Data.Span);
    }
}
