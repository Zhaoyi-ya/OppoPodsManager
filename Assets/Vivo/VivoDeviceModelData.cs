using System.Reflection;
using System.Text.Json;
using OppoPodsManager.Control.Vivo;
using OppoPodsManager.Control.Vivo.Models;

namespace OppoPodsManager.Assets.Vivo;

// 从 vivo APK 的 assets/tws_config.json 读取官方型号和功能白名单。
public static class VivoDeviceModelData
{
    private const string ResourceName = "OppoPodsManager.Assets.Vivo.Data.DeviceModels.json";

    // 按 ModelId 的 per-model 噪声模式覆盖点。
    // 当前所有已知 vivo 型号均使用 Canonical（APK 逆向证实全型号统一，无需覆盖）；
    // 若将来实测到某型号 mode 字节或 reduceModel 档位不同，在此按 ModelId 加一行即可，发送逻辑无需改动。
    private static readonly Dictionary<int, VivoNoiseModeMap> NoiseModeOverrides = new()
    {
        // [113] = new VivoNoiseModeMap(0x00, 0x01, 0x02, 0x04, 0x04, 0x01), // vivo TWS 3e（示例：与 Canonical 一致）
    };

    public static VivoModelCatalog LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
            return new VivoModelCatalog([]);

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("tws_config", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return new VivoModelCatalog([]);

        var models = new List<VivoModelDefinition>();
        foreach (var entry in entries.EnumerateArray())
            if (TryParseModel(entry, out var model))
                models.Add(model);
        return new VivoModelCatalog(models);
    }

    private static bool TryParseModel(JsonElement entry, out VivoModelDefinition model)
    {
        model = null!;
        if (!TryGetInt(entry, "model", out var modelId)
            || !TryGetString(entry, "name", out var name))
            return false;

        var projectName = TryGetString(entry, "project_name", out var project) ? project : string.Empty;
        var deviceType = TryGetInt(entry, "device_type", out var type) ? type : 0;
        var features = new Dictionary<string, int>(StringComparer.Ordinal);
        if (entry.TryGetProperty("feature", out var featureData) && featureData.ValueKind == JsonValueKind.Object)
        {
            foreach (var feature in featureData.EnumerateObject())
                if (feature.Value.ValueKind == JsonValueKind.Number && feature.Value.TryGetInt32(out var value))
                    features[feature.Name] = value;
        }

        model = new VivoModelDefinition(modelId, name, projectName, deviceType, features);
        // 按 ModelId 应用 per-model 噪声模式覆盖；未登记的型号使用 Canonical（APK 逆向证实全 vivo 型号统一）。
        model = model with
        {
            NoiseMap = NoiseModeOverrides.TryGetValue(modelId, out var overrideMap)
                ? overrideMap
                : VivoNoiseModeMap.Canonical,
        };
        return true;
    }

    private static bool TryGetString(JsonElement value, string propertyName, out string text)
    {
        text = string.Empty;
        return value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(text = property.GetString() ?? string.Empty);
    }

    private static bool TryGetInt(JsonElement value, string propertyName, out int number)
    {
        number = 0;
        return value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out number);
    }
}
