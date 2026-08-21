using System.Reflection;
using System.Text.Json;
using OppoPodsManager.Control.Brands.Vivo;
using OppoPodsManager.Control.Brands.Vivo.Models;

namespace OppoPodsManager.Assets.Vivo;

// 从 vivo APK 的 assets/tws_config.json 读取官方型号和功能白名单。
public static class VivoDeviceModelData
{
    private const string ResourceName = "OppoPodsManager.Assets.Vivo.Data.DeviceModels.json";

    // 按 ModelId 的 per-model 噪声模式 SET 后缀覆盖点（对齐官方 App / Windows 逆向参考 VivoProfiles）。
    // SET 噪声模式帧(0x0130) 载荷固定为 [mode, ..NoiseSetSuffix]，NoiseSetSuffix 为按型号的固定后缀：
    //   · 默认(Canonical) = [0x03, 0x01] → v4 家族（TWS 4 / TWS 4 Hi-Fi / TWS Air3 / iQOO TWS 2 等），与官方 App 抓包逐字节一致。
    //   · TWS 3e (DPD2321A) = [0x03]      → v3，官方 App 逆向（Windows 参考 Tws3eV3.NoiseSetSuffix=[0x03]）。
    //   · Air3 Pro 系 (DPD2431A/B/AC) = [0x04, 0x00] → v3，官方 App 逆向（Windows 参考 Air3ProV3.NoiseSetSuffix=[4,0]）。
    // 仅当某型号与官方 App 后缀不同才需在此按 ModelId 登记；mode 字节(0=NC/1=Off/2=Trans)全型号统一，无需覆盖。
    private static readonly Dictionary<int, VivoNoiseModeMap> NoiseModeOverrides = new()
    {
        // vivo TWS 3e（DPD2321A，ModelId 112/113）：v3 + [mode, 0x03]（官方 App 逆向，非旧工程臆测的 [mode, reduceModel]）。
        [112] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [0x03]),
        [113] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [0x03]),

        // vivo / iQOO TWS Air3 Pro 系（DPD2431A/B/AC，ModelId 168~190）：v3 + [mode, 0x04, 0x00]。
        [168] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [169] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [170] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [172] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [173] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [188] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [189] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
        [190] = new VivoNoiseModeMap(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault, NoiseSetSuffix: [4, 0]),
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
