using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// 薄封装：全部能力位图逻辑在 <see cref="OppoCapabilityBitmap"/>。
/// 保留本类型以兼容现有 Session/测试调用点。
/// </summary>
public sealed class OppoCapabilityResolver
{
    public IReadOnlySet<ushort> ParseSupportedCommands(ReadOnlySpan<byte> payload) =>
        OppoCapabilityBitmap.Parse(payload).SupportedCommands;

    public OppoCapabilityBitmap ParseBitmap(ReadOnlySpan<byte> payload) =>
        OppoCapabilityBitmap.Parse(payload);

    public DeviceCapabilities Resolve(
        DeviceCapabilities staticProfile,
        IReadOnlySet<ushort> supportedCommands) =>
        OppoCapabilityBitmap.FromCommands(supportedCommands).Resolve(staticProfile);

    public DeviceCapabilities Resolve(
        DeviceCapabilities staticProfile,
        OppoCapabilityBitmap bitmap) =>
        bitmap.Resolve(staticProfile);

    public DeviceCapabilities Resolve(
        IReadOnlySet<DeviceFeature> staticFeatures,
        IReadOnlySet<ushort> supportedCommands,
        bool supportsCustomEqualizer = false,
        bool supportsMultiDevice = false,
        int? equalizerBandCount = null)
    {
        var staticProfile = new DeviceCapabilities(
            staticFeatures,
            SupportsCustomEqualizer: supportsCustomEqualizer,
            SupportsMultiDevice: supportsMultiDevice,
            EqualizerBandCount: equalizerBandCount);
        return Resolve(staticProfile, supportedCommands);
    }

    public static string BitmapBits(ReadOnlySpan<byte> bitmap) =>
        OppoCapabilityBitmap.BitmapBits(bitmap);
}
