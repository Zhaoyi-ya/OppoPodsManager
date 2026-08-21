using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 管理查找耳机命令，不把它误归类为 0x0403 功能开关。
public static class FindDevice
{
    public const ushort SetCommand = CommandId.SetFindDevice;
    public const ushort SetResponse = CommandId.SetFindDeviceResponse;

    // 查找耳机只要求白名单声明和 0x0400 命令存在。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("find-device")
            && capability.SupportsCommand(SetCommand);

    // 构造 setFindMode 的开始或停止负载。
    public static byte[] BuildPayload(bool enabled)
        => [enabled ? (byte)1 : (byte)0];
}
