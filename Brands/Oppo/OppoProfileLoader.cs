using System.Reflection;
using System.Text.Json;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Loads Melody whitelist profiles into Core DeviceCapabilities.
/// Replaces the legacy Models/DeviceProfileLoader path.
/// </summary>
public sealed class OppoProfileLoader
{
    private readonly OppoDeviceProfileCatalog _catalog;
    private readonly OppoEqualizerCatalog _eqNames;

    public OppoProfileLoader(
        OppoDeviceProfileCatalog? catalog = null,
        OppoEqualizerCatalog? eqNames = null)
    {
        _catalog = catalog ?? new OppoDeviceProfileCatalog();
        _eqNames = eqNames ?? new OppoEqualizerCatalog();
    }

    public IReadOnlyList<string> GetModelNames()
    {
        var names = new List<string>();
        foreach (var entry in _catalog.GetWhiteList())
        {
            var name = _catalog.GetName(entry);
            if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                names.Add(name);
        }
        return names;
    }

    public DeviceCapabilities DetectByName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return Unsupported(deviceName);

        var norm = Normalize(deviceName);
        if (Match(e =>
            {
                var nm = Normalize(_catalog.GetName(e) ?? "");
                return nm.Length > 0 && nm == norm;
            }, deviceName) is { } exact)
            return exact;

        if (Match(e =>
            {
                var nm = Normalize(_catalog.GetName(e) ?? "");
                return nm.Length >= 5 && norm.Contains(nm);
            }, deviceName) is { } contains)
            return contains;

        if (Match(e =>
            {
                var nm = Normalize(_catalog.GetName(e) ?? "");
                return norm.Length >= 5 && nm.Contains(norm);
            }, deviceName) is { } reverse)
            return reverse;

        return Unsupported(deviceName, recognized: false);
    }

    public DeviceCapabilities ForceModel(string modelName) =>
        Match(e => string.Equals(modelName, _catalog.GetName(e), StringComparison.OrdinalIgnoreCase), modelName)
        ?? Unsupported(modelName);

    public DeviceCapabilities? DetectByProductId(string productId, string? deviceName = null)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        var entry = _catalog.FindByProductId(productId);
        return entry is null ? null : FromJson(entry.Value, deviceName ?? _catalog.GetName(entry.Value) ?? productId);
    }

    private DeviceCapabilities? Match(Func<JsonElement, bool> predicate, string deviceName)
    {
        JsonElement? fallback = null;
        foreach (var entry in _catalog.GetWhiteList())
        {
            if (!predicate(entry))
                continue;
            if (entry.TryGetProperty("function", out _))
                return FromJson(entry, deviceName);
            fallback ??= entry;
        }
        return fallback is { } fb ? FromJson(fb, deviceName) : null;
    }

    public DeviceCapabilities FromJson(JsonElement entry, string deviceName)
    {
        var name = _catalog.GetName(entry) ?? deviceName;
        var id = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? ""
            : "";

        var features = new HashSet<DeviceFeature> { DeviceFeature.Battery };
        var supportsCustomEq = false;
        var supportsMultiDevice = false;
        int? eqBands = null;
        byte gameSoundType = 0;
        byte gameModeFeature = 0;
        var gameSoundMutexes = new HashSet<int>();
        var customEqFrequencies = Array.Empty<int>();
        var customEqMax = 0;
        var customEqUi = 0;
        var spatialTypes = new List<int>();
        var multiDevicesConnect = 0;
        var hasMultiManage = false;
        var isLegacyAnc = false;
        var hasAdaptiveAnc = false;
        IReadOnlyList<AncOption> ancOptions = [];
        IReadOnlyDictionary<string, byte> ancModes = new Dictionary<string, byte>();
        IReadOnlyDictionary<byte, string> ancIndexToName = new Dictionary<byte, string>();
        IReadOnlyDictionary<string, byte> equalizerPresets = new Dictionary<string, byte>();
        IReadOnlyList<string> spatialModes = [];
        var protocolType = 1;
        var supportSpp = true;

        if (entry.TryGetProperty("protocolType", out var pt) && pt.ValueKind == JsonValueKind.Number)
            protocolType = pt.GetInt32();
        if (entry.TryGetProperty("supportSpp", out var sp) && (sp.ValueKind is JsonValueKind.True or JsonValueKind.False))
            supportSpp = sp.GetBoolean();
        var isSupported = supportSpp && protocolType != 0;

        if (!entry.TryGetProperty("function", out var func) || func.ValueKind != JsonValueKind.Object)
        {
            return new DeviceCapabilities(features)
            {
                ModelName = name,
                ModelId = id,
                DeviceName = deviceName,
                IsSupported = isSupported,
                ProtocolType = protocolType,
                SupportSpp = supportSpp,
            };
        }

        // Spatial
        if (func.TryGetProperty("spatialTypes", out var st) && st.ValueKind == JsonValueKind.Array)
        {
            spatialTypes = st.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.Number)
                .Select(v => v.GetInt32())
                .Distinct()
                .ToList();
            if (spatialTypes.Count > 0)
            {
                features.Add(DeviceFeature.SpatialAudio);
                spatialModes = spatialTypes.Select(t => t switch
                {
                    1 => "Fixed",
                    2 => "Track",
                    _ => "Off",
                }).Distinct().ToArray();
            }
        }
        else if (FlagOn(func, "spatialAudio") || FlagOn(func, "spatialSound"))
        {
            features.Add(DeviceFeature.SpatialAudio);
        }

        // Multi-device
        if (func.TryGetProperty("multiDevicesConnect", out var mdc) && mdc.ValueKind == JsonValueKind.Number)
        {
            multiDevicesConnect = mdc.GetInt32();
            if (multiDevicesConnect >= 1)
            {
                features.Add(DeviceFeature.MultiDevice);
                features.Add(DeviceFeature.DualDevice);
                supportsMultiDevice = multiDevicesConnect >= 2;
                hasMultiManage = multiDevicesConnect >= 2;
            }
        }
        else if (func.TryGetProperty("multiConnectFunctions", out var mcf)
                 && mcf.ValueKind == JsonValueKind.Array
                 && mcf.GetArrayLength() > 0)
        {
            features.Add(DeviceFeature.MultiDevice);
            features.Add(DeviceFeature.DualDevice);
            foreach (var fn in mcf.EnumerateArray())
            {
                if (fn.TryGetProperty("functionType", out var ft)
                    && ft.ValueKind == JsonValueKind.String
                    && ft.GetString() == "unpairDevice")
                {
                    hasMultiManage = true;
                    multiDevicesConnect = 2;
                    supportsMultiDevice = true;
                    break;
                }
            }
            if (multiDevicesConnect == 0)
                multiDevicesConnect = 1;
        }

        // EQ
        var eqMap = new Dictionary<byte, string>();
        LoadEqModes(func, "equalizerMode", eqMap);
        LoadEqModes(func, "equalizerModeCompat", eqMap);
        LoadEqModes(func, "equalizerModeByVersion", eqMap);
        if (eqMap.Count > 0)
        {
            features.Add(DeviceFeature.Equalizer);
            var presets = new Dictionary<string, byte>();
            foreach (var (k, v) in eqMap)
                presets[v] = k;
            equalizerPresets = presets;
        }

        supportsCustomEq = FlagOn(func, "customEqualizer");
        if (func.TryGetProperty("customEqFrequency", out var cef) && cef.ValueKind == JsonValueKind.Array)
        {
            customEqFrequencies = cef.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0)
                .ToArray();
            if (customEqFrequencies.Length > 0)
            {
                supportsCustomEq = true;
                eqBands = customEqFrequencies.Length;
                features.Add(DeviceFeature.Equalizer);
            }
        }
        if (func.TryGetProperty("customEqMax", out var cem) && cem.ValueKind == JsonValueKind.Number)
            customEqMax = cem.GetInt32();
        if (func.TryGetProperty("customEqUiVersion", out var cev) && cev.ValueKind == JsonValueKind.Number)
            customEqUi = cev.GetInt32();

        // Gaming
        if (FunctionGameModeSupported(func))
        {
            features.Add(DeviceFeature.Gaming);
            gameModeFeature = 0x06;
        }
        if (func.TryGetProperty("gameSoundList", out var gsl) && gsl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in gsl.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    var tv = t.GetInt32();
                    if (tv != 0)
                    {
                        features.Add(DeviceFeature.Gaming);
                        features.Add(DeviceFeature.GameSound);
                        gameModeFeature = 0x28;
                        gameSoundType = (byte)tv;
                        break;
                    }
                }
            }
        }
        if (func.TryGetProperty("gameSoundMutexes", out var gsm) && gsm.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in gsm.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Number)
                    gameSoundMutexes.Add(m.GetInt32());
        }

        // Feature flags
        if (FlagOn(func, "findDevice") || FlagOn(func, "FindDevice"))
            features.Add(DeviceFeature.FindDevice);
        if (FlagOn(func, "keyFunction") || FlagOn(func, "clickTakePic") || FlagOn(func, "clickTakePicNew"))
            features.Add(DeviceFeature.TouchControls);
        if (FlagOn(func, "autoFirmwareUpdate") || FlagOn(func, "firmwareUpdate"))
            features.Add(DeviceFeature.FirmwareUpdate);
        if (FlagOn(func, "bassEngineSupport") || FlagOn(func, "bassEngine"))
            features.Add(DeviceFeature.BassEngine);
        if (FlagOn(func, "vocalEnhance"))
            features.Add(DeviceFeature.VocalEnhance);
        if (FlagAnyPresent(func, "hearingEnhancement", "hearingEnhancementNew", "hearingEnhance"))
            features.Add(DeviceFeature.HearingEnhance);
        if (FlagOn(func, "longPowerMode"))
            features.Add(DeviceFeature.LongPowerMode);
        if (FlagOn(func, "wearDetection"))
            features.Add(DeviceFeature.WearDetection);
        if (FlagOn(func, "spineHealth") || FlagOn(func, "spineLiveMonitor"))
            features.Add(DeviceFeature.SpineHealth);

        // ANC
        if (func.TryGetProperty("noiseReductionMode", out var nrm) && nrm.ValueKind == JsonValueKind.Array)
        {
            features.Add(DeviceFeature.Anc);
            foreach (var mode in nrm.EnumerateArray())
                if (mode.TryGetProperty("childrenMode", out _))
                    hasAdaptiveAnc = true;

            var built = BuildAncOptions(nrm);
            ancOptions = built.Options;
            ancModes = built.NameToIndex;
            ancIndexToName = built.IndexToName;

            if (!hasAdaptiveAnc)
            {
                foreach (var mode in nrm.EnumerateArray())
                {
                    if (mode.TryGetProperty("modeType", out var lt) && lt.GetInt32() == 5
                        && mode.TryGetProperty("protocolIndex", out var lp) && lp.GetInt32() == 0)
                    {
                        isLegacyAnc = true;
                        break;
                    }
                }
            }
        }

        return new DeviceCapabilities(
            features,
            SupportsCustomEqualizer: supportsCustomEq,
            SupportsMultiDevice: supportsMultiDevice,
            EqualizerBandCount: eqBands,
            EqualizerPresets: equalizerPresets,
            AncModes: ancModes,
            SpatialModes: spatialModes)
        {
            ModelName = name,
            ModelId = id,
            DeviceName = deviceName,
            IsSupported = isSupported,
            ProtocolType = protocolType,
            SupportSpp = supportSpp,
            GameSoundType = gameSoundType,
            GameModeFeature = gameModeFeature,
            GameSoundMutexes = gameSoundMutexes,
            CustomEqFrequencies = customEqFrequencies,
            CustomEqMaxPresets = customEqMax,
            CustomEqUiVersion = customEqUi,
            SpatialTypes = spatialTypes,
            HasSpatialAudio = spatialTypes.Count > 0,
            HasSpatialSound = false,
            MultiDevicesConnect = multiDevicesConnect,
            HasMultiConnectManage = hasMultiManage,
            IsLegacyAnc = isLegacyAnc,
            HasAdaptiveAnc = hasAdaptiveAnc,
            AncOptions = ancOptions,
            AncIndexToName = ancIndexToName,
        };
    }

    private void LoadEqModes(JsonElement func, string key, Dictionary<byte, string> eqMap)
    {
        if (!func.TryGetProperty(key, out var modes) || modes.ValueKind != JsonValueKind.Array)
            return;

        foreach (var mode in modes.EnumerateArray())
        {
            if (!mode.TryGetProperty("protocolIndex", out var pi) || pi.ValueKind != JsonValueKind.Number)
                continue;
            var idx = pi.GetByte();
            var displayName = $"M{idx}";
            if (mode.TryGetProperty("modeType", out var mt) && mt.ValueKind == JsonValueKind.Number)
            {
                var mapped = _eqNames.ResolveName(mt.GetInt32().ToString());
                if (!string.IsNullOrEmpty(mapped))
                    displayName = mapped!;
            }
            if (!eqMap.ContainsKey(idx))
                eqMap[idx] = displayName;
        }
    }

    private static bool FunctionGameModeSupported(JsonElement func)
    {
        if (func.TryGetProperty("gameModeList", out var gml) && gml.ValueKind == JsonValueKind.Array)
        {
            var any = false;
            foreach (var item in gml.EnumerateArray())
            {
                any = true;
                if (item.TryGetProperty("gameMode", out var gm)
                    && gm.ValueKind == JsonValueKind.Number
                    && gm.GetInt32() == 1)
                    return true;
            }
            if (any)
                return false;
        }
        return func.TryGetProperty("gameMode", out var top)
               && top.ValueKind == JsonValueKind.Number
               && top.GetInt32() == 1;
    }

    private static (List<AncOption> Options, Dictionary<string, byte> NameToIndex, Dictionary<byte, string> IndexToName)
        BuildAncOptions(JsonElement nrm)
    {
        var idxToName = new Dictionary<byte, string>();
        var nameToIdx = new Dictionary<string, byte>();
        var options = new List<AncOption>();

        foreach (var entry in nrm.EnumerateArray())
        {
            if (!entry.TryGetProperty("modeType", out var mt))
                continue;
            var type = mt.GetInt32();
            var key = ModeKey(type);
            var ownIdx = entry.TryGetProperty("protocolIndex", out var pi) ? pi.GetByte() : (byte)0;
            var hasChildren = entry.TryGetProperty("childrenMode", out var children)
                              && children.ValueKind == JsonValueKind.Array;
            var childOpts = new List<AncOption>();
            if (hasChildren)
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("protocolIndex", out var cpi))
                        continue;
                    var ctype = child.TryGetProperty("modeType", out var cmt) ? cmt.GetInt32() : type;
                    var cidx = cpi.GetByte();
                    var ckey = ModeKey(ctype);
                    RegisterFlat(idxToName, nameToIdx, cidx, ckey);
                    childOpts.Add(new AncOption
                    {
                        Key = ckey,
                        Label = ckey,
                        ProtocolIndex = cidx,
                        Sendable = true,
                    });
                }
            }

            if (childOpts.Count == 1 && childOpts[0].Key == key)
            {
                var only = childOpts[0];
                RegisterFlat(idxToName, nameToIdx, ownIdx, key);
                nameToIdx[key] = ownIdx;
                if (only.ProtocolIndex != ownIdx)
                    idxToName[only.ProtocolIndex] = key;
                options.Add(new AncOption { Key = key, Label = key, ProtocolIndex = ownIdx, Sendable = true });
                continue;
            }

            if (childOpts.Count > 0)
            {
                options.Add(new AncOption
                {
                    Key = key,
                    Label = key,
                    ProtocolIndex = ownIdx,
                    Sendable = false,
                    Children = childOpts,
                });
            }
            else
            {
                RegisterFlat(idxToName, nameToIdx, ownIdx, key);
                options.Add(new AncOption { Key = key, Label = key, ProtocolIndex = ownIdx, Sendable = true });
            }
        }

        options.Sort((a, b) => MainRank(a.Key).CompareTo(MainRank(b.Key)));
        return (options, nameToIdx, idxToName);
    }

    private static void RegisterFlat(
        Dictionary<byte, string> idxToName,
        Dictionary<string, byte> nameToIdx,
        byte idx,
        string name)
    {
        idxToName[idx] = name;
        if (!nameToIdx.ContainsKey(name))
            nameToIdx[name] = idx;
    }

    private static string ModeKey(int type) => type switch
    {
        1 => "Off",
        2 => "Transparency",
        3 => "Light",
        4 => "Deep",
        5 => "NC",
        6 => "Adaptive",
        7 => "Smart",
        8 => "Medium",
        10 => "Adaptive",
        _ => "Mode" + type,
    };

    private static int MainRank(string key) => key switch
    {
        "NC" => 0,
        "Smart" => 0,
        "Transparency" => 1,
        "Adaptive" => 2,
        "Off" => 99,
        _ => 50,
    };

    private static bool FlagOn(JsonElement func, string key)
    {
        if (!func.TryGetProperty(key, out var v))
            return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) && i >= 1,
            JsonValueKind.True => true,
            JsonValueKind.Object => true,
            JsonValueKind.Array => v.GetArrayLength() > 0,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
            _ => false,
        };
    }

    private static bool FlagAnyPresent(JsonElement func, params string[] keys)
    {
        foreach (var k in keys)
            if (FlagOn(func, k))
                return true;
        return false;
    }

    private static DeviceCapabilities Unsupported(string? deviceName, bool recognized = true) =>
        new(new HashSet<DeviceFeature>())
        {
            DeviceName = deviceName ?? "",
            ModelName = recognized ? "Unknown" : "Unrecognized",
            IsSupported = false,
        };

    private static string Normalize(string name) =>
        new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
