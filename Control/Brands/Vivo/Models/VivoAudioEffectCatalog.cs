namespace OppoPodsManager.Control.Brands.Vivo.Models;

// 按 vivo 官方 audio_effect 版本提供可用内置音效的协议 ID。
internal static class VivoAudioEffectCatalog
{
    public const byte DeepXCustomEffect = 0x10;

    // 协议键保持稳定，显示文本由本地化资源层转换。
    public static IReadOnlyList<string> GetPresetKeys(int version)
        => GetPresetIds(version).Select(id => $"Vivo.AudioEffect.{id}").ToArray();

    public static bool TryGetPresetId(string? presetKey, out byte presetId)
    {
        presetId = 0;
        const string prefix = "Vivo.AudioEffect.";
        return !string.IsNullOrWhiteSpace(presetKey)
            && presetKey.StartsWith(prefix, StringComparison.Ordinal)
            && byte.TryParse(presetKey[prefix.Length..], out presetId);
    }

    public static string GetPresetKey(byte presetId) => $"Vivo.AudioEffect.{presetId}";

    // 官方设置服务把五种游戏场景音效统一展示为沉浸环绕预设。
    public static bool TryNormalizeReportedPreset(byte reportedPresetId, out byte presetId)
    {
        presetId = reportedPresetId switch
        {
            6 or 7 or 8 or 9 or 10 => 4,
            DeepXCustomEffect => 0,
            _ => reportedPresetId
        };
        return reportedPresetId != DeepXCustomEffect;
    }

    private static IReadOnlyList<byte> GetPresetIds(int version) => version switch
    {
        2 or 10 => [0, 1, 2, 3, 4],
        3 or 11 => [0, 1, 2, 3, 4],
        4 or 12 => [0, 1, 2, 3, 5],
        5 or 13 => [0, 1, 2, 3, 4, 5],
        6 or 14 => [0, 1, 2, 3, 4, 5],
        25 => [0, 1, 2, 3, 11],
        27 => [0, 1, 2, 3, 4, 11],
        28 => [0, 1, 2, 3, 5, 11],
        30 => [0, 1, 2, 3, 4, 5, 11],
        _ => [0, 1, 2, 3]
    };
}
