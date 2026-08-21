using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 定义听力增强的官方数据查询和检测命令边界。
public static class HearingEnhancement
{
    public const byte FeatureId = 0x0B;
    public const ushort QueryCommand = 0x0115;
    public const ushort SetCommand = 0x040D;
    public const ushort QueryResponse = 0x8115;
    public const ushort SetResponse = 0x840D;

    // 听力增强必须同时具备数据查询和检测流程命令，不能只看 0x0403。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("hearing-enhancement")
            && capability.SupportsCommand(QueryCommand)
            && capability.SupportsCommand(SetCommand);

    // 当前 UI 的开关仍通过 0x0403 状态项读取，供控制层判断是否可直接切换。
    public static bool HasFeatureSwitch(DeviceCapability capability)
        => IsSupported(capability) && capability.SupportsCommand(CommandId.SetFeature);

    // 从批量功能状态读取听力增强当前状态。
    public static bool TryRead(FeatureStateSnapshot states, out bool enabled)
        => states.TryGetValue(FeatureId, out enabled);

    // 在设备同时提供通用开关时构造兼容的开关负载。
    public static byte[] BuildFeaturePayload(bool enabled)
        => [FeatureId, enabled ? (byte)1 : (byte)0];
}
