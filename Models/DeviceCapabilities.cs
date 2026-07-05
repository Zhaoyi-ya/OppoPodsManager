using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace OppoPodsManager;

/// <summary>
/// 一个可选的 ANC 模式项（用于按 JSON 动态生成降噪 UI）。
/// 主模式可含若干子模式（如"降噪"下的智能/中度/超级/轻度）；无子模式则直接可发送。
/// </summary>
public sealed class AncOption
{
    public string Key { get; set; } = "";        // 内部键：Off/Transparency/Adaptive/NC/Smart/Light/Medium/Deep
    public string Label { get; set; } = "";       // UI 显示名
    public byte ProtocolIndex { get; set; }        // 发送时的位索引（有子模式时该值仅占位，实际发子模式）
    public bool Sendable { get; set; } = true;     // 该项本身能否直接发送（纯容器为 false）
    public List<AncOption> Children { get; set; } = new();
}

/// <summary>
/// 设备能力检测。从 DeviceModels.json 加载 whitelist 配置，按蓝牙名称匹配设备。
/// 支持能力、ANC 模式映射、EQ 名称均从 JSON 动态推导，添加新设备只需追加 whitelist 条目。
/// </summary>
public class DeviceCapabilities
{
    public string DeviceName { get; set; } = "";
    public string ModelName { get; set; } = "Unknown";
    public string ModelId { get; set; } = "";
    public int ProtocolType { get; set; } = 1;      // 0=旧版协议 1/2=新版；本程序命令层仅实现新版
    public bool SupportSpp { get; set; } = true;    // 是否支持经典 SPP（false 多为 BLE-only）
    public bool IsSupported { get; set; } = true;   // 名称匹配到 whitelist 且协议可用

    // ========== 功能标志（当前 UI 已用）==========
    public bool HasSpatialAudio { get; set; }      // cmd 0x0422 空间音频三模式（Off/Fixed/Track）
    public bool HasSpatialSound { get; set; }       // feature 0x1B 空间音效开关
    public bool HasDualDevice { get; set; }         // feature 0x11 双设备连接
    public bool HasAdaptiveAnc { get; set; }        // ANC 子模式（Smart/Light/Medium/Deep）
    public bool IsLegacyAnc { get; set; }           // noiseReductionMode 无子模式且 ANC On 在异常位置时启用值交换
    public bool HasGameMode { get; set; }           // 游戏模式（feature 0x28，专用字段 gameMode/gameModeList 判定）
    public bool HasGameSound { get; set; }           // 游戏音效（cmd 0x423，由 JSON gameSoundList 推导）
    public byte GameSoundType { get; set; }          // 游戏音效开启时发送的 type（gameSoundList 里首个非 0 类型）

    // ========== 扩展功能标志（对齐官方 whiteList.function，供未来界面使用）==========
    // 命名与官方字段/功能一一对应；开关类默认经 0x0403 + 对应 featureType 下发（见 OppoProtocol.Feature*）。
    // 命令化的功能标注了命令号；纯 JSON 声明能力（无独立开关命令）也一并暴露供 UI 判断是否展示入口。
    public bool HasHiResAudio { get; set; }          // highAudio / highToneQuality 高清/超清音质（编解码器切换 0x040E）
    public bool HasDolbyAtmos { get; set; }          // dolbyAtmos 杜比全景声（空间音频相关展示）
    public bool HasCustomEq { get; set; }            // customEqualizer 自定义 EQ（0x0418 setEqInfo）
    public bool HasHearingEnhancement { get; set; }  // hearingEnhancement / hearingEnhancementNew 听力增强
    public bool HasPersonalNoise { get; set; }       // personalNoise 个性化降噪（feature 0x0C / 0x0412）
    public bool HasWearDetection { get; set; }       // wearDetection 佩戴检测（feature 0x04）
    public bool HasAutoSwitchLink { get; set; }      // autoSwitchLink 自动切换连接
    public bool HasFindDevice { get; set; }          // findDevice 查找耳机（0x0400 setFindMode）
    public bool HasClickTakePic { get; set; }        // clickTakePic / clickTakePicNew 一键拍照（feature 0x0D）
    public bool HasZenMode { get; set; }             // zenMode 禅模式（feature 0x0F）
    public bool HasEarScan { get; set; }             // earScan 耳道扫描（0x011E/0x0415）
    public bool HasBassEngine { get; set; }          // bassEngineSupport 低音引擎（feature 0x1D / 0x041B）
    public bool HasCustomDress { get; set; }         // customDress 个性化装扮
    public bool HasFitDetection { get; set; }        // fitDetection 贴合度检测
    public bool HasVocalEnhance { get; set; }        // vocalEnhance 人声增强（feature 0x09）
    public bool HasLongPowerMode { get; set; }       // longPowerMode 长续航模式（feature 0x17）
    public bool HasVoiceCommand { get; set; }        // voiceCommand 语音指令（feature 0x19）
    public bool HasSpeechPerception { get; set; }    // speechPerception 人声感知（feature 0x32）
    public bool HasSleepDetection { get; set; }      // sleepDetection 睡眠检测（feature 0x3A）
    public bool HasHeadMotion { get; set; }          // headMotion 头部动作（feature 0x3B）
    public bool HasAiTranslate { get; set; }         // aiTranslate / aiTranslateCompat AI 翻译（0x0428）
    public bool HasAiSummary { get; set; }           // aiSummary AI 摘要（0x0425 / 0x012E）
    public bool HasMeetingAssistant { get; set; }    // meetingAssistant 会议助手
    public bool HasWhiteNoise { get; set; }          // whiteNoise 白噪音
    public bool HasSpineHealth { get; set; }         // spineHealth 脊柱健康（feature 0x22/0x23/0x24）
    public bool HasDiagnostic { get; set; }          // diagnostic 耳机检测
    public bool HasFirmwareUpdate { get; set; }      // autoFirmwareUpdate 固件更新
    public bool HasPromptVolume { get; set; }        // promptVolume 提示音量（0x0427）
    public bool HasMultiConnectManage { get; set; }  // multiDevicesConnect>=2 多设备管理（0x0112/0x0429）
    public int  MultiDevicesConnect { get; set; }    // multiDevicesConnect 原始值（0/1/2；2=支持列表管理）

    /// <summary>
    /// 游戏音效互斥组（JSON gameSoundMutexes，官方 GameSoundMutexHelper）。
    /// 值语义：1=调音(EQ) 2=空间音效 3=EQ相关 4=自适应听感。
    /// 表示"游戏音效强化"与这些功能同一时刻只能开一个（设备固件强制）。
    /// </summary>
    public HashSet<int> GameSoundMutexes { get; set; } = new();

    /// <summary>游戏音效是否与 EQ 调音互斥（mutex 含 1 或 3）。</summary>
    public bool GameSoundMutexEq => GameSoundMutexes.Contains(1) || GameSoundMutexes.Contains(3);
    /// <summary>游戏音效是否与空间音效互斥（mutex 含 2）。</summary>
    public bool GameSoundMutexSpatial => GameSoundMutexes.Contains(2);

    // ========== EQ 预设 ==========
    public Dictionary<string, byte> EqPresets { get; set; } = new();
    public Dictionary<byte, string> EqNames { get; set; } = new();

    /// <summary>protocolIndex → UI 模式名（按型号 noiseReductionMode 动态构建）</summary>
    public Dictionary<byte, string> AncIndexToName { get; set; } = new();

    /// <summary>UI 模式名 → protocolIndex（发送 ANC 时按型号取正确字节位）</summary>
    public Dictionary<string, byte> AncNameToIndex { get; set; } = new();

    /// <summary>按 JSON noiseReductionMode 构建的层级化 ANC 选项，用于动态生成 UI（主模式+子模式，已按 UI 顺序排序）</summary>
    public List<AncOption> AncOptions { get; set; } = new();

    // ========== 内嵌资源 ==========
    private static readonly List<JsonElement> _deviceModels = LoadDeviceModels();
    private static readonly Dictionary<string, string> _eqModeNames = LoadEqNameMap();

    /// <summary>id → 条目 索引（O(1) 精确查找，官方 productId 主键）。
    /// 同 id 多条时优先保留带 function 的能力条目；id 大小写不敏感。</summary>
    private static readonly Dictionary<string, JsonElement> _modelsById = BuildIdIndex(_deviceModels);

    private static Dictionary<string, JsonElement> BuildIdIndex(List<JsonElement> models)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in models)
        {
            if (!(e.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)) continue;
            var id = idEl.GetString();
            if (string.IsNullOrEmpty(id)) continue;
            bool hasFunc = e.TryGetProperty("function", out _);
            // 已存在且旧值带 function、新值不带 → 保留旧值；否则新值覆盖（能力条目优先）
            if (map.TryGetValue(id, out var prev) && prev.TryGetProperty("function", out _) && !hasFunc)
                continue;
            map[id] = e;
        }
        return map;
    }

    /// <summary>从嵌入资源加载设备 whitelist，按名称长度降序排列</summary>
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
            // 官方完整格式：根为对象，设备能力在 "whiteList" 数组里
            //（同级另有 diagnosisList/leAllFilterFunctions/versionCode 等，本机暂不使用）。
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

    /// <summary>从嵌入资源加载 EQ modeType → 名称映射</summary>
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

    /// <summary>根据蓝牙名称自动检测设备能力</summary>
    public static DeviceCapabilities Detect(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return Default();
        var norm = Normalize(deviceName);

        // 每轮都"优先带 function 的能力条目"，避免同名诊断条目（无 function）顶掉真正的能力条目。
        // 第1轮：规范化后完全相等
        if (MatchWithFunctionPref(e => { var nm = Normalize(EntryName(e) ?? ""); return nm.Length > 0 && nm == norm; },
                                  deviceName) is { } r1) return r1;

        // 第2轮：设备名包含型号名（型号名需 >=5 字符，避免 "air" 之类误配）
        if (MatchWithFunctionPref(e => { var nm = Normalize(EntryName(e) ?? ""); return nm.Length >= 5 && norm.Contains(nm); },
                                  deviceName) is { } r2) return r2;

        // 第3轮：型号名包含设备名（设备名需 >=5 字符）
        if (MatchWithFunctionPref(e => { var nm = Normalize(EntryName(e) ?? ""); return norm.Length >= 5 && nm.Contains(norm); },
                                  deviceName) is { } r3) return r3;

        // 未匹配：标记为未完整适配
        return new DeviceCapabilities { DeviceName = deviceName, ModelName = deviceName, IsSupported = false };
    }

    /// <summary>在 predicate 命中的条目里优先返回带 function 的能力条目；无能力条目时用第一个命中项兜底；都没有返回 null。</summary>
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

    /// <summary>按完整型号名手动覆盖检测</summary>
    public static DeviceCapabilities ForceModel(string modelName)
    {
        return MatchWithFunctionPref(
            e => string.Equals(modelName, EntryName(e), StringComparison.OrdinalIgnoreCase),
            modelName) ?? Default();
    }


    /// <summary>按 productId（官方主键，如 "06F010"）精确识别设备。找不到返回 null。
    /// 注意：数据源里同一 id 可能有多个条目（能力条目 + 诊断条目，后者无 function 块），
    /// 必须优先选带 function 的能力条目，否则会误取诊断条目导致界面无任何功能。</summary>
    public static DeviceCapabilities? DetectById(string productId, string? deviceName = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) return null;
        return _modelsById.TryGetValue(productId, out var entry)
            ? FromJson(entry, deviceName ?? EntryName(entry) ?? productId)
            : null;
    }

    /// <summary>数值字段 >=1（或布尔 true）视为"支持/开启"；缺失或 0 视为不支持。</summary>
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

    /// <summary>任一给定字段"存在且非 false/0"即视为支持（用于带 Compat/New 变体的能力）。</summary>
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
                case JsonValueKind.String: return true;   // 存在即算声明该能力
            }
        }
        return false;
    }

    /// <summary>安全读取条目 name（缺失返回 null）。</summary>
    private static string? EntryName(JsonElement entry) =>
        entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;

    /// <summary>获取所有已知设备型号名称列表</summary>
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

    /// <summary>从 whitelist JSON 条目解析设备能力</summary>
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
        // 命令层仅实现新版协议(1/2)且需支持 SPP，否则标记为未完整适配
        caps.IsSupported = caps.SupportSpp && caps.ProtocolType != 0;

        if (!entry.TryGetProperty("function", out var func)) return caps;

        // spatialTypes 存在 → 空间音效；长度 ≥3 → 空间音频三模式
        caps.HasSpatialSound = func.TryGetProperty("spatialTypes", out var st);
        if (st.ValueKind == JsonValueKind.Array)
        {
            int n = 0;
            foreach (var _ in st.EnumerateArray()) { n++; if (n >= 3) break; }
            if (n >= 3) caps.HasSpatialAudio = true;
        }

        // multiDevicesConnect ≥ 1 → 双设备连接
        if (func.TryGetProperty("multiDevicesConnect", out var mdc) && mdc.ValueKind == JsonValueKind.Number)
        {
            caps.MultiDevicesConnect = mdc.GetInt32();
            caps.HasDualDevice = caps.MultiDevicesConnect >= 1;
            caps.HasMultiConnectManage = caps.MultiDevicesConnect >= 2;  // 2=支持连接列表管理/切换活动设备
        }

        // ===== 扩展能力（对齐官方 whiteList.function 字段，供未来界面使用）=====
        // 约定：数值字段 >=1 视为支持；数组字段非空视为支持；带 Compat/New 变体的取"任一存在"。
        caps.HasHiResAudio         = FlagAnyPresent(func, "highAudio", "highToneQuality");
        caps.HasDolbyAtmos         = FlagOn(func, "dolbyAtmos");
        caps.HasCustomEq           = FlagOn(func, "customEqualizer");
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

        // 游戏模式：对齐官方 melody（common/util/W.g + W.f）。判据是【专用字段】gameMode / gameModeList，
        // 与 gameSoundList 无关。规则：
        //   gameModeList 非空 → 取（按固件版本筛选后的）条目的 gameMode 值；否则取顶层 gameMode。
        //   该值经 W.f() 判定：==1 支持、==0 或缺失 不支持。
        // 本机不做固件版本细分，简化为：gameModeList 里任一条目 gameMode==1，或顶层 gameMode==1，即支持。
        caps.HasGameMode = FunctionGameModeSupported(func);

        // 游戏音效：官方独立功能（setGameSoundTypeEnable 0x0423 + gameSoundList）。
        // 列表项如 [{type:3},{type:0}]：type 0 = 关闭/普通，非 0 = 具体游戏音效类型。取首个非 0 type 作为开启类型。
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

        // gameSoundMutexes → 游戏音效互斥组（与调音/空间音效等互斥，官方 GameSoundMutexHelper）
        if (func.TryGetProperty("gameSoundMutexes", out var gsm) && gsm.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in gsm.EnumerateArray())
                if (m.ValueKind == JsonValueKind.Number)
                    caps.GameSoundMutexes.Add(m.GetInt32());
        }

        // noiseReductionMode 有 childrenMode → 自适应降噪子模式
        if (func.TryGetProperty("noiseReductionMode", out var nrm))
        {
            foreach (var mode in nrm.EnumerateArray())
                if (mode.TryGetProperty("childrenMode", out _))
                    caps.HasAdaptiveAnc = true;

            BuildAncOptions(nrm, caps);

            // 无 childrenMode 且 modeType 5 在 protocolIndex 0 → 旧版 ANC 值交换
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

        // equalizerMode[].modeType → EqModeNames.json 查找显示名称
        var eqMap = new Dictionary<byte, string>();
        if (func.TryGetProperty("equalizerMode", out var eqModes))
        {
            foreach (var mode in eqModes.EnumerateArray())
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
        if (eqMap.Count == 0) eqMap[0] = "默认";
        ApplyEqNames(caps, eqMap);
        return caps;
    }

    private static DeviceCapabilities Default() => new() { ModelName = "Unknown" };

    /// <summary>
    /// 游戏模式支持判定，对齐官方 melody（common/util/W.g + W.f）：
    /// 只认专用字段 gameModeList / gameMode，与 gameSoundList 无关。
    /// W.f 语义：值 ==1 支持、==0 或缺失 不支持。gameModeList 存在则以其条目为准，否则回退顶层 gameMode。
    /// </summary>
    private static bool FunctionGameModeSupported(JsonElement func)
    {
        // gameModeList 非空：任一条目 gameMode==1 视为支持（本机不做固件版本细分）
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
            if (any) return false;  // 列表非空但无 ==1 条目 → 不支持
        }
        // 回退顶层 gameMode：==1 支持
        return func.TryGetProperty("gameMode", out var top) && top.ValueKind == JsonValueKind.Number
               && top.GetInt32() == 1;
    }

    /// <summary>写入 EQ preset 双向索引</summary>
    private static void ApplyEqNames(DeviceCapabilities caps, Dictionary<byte, string> names)
    {
        caps.EqPresets = new Dictionary<string, byte>();
        caps.EqNames = new Dictionary<byte, string>(names);
        foreach (var (k, v) in names) caps.EqPresets[v] = k;
    }

    // modeType 语义（官方 APK Lpa/k.b() + Lu9/a 权威映射）：
    //   1=关闭 2=通透 3=轻度 4=深度 5=降噪(容器) 6=自适应 7=智能 8=中度 10=自适应
    private static string ModeKey(int type) => type switch
    {
        1 => "Off",
        2 => "Transparency",
        3 => "Light",
        4 => "Deep",        // APK: melody_common_depth_noise_reduction_title = 深度降噪
        5 => "NC",          // 降噪容器（含子档位）或扁平"强降噪"
        6 => "Adaptive",
        7 => "Smart",       // APK: intelligent_title_v2 = 智能切换
        8 => "Medium",      // APK: melody_common_middle_noise_reduction_title = 中度降噪
        10 => "Adaptive",   // APK Lu9/a: modeType 10 → 自适应（Free4 系扁平主模式）
        _ => "Mode" + type
    };

    /// <summary>模式键 → UI 中文显示名</summary>
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

    /// <summary>UI 排序权重：降噪 → 通透 → 自适应 → 其它 → 关闭（关闭永远最后）</summary>
    private static int MainRank(string key) => key switch
    {
        "NC" => 0, "Smart" => 0,
        "Transparency" => 1,
        "Adaptive" => 2,
        "Off" => 99,
        _ => 50
    };

    /// <summary>
    /// 从 JSON noiseReductionMode 构建：
    ///   1) 层级化 AncOptions（主模式 + 子模式），供 UI 动态生成；
    ///   2) 扁平 AncIndexToName / AncNameToIndex，供 ParseAnc / SendAnc 使用。
    /// 完全按型号 JSON 推导，型号没有的模式不会出现。
    /// </summary>
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

            // 收集子模式
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

            // 单一自引用子模式（如 Off/Transparency 各含一个同类型子项）视为叶子：直接用子项索引发送
            if (childOpts.Count == 1 && childOpts[0].Key == key)
            {
                var only = childOpts[0];
                RegisterFlat(idxToName, nameToIdx, only.ProtocolIndex, key);
                options.Add(new AncOption { Key = key, Label = AncLabel(key), ProtocolIndex = only.ProtocolIndex, Sendable = true });
                continue;
            }

            if (childOpts.Count > 0)
            {
                // 真正的容器（多个子模式）：主项不可直接发送，点主项时发第一个子项
                options.Add(new AncOption { Key = key, Label = AncLabel(key), ProtocolIndex = ownIdx, Sendable = false, Children = childOpts });
            }
            else
            {
                // 扁平叶子项
                RegisterFlat(idxToName, nameToIdx, ownIdx, key);
                options.Add(new AncOption { Key = key, Label = AncLabel(key), ProtocolIndex = ownIdx, Sendable = true });
            }
        }

        // 主项排序 + 子项各自排序
        options.Sort((a, b) => MainRank(a.Key).CompareTo(MainRank(b.Key)));
        foreach (var o in options)
            o.Children.Sort((a, b) => MainRank(a.Key).CompareTo(MainRank(b.Key)));
        caps.AncOptions = options;
    }

    private static void RegisterFlat(Dictionary<byte, string> idxToName, Dictionary<string, byte> nameToIdx, byte idx, string name)
    {
        idxToName[idx] = name;
        // 名称→索引只记第一次，避免容器/子模式重名覆盖
        if (!nameToIdx.ContainsKey(name)) nameToIdx[name] = idx;
    }

    /// <summary>移除非字母数字字符并转小写，用于模糊匹配</summary>
    private static string Normalize(string name) =>
        new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

}
