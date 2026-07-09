using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OppoPodsManager;

/// <summary>
/// 从嵌入 JSON 资源加载设备能力数据。
/// AOT 兼容：用 JsonDocument 手动遍历，不依赖源生成器。
/// 只含静态加载/解析方法；模型属性见 <see cref="DeviceCapabilities"/>。
/// </summary>
public static class DeviceProfileLoader
{
    private static readonly List<JsonElement> _deviceModels = LoadDeviceModels();
    private static readonly Dictionary<string, string> _eqModeNames = LoadEqNameMap();
    private static readonly Dictionary<string, JsonElement> _modelsById = BuildIdIndex(_deviceModels);

    /// <summary>id → 条目索引（productId 主键，O(1)）。</summary>
    private static Dictionary<string, JsonElement> BuildIdIndex(List<JsonElement> models)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in models)
        {
            if (!(e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            bool hasFunc = e.TryGetProperty("function", out _);
            if (map.TryGetValue(id, out var prev) && prev.TryGetProperty("function", out _) && !hasFunc)
                continue;
            map[id] = e;
        }
        return map;
    }

    private static List<JsonElement> LoadDeviceModels()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("OppoPodsManager.DeviceModels.json");
            if (stream == null) return new List<JsonElement>();
            using var reader = new StreamReader(stream);
            var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("whiteList", out var wl)
                || wl.ValueKind != JsonValueKind.Array)
                return new List<JsonElement>();
            var list = wl.EnumerateArray().ToList();
            list.Sort((a, b) =>
            {
                var na = EntryName(a) ?? "";
                var nb = EntryName(b) ?? "";
                return nb.Length.CompareTo(na.Length);
            });
            return list;
        }
        catch { return new List<JsonElement>(); }
    }

    private static Dictionary<string, string> LoadEqNameMap()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("OppoPodsManager.EqModeNames.json");
            if (stream == null) return new Dictionary<string, string>();
            using var reader = new StreamReader(stream);
            var doc = JsonDocument.Parse(reader.ReadToEnd());
            var map = new Dictionary<string, string>();
            foreach (var kv in doc.RootElement.GetProperty("mapping").EnumerateObject())
                map[kv.Name] = kv.Value.GetString() ?? "";
            return map;
        }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>根据蓝牙名称自动检测设备能力。</summary>
    public static DeviceCapabilities Detect(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return Default();
        var norm = Normalize(deviceName);

        if (MatchWithFunctionPref(e => { var nm = Normalize(EntryName(e) ?? ""); return nm.Length > 0 && nm == norm; },
                                  deviceName) is { } r1) return r1;
        if (MatchWithFunctionPref(e => { var nm = Normalize(EntryName(e) ?? ""); return nm.Length >= 5 && norm.Contains(nm); },
                                  deviceName) is { } r2) return r2;
        if (MatchWithFunctionPref(e => { var nm = Normalize(EntryName(e) ?? ""); return norm.Length >= 5 && nm.Contains(norm); },
                                  deviceName) is { } r3) return r3;
        return new DeviceCapabilities { DeviceName = deviceName, ModelName = deviceName, IsSupported = false };
    }

    private static DeviceCapabilities? MatchWithFunctionPref(Func<JsonElement, bool> predicate, string deviceName)
    {
        JsonElement? fallback = null;
        foreach (var entry in _deviceModels)
        {
            if (!predicate(entry)) continue;
            if (entry.TryGetProperty("function", out _))
                return FromJson(entry, deviceName);
            fallback ??= entry;
        }
        return fallback is { } fb ? FromJson(fb, deviceName) : null;
    }

    /// <summary>按完整型号名手动覆盖检测。</summary>
    public static DeviceCapabilities ForceModel(string modelName)
    {
        return MatchWithFunctionPref(
            e => string.Equals(modelName, EntryName(e), StringComparison.OrdinalIgnoreCase),
            modelName) ?? Default();
    }

    /// <summary>按 productId 精确识别设备。</summary>
    public static DeviceCapabilities? DetectById(string productId, string? deviceName = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        return _modelsById.TryGetValue(productId, out var entry)
            ? FromJson(entry, deviceName ?? EntryName(entry) ?? productId)
            : null;
    }

    /// <summary>获取所有已知设备型号名称列表。</summary>
    public static List<string> GetModelNames()
    {
        var names = new List<string>();
        foreach (var entry in _deviceModels)
        {
            var name = EntryName(entry);
            if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                names.Add(name);
        }
        return names;
    }

    private static string? EntryName(JsonElement entry) =>
        entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

    private static bool FlagOn(JsonElement func, string key)
    {
        if (!func.TryGetProperty(key, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) && i >= 1,
            JsonValueKind.True => true,
            _ => false,
        };
    }

    private static bool FlagAnyPresent(JsonElement func, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!func.TryGetProperty(k, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number: if (v.TryGetInt32(out var i) && i >= 1) return true; break;
                case JsonValueKind.True: return true;
                case JsonValueKind.Object:
                case JsonValueKind.Array:
                case JsonValueKind.String: return true;
            }
        }
        return false;
    }

    private static DeviceCapabilities Default() => new() { ModelName = "Unknown" };

    private static DeviceCapabilities FromJson(JsonElement entry, string deviceName)
    {
        var name = EntryName(entry) ?? deviceName;
        var id = entry.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                 ? idEl.GetString() ?? "" : "";
        var caps = new DeviceCapabilities { DeviceName = deviceName, ModelName = name, ModelId = id };

        if (entry.TryGetProperty("protocolType", out var pt) && pt.ValueKind == JsonValueKind.Number)
            caps.ProtocolType = pt.GetInt32();
        if (entry.TryGetProperty("supportSpp", out var sp) && (sp.ValueKind == JsonValueKind.True || sp.ValueKind == JsonValueKind.False))
            caps.SupportSpp = sp.GetBoolean();
        caps.IsSupported = caps.SupportSpp && caps.ProtocolType != 0;

        if (!entry.TryGetProperty("function", out var func)) return caps;

        caps.HasSpatialSound = func.TryGetProperty("spatialTypes", out var st);
        if (st.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            foreach (var _ in st.EnumerateArray()) { n++; if (n >= 3) break; }
            if (n >= 3) caps.HasSpatialAudio = true;
        }

        if (func.TryGetProperty("multiDevicesConnect", out var mdc) && mdc.ValueKind == JsonValueKind.Number)
        {
            caps.MultiDevicesConnect = mdc.GetInt32();
            caps.HasDualDevice = caps.MultiDevicesConnect >= 1;
            caps.HasMultiConnectManage = caps.MultiDevicesConnect >= 2;
        }

        caps.HasHiResAudio         = FlagAnyPresent(func, "highAudio", "highToneQuality");
        caps.HasDolbyAtmos         = FlagOn(func, "dolbyAtmos");
        caps.HasCustomEq           = FlagOn(func, "customEqualizer");

        if (func.TryGetProperty("customEqFrequency", out var cef) && cef.ValueKind == JsonValueKind.Array)
        {
            caps.CustomEqFrequencies = cef.EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0).ToArray();
        }
        if (func.TryGetProperty("customEqMax", out var cem) && cem.ValueKind == JsonValueKind.Number)
            caps.CustomEqMaxPresets = cem.GetInt32();
        if (func.TryGetProperty("customEqUiVersion", out var cev) && cev.ValueKind == JsonValueKind.Number)
            caps.CustomEqUiVersion = cev.GetInt32();

        caps.HasHearingEnhancement = FlagAnyPresent(func, "hearingEnhancement", "hearingEnhancementNew");
        caps.HasPersonalNoise      = FlagOn(func, "personalNoise") || func.TryGetProperty("personalNoiseCompat", out _);
        caps.HasWearDetection      = FlagOn(func, "wearDetection");
        caps.HasAutoSwitchLink     = FlagOn(func, "autoSwitchLink");
        caps.HasFindDevice         = FlagOn(func, "findDevice");
        caps.HasClickTakePic       = FlagAnyPresent(func, "clickTakePic", "clickTakePicNew");
        caps.HasZenMode            = FlagOn(func, "zenMode");
        caps.HasEarScan            = FlagOn(func, "earScan");
        caps.HasBassEngine         = FlagOn(func, "bassEngineSupport");
        caps.HasCustomDress        = FlagOn(func, "customDress");
        caps.HasFitDetection       = FlagOn(func, "fitDetection");
        caps.HasVocalEnhance       = FlagOn(func, "vocalEnhance");
        caps.HasLongPowerMode      = FlagOn(func, "longPowerMode");
        caps.HasVoiceCommand       = FlagOn(func, "voiceCommand") || func.TryGetProperty("voiceCommandItems", out _);
        caps.HasSpeechPerception   = FlagOn(func, "speechPerception");
        caps.HasSleepDetection     = FlagOn(func, "sleepDetection");
        caps.HasHeadMotion         = FlagOn(func, "headMotion");
        caps.HasAiTranslate        = FlagAnyPresent(func, "aiTranslate", "aiTranslateCompat");
        caps.HasAiSummary          = FlagOn(func, "aiSummary");
        caps.HasMeetingAssistant   = FlagOn(func, "meetingAssistant");
        caps.HasWhiteNoise         = FlagOn(func, "whiteNoise");
        caps.HasSpineHealth        = FlagOn(func, "spineHealth");
        caps.HasDiagnostic         = FlagOn(func, "diagnostic");
        caps.HasFirmwareUpdate     = FlagOn(func, "autoFirmwareUpdate");
        caps.HasPromptVolume       = FlagOn(func, "promptVolume") || func.TryGetProperty("promptVolumeRange", out _);

        // 游戏模式（低延迟）是通用 SPP 能力：DeviceModels.json 里没有任何逐机型的 gameMode 标志
        // （既无 gameModeList，也无扁平 gameMode:1），melody 也是对所有机型发批量查询 0x28(FeatureGameMain)
        // 由设备回报实际支持/状态（见 ParseBatchStatus）。故凡受支持机型即开放游戏模式开关；
        // 不支持的设备回报里不含 0x28，开关保持默认、无副作用。
        caps.HasGameMode = caps.IsSupported || FunctionGameModeSupported(func);

        if (func.TryGetProperty("gameSoundList", out var gsl) && gsl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in gsl.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    int tv = t.GetInt32();
                    if (tv != 0) { caps.HasGameSound = true; caps.GameSoundType = (byte)tv; break; }
                }
            }
        }

        if (func.TryGetProperty("gameSoundMutexes", out var gsm) && gsm.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in gsm.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Number)
                    caps.GameSoundMutexes.Add(m.GetInt32());
        }

        if (func.TryGetProperty("noiseReductionMode", out var nrm))
        {
            foreach (var mode in nrm.EnumerateArray())
                if (mode.TryGetProperty("childrenMode", out _))
                    caps.HasAdaptiveAnc = true;

            BuildAncOptions(nrm, caps);

            bool hasChildren = false;
            foreach (var mode in nrm.EnumerateArray())
                if (mode.TryGetProperty("childrenMode", out _)) hasChildren = true;
            if (!hasChildren)
            {
                foreach (var mode in nrm.EnumerateArray())
                    if (mode.TryGetProperty("modeType", out var lt) && lt.GetInt32() == 5 &&
                        mode.TryGetProperty("protocolIndex", out var lp) && lp.GetInt32() == 0)
                    { caps.IsLegacyAnc = true; break; }
            }
        }

        var eqMap = new Dictionary<byte, string>();
        LoadEqModes(func, "equalizerMode", eqMap);
        LoadEqModes(func, "equalizerModeCompat", eqMap);
        LoadEqModes(func, "equalizerModeByVersion", eqMap);
        if (eqMap.Count == 0) eqMap[0] = "默认";
        ApplyEqNames(caps, eqMap);
        return caps;
    }

    private static void LoadEqModes(JsonElement func, string key, Dictionary<byte, string> eqMap)
    {
        if (!func.TryGetProperty(key, out var modes)) return;
        foreach (var mode in modes.EnumerateArray())
        {
            if (!mode.TryGetProperty("protocolIndex", out var pi)) continue;
            byte idx = pi.GetByte();
            string displayName = idx < 10 ? $"模式{idx}" : $"M{idx}";
            if (mode.TryGetProperty("modeType", out var mt))
                if (_eqModeNames.TryGetValue(mt.GetInt32().ToString(), out var n))
                    displayName = n;
            if (!eqMap.ContainsKey(idx)) eqMap[idx] = displayName;
        }
    }

    private static bool FunctionGameModeSupported(JsonElement func)
    {
        if (func.TryGetProperty("gameModeList", out var gml) && gml.ValueKind == JsonValueKind.Array)
        {
            bool any = false;
            foreach (var item in gml.EnumerateArray())
            {
                any = true;
                if (item.TryGetProperty("gameMode", out var gm) && gm.ValueKind == JsonValueKind.Number
                    && gm.GetInt32() == 1)
                    return true;
            }
            if (any) return false;
        }
        return func.TryGetProperty("gameMode", out var top) && top.ValueKind == JsonValueKind.Number
               && top.GetInt32() == 1;
    }

    private static void ApplyEqNames(DeviceCapabilities caps, Dictionary<byte, string> names)
    {
        caps.EqPresets = new Dictionary<string, byte>();
        caps.EqNames = new Dictionary<byte, string>(names);
        foreach (var (k, v) in names) caps.EqPresets[v] = k;
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
        _ => "Mode" + type
    };

    private static string AncLabel(string key) => key switch
    {
        "Off" => "关闭",
        "Transparency" => "通透",
        "Adaptive" => "自适应",
        "NC" => "降噪",
        "Smart" => "智能",
        "Light" => "轻度",
        "Medium" => "中度",
        "Deep" => "深度",
        _ => key
    };

    private static int MainRank(string key) => key switch
    {
        "NC" => 0, "Smart" => 0,
        "Transparency" => 1,
        "Adaptive" => 2,
        "Off" => 99,
        _ => 50
    };

    private static void BuildAncOptions(JsonElement nrm, DeviceCapabilities caps)
    {
        var idxToName = caps.AncIndexToName;
        var nameToIdx = caps.AncNameToIndex;
        var options = new List<AncOption>();

        foreach (var entry in nrm.EnumerateArray())
        {
            if (!entry.TryGetProperty("modeType", out var mt)) continue;
            int type = mt.GetInt32();
            string key = ModeKey(type);
            byte ownIdx = entry.TryGetProperty("protocolIndex", out var pi) ? pi.GetByte() : (byte)0;

            var hasChildren = entry.TryGetProperty("childrenMode", out var children)
                              && children.ValueKind == JsonValueKind.Array;

            var childOpts = new List<AncOption>();
            if (hasChildren)
            {
                foreach (var child in children.EnumerateArray())
                {
                    if (!child.TryGetProperty("protocolIndex", out var cpi)) continue;
                    int ctype = child.TryGetProperty("modeType", out var cmt) ? cmt.GetInt32() : type;
                    byte cidx = cpi.GetByte();
                    string ckey = ModeKey(ctype);
                    RegisterFlat(idxToName, nameToIdx, cidx, ckey);
                    childOpts.Add(new AncOption { Key = ckey, Label = AncLabel(ckey), ProtocolIndex = cidx, Sendable = true });
                }
            }

            if (childOpts.Count == 1 && childOpts[0].Key == key)
            {
                var only = childOpts[0];
                RegisterFlat(idxToName, nameToIdx, only.ProtocolIndex, key);
                options.Add(new AncOption { Key = key, Label = AncLabel(key), ProtocolIndex = only.ProtocolIndex, Sendable = true });
                continue;
            }

            if (childOpts.Count > 0)
            {
                options.Add(new AncOption { Key = key, Label = AncLabel(key), ProtocolIndex = ownIdx, Sendable = false, Children = childOpts });
            }
            else
            {
                RegisterFlat(idxToName, nameToIdx, ownIdx, key);
                options.Add(new AncOption { Key = key, Label = AncLabel(key), ProtocolIndex = ownIdx, Sendable = true });
            }
        }

        options.Sort((a, b) => MainRank(a.Key).CompareTo(MainRank(b.Key)));
        foreach (var o in options)
            o.Children.Sort((a, b) => MainRank(a.Key).CompareTo(MainRank(b.Key)));
        caps.AncOptions = options;
    }

    private static void RegisterFlat(Dictionary<byte, string> idxToName, Dictionary<string, byte> nameToIdx, byte idx, string name)
    {
        idxToName[idx] = name;
        if (!nameToIdx.ContainsKey(name)) nameToIdx[name] = idx;
    }

    private static string Normalize(string name) =>
        new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
