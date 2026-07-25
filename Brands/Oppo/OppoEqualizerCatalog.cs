using System.Reflection;
using System.Text.Json;

namespace OppoPodsManager.Brands.Oppo;

public sealed class OppoEqualizerCatalog
{
    private readonly Assembly _assembly;

    public OppoEqualizerCatalog()
    {
        _assembly = typeof(OppoEqualizerCatalog).Assembly;
    }


    public string? ResolveName(string modeType, string? language = null)
    {
        var map = LoadNames(language);
        return map.TryGetValue(modeType, out var name) && !string.IsNullOrEmpty(name) ? name : null;
    }

    public IReadOnlyDictionary<string, string> LoadNames(string? language)
    {
        var suffix = string.IsNullOrWhiteSpace(language) || language.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $".{language}";
        var resourceName = $"OppoPodsManager.Brands.Oppo.EqModeNames{suffix}.json";
        using var stream = _assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("mapping", out var mapping)
            || mapping.ValueKind != JsonValueKind.Object)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in mapping.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
                result[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return result;
    }
}
