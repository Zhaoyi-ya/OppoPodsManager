using System.Reflection;
using System.Text.Json;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Assets.Oplus;

public static class DeviceModelData
{
    private const string ResourceName = "OppoPodsManager.Assets.Oplus.Data.DeviceModels.json";

    public static ModelCatalog LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
            return new ModelCatalog([]);

        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.TryGetProperty("whiteList", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return new ModelCatalog([]);

        var models = new List<ModelDefinition>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (TryParseModel(entry, out var model))
                models.Add(model);
        }

        return new ModelCatalog(models);
    }

    private static bool TryParseModel(JsonElement entry, out ModelDefinition model)
    {
        model = null!;
        if (!TryGetString(entry, "name", out var name) || string.IsNullOrWhiteSpace(name))
            return false;

        var productId = TryGetString(entry, "id", out var id) ? id : string.Empty;
        var brand = TryGetString(entry, "brand", out var parsedBrand) ? parsedBrand : "OPPO";
        var series = TryGetString(entry, "type", out var parsedSeries) ? parsedSeries : "Other";
        var features = new HashSet<string>(StringComparer.Ordinal);
        var noiseModes = new Dictionary<byte, NoiseMode>();
        var noiseGroups = new List<NoiseModeGroup>();
        var equalizerPresets = new List<string>();
        var customEqFrequencies = new List<int>();
        var customEqMaxPresets = 0;
        var customEqUiVersion = 0;
        byte? preferredGameSoundType = null;
        var gameSoundMutexes = new HashSet<int>();
        if (entry.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            AddEnabledFeature(function, "wearDetection", "wear-detection", features);
            if (SupportsGameMode(function))
                features.Add("game-mode");
            AddEnabledFeature(function, "bassEngineSupport", "bass-engine", features);
            AddEnabledFeature(function, "vocalEnhance", "voice-enhancement", features);
            AddEnabledFeature(function, "longPowerMode", "long-battery", features);
            AddEnabledFeature(function, "findDevice", "find-device", features);
            AddEnabledFeature(function, "hearingEnhancement", "hearing-enhancement", features);
            AddDeclaredFeature(function, "hearingEnhancementNew", "hearing-enhancement", features);
            AddEnabledFeature(function, "spineHealth", "spine-health", features);
            AddEnabledFeature(function, "multiDevicesConnect", "dual-device", features);
            AddNoiseModes(function, noiseModes, noiseGroups);
            AddEqualizerPresets(function, equalizerPresets);
            AddCustomEqualizer(function, customEqFrequencies, ref customEqMaxPresets, ref customEqUiVersion, features);
            preferredGameSoundType = FindPreferredGameSoundType(function);
            AddGameSoundMutexes(function, gameSoundMutexes);

            if (noiseModes.Count > 0)
                features.Add("noise-cancellation");
            if (equalizerPresets.Count > 0)
                features.Add("equalizer");
            if (HasArray(function, "spatialTypes"))
                features.Add("spatial-configured");
            if (HasArray(function, "gameSoundList"))
            {
                features.Add("game-sound");
                // 官方将带游戏音效的型号归入游戏模式能力，再由 0x06/0x28 状态选择协议索引。
                features.Add("game-mode");
            }
            if (HasArray(function, "multiConnectFunctions") || IsEnabled(function, "multiDevicesConnect"))
                features.Add("multi-device");
        }

        model = new ModelDefinition(productId, name, brand, series, [], features, noiseModes, noiseGroups, equalizerPresets, customEqFrequencies, customEqMaxPresets, customEqUiVersion, preferredGameSoundType, gameSoundMutexes);
        return true;
    }

    // 从官方白名单读取自定义 EQ 的频率、预设数量和界面版本。
    private static void AddCustomEqualizer(
        JsonElement function,
        List<int> frequencies,
        ref int maximumPresets,
        ref int uiVersion,
        ISet<string> features)
    {
        if (function.TryGetProperty("customEqFrequency", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            frequencies.AddRange(values.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _))
                .Select(value => value.GetInt32())
                .Where(value => value >= 0));
        }

        if (function.TryGetProperty("customEqMax", out var maximum)
            && maximum.ValueKind == JsonValueKind.Number
            && maximum.TryGetInt32(out var parsedMaximum))
            maximumPresets = Math.Max(0, parsedMaximum);

        if (function.TryGetProperty("customEqUiVersion", out var version)
            && version.ValueKind == JsonValueKind.Number
            && version.TryGetInt32(out var parsedVersion))
            uiVersion = Math.Max(0, parsedVersion);

        if (frequencies.Count > 0)
            features.Add("custom-equalizer");
    }

    private static void AddNoiseModes(JsonElement function, Dictionary<byte, NoiseMode> modes, List<NoiseModeGroup> groups)
    {
        if (!function.TryGetProperty("noiseReductionMode", out var values)
            || values.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.EnumerateArray())
        {
            AddNoiseMode(value, modes);
            if (TryGetNoiseMode(value, out var parentMode)
                && parentMode == NoiseMode.NoiseCancellation
                && value.TryGetProperty("childrenMode", out var childValues)
                && childValues.ValueKind == JsonValueKind.Array)
            {
                var children = childValues.EnumerateArray()
                    .Where(child => child.TryGetProperty("protocolIndex", out _))
                    .Select(child => TryGetNoiseOption(child, out var option) ? option : null)
                    .Where(option => option is not null)
                    .Cast<NoiseModeOption>()
                    .ToArray();
                if (children.Length > 0)
                    groups.Add(new NoiseModeGroup(parentMode, children));
            }
            if (value.TryGetProperty("childrenMode", out var nestedChildren)
                && nestedChildren.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in nestedChildren.EnumerateArray())
                    AddNoiseMode(child, modes);
            }
        }
    }

    private static bool TryGetNoiseOption(JsonElement value, out NoiseModeOption option)
    {
        option = null!;
        if (!value.TryGetProperty("protocolIndex", out var index) || !index.TryGetByte(out var protocolIndex)
            || !TryGetNoiseMode(value, out var mode))
            return false;
        option = new NoiseModeOption(protocolIndex, mode);
        return true;
    }

    private static bool TryGetNoiseMode(JsonElement value, out NoiseMode mode)
    {
        mode = NoiseMode.Unknown;
        if (!value.TryGetProperty("modeType", out var type) || !type.TryGetInt32(out var modeType))
            return false;
        mode = modeType switch
        {
            1 => NoiseMode.Off,
            2 => NoiseMode.Transparency,
            3 => NoiseMode.Light,
            4 => NoiseMode.Deep,
            5 => NoiseMode.NoiseCancellation,
            6 or 7 or 10 => NoiseMode.Smart,
            8 => NoiseMode.Medium,
            _ => NoiseMode.NoiseCancellation
        };
        return true;
    }

    private static void AddNoiseMode(JsonElement value, Dictionary<byte, NoiseMode> modes)
    {
        if (!value.TryGetProperty("protocolIndex", out var index)
            || !index.TryGetByte(out var protocolIndex)
            || !value.TryGetProperty("modeType", out var type)
            || !type.TryGetInt32(out var modeType))
            return;

        modes[protocolIndex] = modeType switch
        {
            1 => NoiseMode.Off,
            2 => NoiseMode.Transparency,
            3 => NoiseMode.Light,
            4 => NoiseMode.Deep,
            5 => NoiseMode.NoiseCancellation,
            6 or 7 or 10 => NoiseMode.Smart,
            8 => NoiseMode.Medium,
            _ => NoiseMode.NoiseCancellation
        };
    }

    private static void AddEqualizerPresets(JsonElement function, List<string> presets)
    {
        var namedPresets = new SortedDictionary<byte, string>();
        AddEqualizerModes(function, "equalizerMode", namedPresets);
        AddEqualizerModes(function, "equalizerModeCompat", namedPresets);
        AddEqualizerModes(function, "equalizerModeByVersion", namedPresets);
        if (namedPresets.Count == 0)
            return;

        var maximum = namedPresets.Keys.Max();
        for (var index = 0; index <= maximum; index++)
            presets.Add(namedPresets.TryGetValue((byte)index, out var name) ? name : $"M{index}");
    }

    // 合并原项目兼容的三种 EQ 配置格式，并保留协议索引顺序。
    private static void AddEqualizerModes(JsonElement function, string propertyName, IDictionary<byte, string> modes)
    {
        if (!function.TryGetProperty(propertyName, out var values)
            || values.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetProperty("protocolIndex", out var index) && index.TryGetByte(out var preset))
            {
                var displayName = $"M{preset}";
                if (value.TryGetProperty("modeType", out var modeType)
                    && modeType.TryGetInt32(out var type))
                    displayName = EqualizerNameData.GetDisplayName($"M{type}");
                modes.TryAdd(preset, displayName);
            }
        }
    }

    // 从官方型号配置中选择首个非零游戏音效类型，0 表示关闭。
    private static byte? FindPreferredGameSoundType(JsonElement function)
    {
        if (!function.TryGetProperty("gameSoundList", out var values)
            || values.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetProperty("type", out var type)
                && type.TryGetByte(out var soundType)
                && soundType != 0)
                return soundType;
        }

        return null;
    }

    // 读取官方声明的游戏音效互斥项：1/3 对应均衡器，2 对应空间音效。
    private static void AddGameSoundMutexes(JsonElement function, ISet<int> mutexes)
    {
        if (!function.TryGetProperty("gameSoundMutexes", out var values)
            || values.ValueKind != JsonValueKind.Array)
            return;

        foreach (var value in values.EnumerateArray())
        {
            if (value.TryGetInt32(out var mutex))
                mutexes.Add(mutex);
        }
    }

    private static void AddEnabledFeature(JsonElement function, string sourceName, string feature, ISet<string> features)
    {
        if (IsEnabled(function, sourceName))
            features.Add(feature);
    }

    // 按官方 FlagAnyPresent 规则读取兼容字段，兼容布尔、数字和对象配置。
    private static void AddDeclaredFeature(JsonElement function, string sourceName, string feature, ISet<string> features)
    {
        if (!function.TryGetProperty(sourceName, out var value))
            return;

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.String
            || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 1)
            features.Add(feature);
    }

    // 官方优先检查 gameModeList；列表存在但没有启用项时不认为支持游戏模式。
    private static bool SupportsGameMode(JsonElement function)
    {
        if (function.TryGetProperty("gameModeList", out var values)
            && values.ValueKind == JsonValueKind.Array)
        {
            var hasEntries = false;
            foreach (var value in values.EnumerateArray())
            {
                hasEntries = true;
                if (value.TryGetProperty("gameMode", out var mode)
                    && mode.ValueKind == JsonValueKind.Number
                    && mode.TryGetInt32(out var enabled)
                    && enabled == 1)
                    return true;
            }

            if (hasEntries)
                return false;
        }

        return IsEnabled(function, "gameMode");
    }

    private static bool IsEnabled(JsonElement objectValue, string name)
        => objectValue.TryGetProperty(name, out var value)
            && ((value.ValueKind == JsonValueKind.True)
                || (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0));

    private static bool HasArray(JsonElement objectValue, string name)
        => objectValue.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() > 0;

    private static bool TryGetString(JsonElement objectValue, string name, out string value)
    {
        value = string.Empty;
        return objectValue.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty).Length > 0;
    }
}
