using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Assets.Localization;

// 将协议状态和运行时默认值转换为当前语言的界面文案。
internal static class DeviceText
{
    // 为未上报电量的设备部件提供统一占位文本。
    public static string Battery(BatteryLevel? level)
        => level is { } value ? $"{value.Percent}%" : TranslationCatalog.Get("Battery_Unavailable");

    // 按设备支持的降噪模式读取对应语言文案。
    public static string NoiseModeName(NoiseMode mode)
        => TranslationCatalog.Get(mode switch
        {
            NoiseMode.NoiseCancellation => "AncMode_NoiseCancellation",
            NoiseMode.Transparency => "AncMode_Transparency",
            NoiseMode.Smart => "AncMode_Adaptive",
            NoiseMode.Adaptive => "AncMode_Adaptive",
            NoiseMode.Light => "Anc_SubLight",
            NoiseMode.Medium => "Anc_SubMedium",
            NoiseMode.Deep => "Anc_SubDeep",
            _ => "AncMode_Off"
        });

    // 按空间音频模式读取对应语言文案。
    public static string SpatialAudioModeName(SpatialAudioMode mode)
        => TranslationCatalog.Get(mode switch
        {
            SpatialAudioMode.Fixed => "SpatialAudio_ModeFixed",
            SpatialAudioMode.HeadTracking => "SpatialAudio_ModeHeadTrack",
            _ => "SpatialAudio_ModeOff"
        });

    // 将耳机佩戴状态转换为当前语言文案。
    public static string WearState(EarWearState state)
        => TranslationCatalog.Get(state switch
        {
            EarWearState.Worn => "Wear_Wearing",
            EarWearState.InCase => "Wear_InCase",
            EarWearState.Removed => "Wear_Removed",
            EarWearState.Disconnected => "Wear_Disconnected",
            _ => "Wear_Unknown"
        });

    // 优先保留设备实际名称，缺失时使用本地化默认名称。
    public static string DeviceName(params string?[] names)
        => names.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? TranslationCatalog.Get("Common_AppDeviceName");

    // 为多设备协议中未提供名称的记录创建本地化名称。
    public static string MultiDeviceName(ConnectedDeviceSnapshot device)
        => !string.IsNullOrWhiteSpace(device.Name)
            ? device.Name
            : string.Format(TranslationCatalog.Get("MultiDevice_Unnamed"), device.Address.Length >= 5 ? device.Address[^5..] : device.Address);
}
