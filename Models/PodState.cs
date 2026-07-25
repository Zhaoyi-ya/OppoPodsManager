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
    /// <summary>低音引擎开关（0x0403 FeatureBassEngine）。</summary>
    public bool BassEngine { get; set; }
    /// <summary>人声增强开关（0x0403 FeatureVocalEnhance）。</summary>
    public bool VocalEnhance { get; set; }
    /// <summary>听力增强开关（0x0403 FeatureHearingEnhance）。</summary>
    public bool HearingEnhance { get; set; }
    /// <summary>长续航模式开关（0x0403 FeatureLongPowerMode）。</summary>
    public bool LongPowerMode { get; set; }
    /// <summary>佩戴检测开关（0x0403 FeatureWearDetection）。</summary>
    public bool WearDetection { get; set; }
    /// <summary>脊柱健康开关（0x0403 FeatureSpineLiveMonitor）。</summary>
    public bool SpineHealth { get; set; }

    /// <summary>多设备连接列表（由主动轮询同步）。</summary>
    public List<ConnectedDeviceInfo> ConnectedDevices { get; set; } = new();

    /// <summary>多设备列表最近更新时间。</summary>
    public DateTime MultiConnectListUpdatedAt { get; set; } = DateTime.MinValue;

    /// <summary>设备端 EQ 预设列表（由 0x8122 getAllEqInfo 响应填充）。</summary>
    public List<EqInfoEntry> DeviceEqEntries { get; set; } = new();

    // ===== Multi-connect priority / connection strategy =====

    // AutoSwitchLink removed (2026-07-23):
    // JADX U8.s.h defaults to true, and o9.e.isAutoSwitchLinkOpened() reads from
    // FeatureSwitchInfo feature 17 (multi-connect enable, always ON for Air5 Pro) +
    // SharedPreferences + account validation. The device has no readback path for
    // AutoSwitchLink on SPP — 0x8200 doesn't include event 0x0D, 0x011C returns a
    // capability bitmap (flags=0x0007, bit3=0), and 0x0132 is priority-device mode.
    // The 0x0413 write path works but without a read path the UI cannot stay in sync.

    /// <summary>
    /// Whether priority selection is automatic (JADX HandheldDeviceInfo.mIsAutoMode).
    /// true = no fixed priority device; false = fixed address in <see cref="PriorityDeviceAddress"/>.
    /// </summary>
    public bool MultiConnectAutoMode { get; set; }

    /// <summary>
    /// 手动模式下用户指定的优先设备 MAC（"AA:BB:CC:DD:EE:FF"）；自动模式或未知为空。
    /// 该设备进入可连接范围时，耳机会优先自动连接它。
    /// </summary>
    public string PriorityDeviceAddress { get; set; } = "";
}
