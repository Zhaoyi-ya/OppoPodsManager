using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Assets.Oplus;

namespace OppoPodsManager.Assets.Localization;

// 提供原界面需要的文案入口，设备能力和协议数据仍由 Control 层维护。
internal static class DeviceProfileLoader
{
    public static string AncLabel(string key) => key switch
    {
        "Off" => TranslationCatalog.Get("Anc_ModeOff"),
        "Transparency" => TranslationCatalog.Get("Anc_ModeTransparency"),
        "Adaptive" => TranslationCatalog.Get("Anc_ModeAdaptive"),
        "NC" => TranslationCatalog.Get("Anc_ModeNoiseCancellation"),
        "Smart" => TranslationCatalog.Get("Anc_SubSmart"),
        "Light" => TranslationCatalog.Get("Anc_SubLight"),
        "Medium" => TranslationCatalog.Get("Anc_SubMedium"),
        "Deep" => TranslationCatalog.Get("Anc_SubDeep"),
        _ => TranslationCatalog.Get("Anc_ModeNoiseCancellation")
    };

    public static string LocalizedEqName(string protocolName)
    {
        if (protocolName.StartsWith("Vivo.AudioEffect.", StringComparison.Ordinal))
            return TranslationCatalog.Get($"Vivo_AudioEffect_{protocolName["Vivo.AudioEffect.".Length..]}");

        return EqualizerNameData.GetDisplayName(protocolName);
    }
}
