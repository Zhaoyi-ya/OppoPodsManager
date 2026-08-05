namespace OppoPodsManager.Control.Edifier;

// 漫步者设备型号识别。蓝牙设备名通常为型号名（如 “W820NB”、“W200BT Plus”），
// 统一归一化（小写 + 仅保留字母数字）后按已知前缀判定是否为 Edifier 设备。
public static class EdifierModels
{
    // 已知 Edifier 型号家族（取自 deviceinfo.json 的 Name 字段与 Klinkore 的 RFCOMMName）。
    private static readonly string[] KnownPrefixes =
    [
        "w820nb",  // W820NB / W820NB Double Gold / W820NB Plus
        "w200bt",  // W200BT Plus
        "edifier"  // 通用品牌名兜底
    ];

    public static bool IsFamilyName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;

        var normalized = Normalize(deviceName);
        foreach (var prefix in KnownPrefixes)
        {
            if (normalized.Contains(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // 小写 + 仅保留字母数字。
    private static string Normalize(string value)
    {
        var characters = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                characters.Append(character);
        }

        return characters.ToString();
    }
}
