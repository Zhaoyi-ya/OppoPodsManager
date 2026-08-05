namespace OppoPodsManager.Control.Vivo;

// vivo / iQOO TWS 设备名初筛（对应 vivo_models.is_family_name）。
internal static class VivoModels
{
    public static bool IsFamilyName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;

        var normalized = Normalize(deviceName);
        return normalized.StartsWith("vivotws", System.StringComparison.Ordinal)
            || normalized.StartsWith("iqootws", System.StringComparison.Ordinal);
    }

    // 按设备名选择协议画像；未知型号回退家族默认 v4（兼容性最广）。
    // 归一化规则与 IsFamilyName 一致（小写 + 仅字母数字）。
    public static VivoProfile SelectProfile(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return VivoProfile.FamilyDefaultV4;

        var normalized = Normalize(deviceName);
        if (normalized.Contains("air3pro", System.StringComparison.Ordinal))
            return VivoProfile.Air3ProV3;
        if (normalized.Contains("tws3e", System.StringComparison.Ordinal))
            return VivoProfile.Tws3eV3;
        return VivoProfile.FamilyDefaultV4;
    }

    // 小写 + 仅保留字母数字，与 Kotlin normalize 一致。
    private static string Normalize(string value)
    {
        var characters = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
            if (char.IsLetterOrDigit(character))
                characters.Append(character);
        return characters.ToString();
    }
}
