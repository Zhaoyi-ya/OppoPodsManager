using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Oppo.Features;

// 保留脊柱健康协议定义，但按官方桌面端规则不开放该控件。
public static class SpineHealth
{
    public const byte FeatureId = 0x22;

    // 当前官方实现没有把脊柱健康作为普通开关暴露给桌面端。
    public static bool IsSupported(DeviceCapability capability) => false;

    // 从功能状态快照读取协议值，供诊断和后续专用流程使用。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 构造协议层通用开关负载，调用方仍需先通过 IsSupported 门控。
    public static byte[] BuildPayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
