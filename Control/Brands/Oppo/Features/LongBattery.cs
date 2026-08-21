using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 管理长续航模式通用功能开关的协议映射。
public static class LongBattery
{
    public const byte FeatureId = 0x17;

    // 长续航模式由 0x0403 和功能状态项共同确认。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("long-battery")
            && capability.SupportsCommand(CommandId.SetFeature);

    // 从功能状态快照读取长续航模式当前值。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 构造通用功能开关负载。
    public static byte[] BuildPayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
