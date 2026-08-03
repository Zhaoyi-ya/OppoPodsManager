using System.Reflection;
using System.Text.Json;
using OppoPodsManager.Assets.Localization;

namespace OppoPodsManager.Assets.Oplus;

// 将设备协议中的 M0、M1 等 EQ 标识转换为当前语言的显示名称。
public static class EqualizerNameData
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);

    // 返回用户可读名称；未知协议编号回退到本地化的“模式 N”。
    public static string GetDisplayName(string protocolName)
    {
        if (protocolName.Length < 2 || protocolName[0] != 'M' || !int.TryParse(protocolName[1..], out var index))
            return protocolName;

        var map = LoadMap(TranslationCatalog.CurrentLanguage);
        if (map.TryGetValue(index.ToString(), out var displayName) && !string.IsNullOrWhiteSpace(displayName))
            return displayName;

        return index == 0
            ? TranslationCatalog.Get("Eq_Default")
            : string.Format(TranslationCatalog.Get("Eq_ModeIndex"), index);
    }

    private static IReadOnlyDictionary<string, string> LoadMap(string language)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(language, out var cached))
                return cached;

            var suffix = language.Equals("zh", StringComparison.OrdinalIgnoreCase) ? string.Empty : $".{language}";
            var resourceName = $"OppoPodsManager.Assets.Oplus.Data.EqModeNames{suffix}.json";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
            if (stream is null)
                return Cache[language] = new Dictionary<string, string>();

            using var document = JsonDocument.Parse(stream);
            var map = document.RootElement.GetProperty("mapping")
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty);
            return Cache[language] = map;
        }
    }
}
