using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 管理旧版空间音效开关的协议映射。
public static class SpatialSound
{
    public const byte FeatureId = 0x1B;

    // 旧版空间音效使用通用功能开关，三模式空间音频由 SpatialAudio 负责。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("spatial-sound")
            && capability.SupportsCommand(CommandId.SetFeature);

    // 从功能状态快照读取旧版空间音效状态。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 构造旧版空间音效开关负载。
    public static byte[] BuildPayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
