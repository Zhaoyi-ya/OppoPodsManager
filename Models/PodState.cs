using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace OppoPodsManager;

public class PodState
{
    // 后台线程写、UI 线程读，用并发字典避免竞态崩溃
    public ConcurrentDictionary<string, (int Level, bool Charging)?> Battery { get; } = new();
    /// <summary>当前 ANC 模式键（如 "NoiseReduction"/"Transparency"）；未知为 "?"。</summary>
    public string AncMode { get; set; } = "?";
    /// <summary>智能切换模式下设备实时计算出的当前档位名（如"深度"）；非智能模式为空。</summary>
    public string IntelligentRealtime { get; set; } = "";
    /// <summary>当前 EQ 预设名（如 "ClearVoice"）；未知为 "?"。</summary>
    public string EqPreset { get; set; } = "?";
    /// <summary>远程固件版本（0x8105 响应）；未知为空。</summary>
    public string FirmwareVersion { get; set; } = "";
    /// <summary>当前音频编解码器 id（0x8114 响应）；未知为 -1。</summary>
    public int CodecType { get; set; } = -1;
    /// <summary>左耳佩戴状态（如 "未佩戴"/"入盒"/"已佩戴"）。</summary>
    public string WearingL { get; set; } = "";
    /// <summary>右耳佩戴状态。</summary>
    public string WearingR { get; set; } = "";
    /// <summary>是否已建立 SPP/BLE 连接。</summary>
    public bool Connected { get; set; }
    /// <summary>设备通过 0x8100 能力位图声明的命令集合。</summary>
    public HashSet<ushort> SupportedCommands { get; set; } = new();
    /// <summary>空间音效开关（0x0403 FeatureSpatialSound）。</summary>
    public bool SpatialSound { get; set; }
    /// <summary>空间音频三模式（Off/Fixed/Track）。</summary>
    public string SpatialMode { get; set; } = "Off";
    /// <summary>游戏模式开关（0x0403 FeatureGameMode）。</summary>
    public bool GameMode { get; set; }
    /// <summary>游戏音效开关（0x0423）。</summary>
    public bool GameSound { get; set; }
    /// <summary>双设备连接开关（0x0403 FeatureDualDevice）。</summary>
    public bool DualDevice { get; set; }

    /// <summary>多设备连接列表（由主动轮询同步）。</summary>
    public List<ConnectedDeviceInfo> ConnectedDevices { get; set; } = new();

    /// <summary>多设备列表最近更新时间。</summary>
    public DateTime MultiConnectListUpdatedAt { get; set; } = DateTime.MinValue;

    /// <summary>设备端 EQ 预设列表（由 0x8122 getAllEqInfo 响应填充）。</summary>
    public List<EqInfoEntry> DeviceEqEntries { get; set; } = new();

    // ===== 多设备优先/自动切换（0x8132 getMultiConnectPriorityDevice 回读）=====

    /// <summary>
    /// 是否处于"自动切换"模式（对应 HandheldDeviceInfo.mIsAutoMode）。
    /// true = 耳机自动决定音频输出设备；false = 用户手动指定优先设备（见 <see cref="PriorityDeviceAddress"/>）。
    /// </summary>
    public bool MultiConnectAutoMode { get; set; }

    /// <summary>
    /// 手动模式下用户指定的优先设备 MAC（"AA:BB:CC:DD:EE:FF"）；自动模式或未知为空。
    /// 对应设备切换时优先连接/输出音频的那台。
    /// </summary>
    public string PriorityDeviceAddress { get; set; } = "";
}
