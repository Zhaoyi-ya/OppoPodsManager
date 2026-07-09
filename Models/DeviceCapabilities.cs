using System;
using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>
/// 一个可选的 ANC 模式项（用于按 JSON 动态生成降噪 UI）。
/// 主模式可含若干子模式（如"降噪"下的智能/中度/超级/轻度）；无子模式则直接可发送。
/// </summary>
public sealed class AncOption
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public byte ProtocolIndex { get; set; }
    public bool Sendable { get; set; } = true;
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
    public int ProtocolType { get; set; } = 1;
    public bool SupportSpp { get; set; } = true;
    public bool IsSupported { get; set; } = true;

    // ========== 功能标志（当前 UI 已用）==========
    public bool HasSpatialAudio { get; set; }
    public bool HasSpatialSound { get; set; }
    public bool HasDualDevice { get; set; }
    public bool HasAdaptiveAnc { get; set; }
    public bool IsLegacyAnc { get; set; }
    public bool HasGameMode { get; set; }
    public bool HasGameSound { get; set; }
    public byte GameSoundType { get; set; }

    // ========== 扩展功能标志（whiteList.function）==========
    public bool HasHiResAudio { get; set; }
    public bool HasDolbyAtmos { get; set; }
    public bool HasCustomEq { get; set; }
    public bool HasHearingEnhancement { get; set; }
    public bool HasPersonalNoise { get; set; }
    public bool HasWearDetection { get; set; }
    public bool HasAutoSwitchLink { get; set; }
    public bool HasFindDevice { get; set; }
    public bool HasClickTakePic { get; set; }
    public bool HasZenMode { get; set; }
    public bool HasEarScan { get; set; }
    public bool HasBassEngine { get; set; }
    public bool HasCustomDress { get; set; }
    public bool HasFitDetection { get; set; }
    public bool HasVocalEnhance { get; set; }
    public bool HasLongPowerMode { get; set; }
    public bool HasVoiceCommand { get; set; }
    public bool HasSpeechPerception { get; set; }
    public bool HasSleepDetection { get; set; }
    public bool HasHeadMotion { get; set; }
    public bool HasAiTranslate { get; set; }
    public bool HasAiSummary { get; set; }
    public bool HasMeetingAssistant { get; set; }
    public bool HasWhiteNoise { get; set; }
    public bool HasSpineHealth { get; set; }
    public bool HasDiagnostic { get; set; }
    public bool HasFirmwareUpdate { get; set; }
    public bool HasPromptVolume { get; set; }
    public bool HasMultiConnectManage { get; set; }
    public int  MultiDevicesConnect { get; set; }

    public HashSet<int> GameSoundMutexes { get; set; } = new();
    public bool GameSoundMutexEq => GameSoundMutexes.Contains(1) || GameSoundMutexes.Contains(3);
    public bool GameSoundMutexSpatial => GameSoundMutexes.Contains(2);

    /// <summary>默认 EQ 预设名（关闭/无效果），用于互斥时回退。取 EqPresets 中最小 protocolIndex 对应的名称；无预设时返回 "默认"。</summary>
    public string DefaultEqPreset
    {
        get
        {
            byte min = byte.MaxValue;
            string name = "默认";
            foreach (var kv in EqPresets)
                if (kv.Value < min) { min = kv.Value; name = kv.Key; }
            return name;
        }
    }

    // ========== EQ 预设 ==========
    public Dictionary<string, byte> EqPresets { get; set; } = new();
    public Dictionary<byte, string> EqNames { get; set; } = new();

    /// <summary>自定义 EQ 频率数组（如 [31, 62, 125, …]），空数组表示无自定义 EQ。</summary>
    public int[] CustomEqFrequencies { get; set; } = Array.Empty<int>();

    /// <summary>设备侧可存储的自定义 EQ 预设数上限（customEqMax）。0 表示不支持。</summary>
    public int CustomEqMaxPresets { get; set; }

    /// <summary>自定义 EQ UI 版本（customEqUiVersion）。2 为当前主流。</summary>
    public int CustomEqUiVersion { get; set; }

    /// <summary>设备端 EQ 预设名称覆盖（eqId → 名称），按 0x8122 响应合并后更新。</summary>
    internal Dictionary<byte, string> DeviceEqNames { get; set; } = new();

    /// <summary>protocolIndex → UI 模式名（按型号 noiseReductionMode 动态构建）</summary>
    public Dictionary<byte, string> AncIndexToName { get; set; } = new();

    /// <summary>UI 模式名 → protocolIndex（发送 ANC 时按型号取正确字节位）</summary>
    public Dictionary<string, byte> AncNameToIndex { get; set; } = new();

    /// <summary>按 JSON noiseReductionMode 构建的层级化 ANC 选项。</summary>
    public List<AncOption> AncOptions { get; set; } = new();

    // ========== 静态转发（委托给 DeviceProfileLoader）==========

    /// <summary>根据蓝牙名称自动检测设备能力。</summary>
    public static DeviceCapabilities Detect(string? deviceName) =>
        DeviceProfileLoader.Detect(deviceName);

    /// <summary>按完整型号名手动覆盖检测。</summary>
    public static DeviceCapabilities ForceModel(string modelName) =>
        DeviceProfileLoader.ForceModel(modelName);

    /// <summary>按 productId 精确识别设备。</summary>
    public static DeviceCapabilities? DetectById(string productId, string? deviceName = null) =>
        DeviceProfileLoader.DetectById(productId, deviceName);

    /// <summary>获取所有已知设备型号名称列表。</summary>
    public static List<string> GetModelNames() =>
        DeviceProfileLoader.GetModelNames();
}
