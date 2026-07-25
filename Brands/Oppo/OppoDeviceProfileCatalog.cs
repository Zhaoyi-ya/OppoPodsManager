using System.Reflection;
using System.Text.Json;

namespace OppoPodsManager.Brands.Oppo;

public sealed class OppoDeviceProfileCatalog
{
    private const string ResourceName = "OppoPodsManager.Brands.Oppo.DeviceModels.json";
    private readonly Lazy<JsonDocument?> _document = new(LoadDocument);

    public bool IsLoaded => _document.Value is not null;

    public IReadOnlyList<JsonElement> GetWhiteList()
    {
        var document = _document.Value;
        if (document is null || !document.RootElement.TryGetProperty("whiteList", out var whiteList)
            || whiteList.ValueKind != JsonValueKind.Array)
            return [];

        return whiteList.EnumerateArray().ToArray();
    }

    public JsonElement? FindByProductId(string productId)
    {
        foreach (var entry in GetWhiteList())
        {
            if (entry.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), productId, StringComparison.OrdinalIgnoreCase))
                return entry;
        }

        return null;
    }

    public string? GetName(JsonElement entry) =>
        entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    private static JsonDocument? LoadDocument()
    {
        var assembly = typeof(OppoDeviceProfileCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName);
        return stream is null ? null : JsonDocument.Parse(stream);
    }
}
