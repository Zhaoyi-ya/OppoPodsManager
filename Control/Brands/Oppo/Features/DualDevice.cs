using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 管理双设备连接通用开关的协议映射。
public static class DualDevice
{
    public const byte FeatureId = 0x11;

    // 双设备开关使用 0x0403，并且必须同时存在型号声明和状态项。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("dual-device")
            && capability.SupportsCommand(CommandId.SetFeature);

    // 从功能状态快照读取双设备开关当前值。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 构造通用功能开关负载。
    public static byte[] BuildPayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
