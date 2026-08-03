using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Oppo.Features;

// 管理人声增强通用功能开关的协议映射。
public static class VoiceEnhancement
{
    public const byte FeatureId = 0x09;

    // 人声增强由 0x0403 和功能状态项共同确认。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("voice-enhancement")
            && capability.SupportsCommand(CommandId.SetFeature);

    // 从功能状态快照读取人声增强当前值。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 构造通用功能开关负载。
    public static byte[] BuildPayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
