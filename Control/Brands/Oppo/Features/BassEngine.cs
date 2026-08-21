
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
namespace OppoPodsManager.Control.Brands.Oppo.Features;
// 定义低音引擎的专用协议入口和状态解析规则。
public static class BassEngine
{
    public const byte FeatureId = 0x1D;
    public const ushort QueryCommand = 0x0124;
    public const ushort SetCommand = 0x041B;
    public const ushort QueryResponse = 0x8124;
    public const ushort SetResponse = 0x841B;
    // 低音引擎不能用通用 0x0403 冒充，必须由专用设置命令确认能力。
    public static bool IsSupported(DeviceCapability capability)
        => capability.SupportsFeature("bass-engine")
            && capability.SupportsCommand(SetCommand);
    // 解析官方低音引擎响应中的最小值、最大值和当前值。
    public static bool TryParse(ReadOnlySpan<byte> payload, out BassEngineState state)
    {
        var offset = payload.Length >= 4 && payload[0] == 0 ? 1 : 0;
        if (payload.Length < offset + 3)
        {
            state = default;
            return false;
        }
        state = new BassEngineState(payload[offset], payload[offset + 1], payload[offset + 2]);
        return true;
    }
    // 构造官方 setBassEngineValue 使用的三元组负载。
    public static byte[] BuildValuePayload(BassEngineState state)
        => [state.Minimum, state.Maximum, state.Current];
}
// 表示低音引擎协议返回的值范围和当前值。
public readonly record struct BassEngineState(byte Minimum, byte Maximum, byte Current);
