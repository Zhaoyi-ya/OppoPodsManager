using OppoPodsManager.Localization;

namespace OppoPodsManager.Infrastructure;

/// <summary>UI-facing ANC / EQ display helpers (no protocol dependency).</summary>
public static class DeviceUiLabels
{
    public static string AncLabel(string key) => key switch
    {
        "Off" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_ModeOff),
        "Transparency" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_ModeTransparency),
        "Adaptive" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_ModeAdaptive),
        "NC" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_ModeNoiseCancellation),
        "Smart" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_SubSmart),
        "Light" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_SubLight),
        "Medium" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_SubMedium),
        "Deep" => LanguageManager.Instance.GetString(LanguageManager.Instance.Anc_SubDeep),
        _ => key,
    };

    public static string CodecName(int id) => id switch
    {
        0 => "SBC",
        1 => "AAC",
        2 => "aptX",
        3 => "LHDC",
        4 => "LC3",
        5 => "aptX",
        6 => "aptX HD",
        7 => "aptX Adaptive",
        8 => "LHDC",
        -1 => "-",
        _ => $"Unknown ({id})",
    };
}
