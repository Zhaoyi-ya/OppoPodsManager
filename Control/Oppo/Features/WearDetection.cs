using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Oppo.Features;

// 管理佩戴检测通用功能开关的协议映射。
public static class WearDetection
{
    public const byte FeatureId = 0x04;

    // 佩戴检测由 0x0403 和功能状态项共同确认。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("wear-detection")
            && capability.SupportsCommand(CommandId.SetFeature);

    // 从功能状态快照读取佩戴检测当前值。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 构造通用功能开关负载。
    public static byte[] BuildPayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
