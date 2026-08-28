namespace OppoPodsManager.Control.Brands.Huawei;

// 华为（Huawei）TWS / 耳机的私有协议常量。
//
// 协议来源：HuaweiPods（开源，https://github.com/Nshpiter/HuaweiPods，Kotlin Xposed 模块；
//           注：Xposed 模块 ID 为 moe.chenxy.huaweipods，勿与 GitHub 用户名混淆）
// 与 TWS-Pods-PC（Python 逆向工具）中已实机抓包确认的华为 RFCOMM 指令。
//
// 帧格式（无分包，单帧收发）：
//   [5A 00][len_lo][len_hi][service][command][TLV...][crc_hi][crc_lo]
//   - len（uint16 LE）= 载荷长度，从 command 起算（command + TLV + CRC），即 TLV 长度 + 3
//   - service/command 构成命令分组，可编码为 ushort（service << 8 | command）映射 IFrameCodec
//   - TLV 序列：[type(1)][len(1)][value(len)...]
//   - CRC16/XMODEM（poly=0x1021, init=0x0000），覆盖从 0x5A 到 TLV 末尾，大端追加 2 字节
//
// 有回包命令（电量 C08、ANC 状态 C2A、佩戴 C11、手势状态 C20/C26/C1F/C17）走 ConnectionLink.RequestAsync；
// fire-and-forget 命令（ANC 开关 C04、FB3 档位 C08、佩戴 C10、手势写 C1F/C25/C1E/C16）无通用 ACK，
// 用 SendFireAndForgetAsync + 乐观更新 + 周期轮询回读确认。
//
// 许可证提示：HuaweiPods 为 GPL-3.0，正式发布前需确认 next 项目的许可证兼容。
public static class HuaweiConstants
{
    // 华为使用标准 SPP 服务 UUID（WindowsRfcommConnection 已支持）。
    public static readonly Guid HuaweiSppServiceId = new("00001101-0000-1000-8000-00805F9B34FB");

    // 帧头魔数与头长（魔数 2 + len 2 + service 1）。
    public const byte MagicHigh = 0x5A;
    public const byte MagicLow = 0x00;
    public const int HeaderSize = 5;
    public const int CrcSize = 2;

    // ---- 命令分组（service << 8 | command）----
    // 电量查询：S01 C08，响应同命令（TLV 0x02=电量[左,右,盒]，0x03=充电位，0x05=佩戴状态）。
    public const ushort QueryBattery = 0x0108;
    public const ushort ReportBattery = 0x0108;
    // 备用电量报告：参考 BATTERY_COMMANDS={0x08,0x27}，部分型号经此主动推送电量。
    public const ushort ReportBatteryAlt = 0x0127;

    // 设备信息查询：S01 C07（响应用于确认身份，首版未订阅）。
    public const ushort QueryDeviceInfo = 0x0107;

    // ANC 状态查询：S2B C2A，响应同命令（TLV 0x01 两字节 [子模式, 主模式]）。
    public const ushort QueryAncState = 0x2B2A;
    public const ushort ReportAncState = 0x2B2A;

    // ANC 开关/模式设置：S2B C04（fire-and-forget，TLV 0x01 两字节 [主模式, 子模式]）。
    public const ushort SetAncMode = 0x2B04;

    // FreeBuds 3 智能降噪档位：S2B C08（fire-and-forget，TLV 0x01 单字节 0-8 级）。
    // 启用降噪时附带下发，使 FB3 方向感档位（SupportsAncDirectionDial）真正生效。
    public const ushort SetAncDirectionLevel = 0x2B08;

    // 佩戴检测：写 S2B C10（fire-and-forget）/ 查 S2B C11（响应同命令，TLV 0x01 单字节 0/1）。
    public const ushort SetWearDetection = 0x2B10;
    public const ushort QueryWearDetection = 0x2B11;
    public const ushort ReportWearDetection = 0x2B11;

    // 双击：写 S01 C1F（fire-and-forget）/ 查 S01 C20（TLV 0x01=左 0x02=右）。
    public const ushort SetDoubleTap = 0x011F;
    public const ushort QueryDoubleTapState = 0x0120;
    public const ushort ReportDoubleTapState = 0x0120;

    // 三击：写 S01 C25 / 查 S01 C26。
    public const ushort SetTripleTap = 0x0125;
    public const ushort QueryTripleTapState = 0x0126;
    public const ushort ReportTripleTapState = 0x0126;

    // 滑动：写 S2B C1E / 查 S2B C1F。
    public const ushort SetSwipe = 0x2B1E;
    public const ushort QuerySwipeState = 0x2B1F;
    public const ushort ReportSwipeState = 0x2B1F;

    // 长按：写 S2B C16 / 查 S2B C17。
    public const ushort SetLongPress = 0x2B16;
    public const ushort QueryLongPressState = 0x2B17;
    public const ushort ReportLongPressState = 0x2B17;

    // 按捏（pinch）功能切换：写 S2B C92（fire-and-forget）。
    // 来源：HuaweiGestureController.buildFreeBudsPro3GestureTogglePacket（modernPinchRoutes = Pro3 / Pro5）。
    // 负载为四段 TLV：(0x01,[0x01]) + (slot,[0x01,context]) + (0x03,[action]) + (0x04,[action])，
    // slot/context/action 由具体按捏功能（接听/拒接/播放暂停/上一曲/下一曲）决定。
    public const ushort SetPinchToggle = 0x2B92;

    // 均衡器（EQ）：读 S2B C4A / 写 S2B C49（fire-and-forget）。
    // 来源：OpenFreebuds config_equalizer.py（CMD_EQ_READ=0x2b4a / CMD_EQ_WRITE=0x2b49）。
    // 内置预设写负载 TLV (1, presetId)；读响应 param2=当前预设 ID、param3=可用预设列表。
    public const ushort QueryEqualizer = 0x2B4A;
    public const ushort SetEqualizer = 0x2B49;

    // 低延迟/游戏模式：S2B C6C（读 param2=1/0；写 TLV (1, 0x01/0x00)）。
    // 来源：OpenFreebuds low_latency.py（CMD_LOW_LATENCY=0x2b6c）。
    public const ushort QueryLowLatency = 0x2B6C;
    public const ushort SetLowLatency = 0x2B6C;

    // 双设备（dual-connect）：使能读/写、枚举、首选写、执行、变更事件。
    // 来源：OpenFreebuds dual_connect（CMD_DUAL_CONNECT_*）。
    public const ushort QueryDualConnectEnabled = 0x2B2F;
    public const ushort SetDualConnectEnabled = 0x2B2E;
    public const ushort EnumerateDualConnect = 0x2B31;
    public const ushort SetDualConnectPreferred = 0x2B32;
    public const ushort ExecuteDualConnect = 0x2B33;
    public const ushort DualConnectChangeEvent = 0x2B36;

    // ---- 语音语言（service language）----
    // 读 S0C C02（响应 TLV 0x03=UTF8 语言列表）、写 S0C C01（TLV 0x01=UTF8 语言码，0x02=1）。
    // 来源：OpenFreebuds service_language.py（read=0x0c02 / write=0x0c01）。
    public const ushort QueryVoiceLanguage = 0x0C02;
    public const ushort SetVoiceLanguage = 0x0C01;

    // ---- 音质偏好（sound quality preference）----
    // 读 S2B CA3（响应 TLV 0x01/0x02 单字节）、写 S2B CA2（TLV 0x01：0=连接优先，1=音质优先）。
    // 来源：OpenFreebuds sound_quality_preference.py（read=0x2ba3 / write=0x2ba2）。
    public const ushort QuerySoundQuality = 0x2BA3;
    public const ushort SetSoundQuality = 0x2BA2;
    public const byte SoundQualityConnectivity = 0x00;
    public const byte SoundQualityQuality = 0x01;

    // ---- 佩戴状态主动上报（in-ear state）----
    // 耳机端 in-ear 检测的实时通知（TLV 0x08/0x09 单字节，1=入耳）。
    // 来源：OpenFreebuds state_in_ear.py（commands=[0x2b03]）。
    public const ushort InEarStateNotify = 0x2B03;

    // ---- 双设备执行命令（OfbHuaweiDualConnCommand）----
    public const byte DualConnectConnect = 1;
    public const byte DualConnectDisconnect = 2;
    public const byte DualConnectUnpair = 3;
    public const byte DualConnectEnableAuto = 4;
    public const byte DualConnectDisableAuto = 5;

    // ---- TLV 类型 ----
    public const byte TlvBatteryLevels = 0x02;
    public const byte TlvChargingStates = 0x03;
    public const byte TlvReportedAvailability = 0x05;
    public const byte TlvAncState = 0x01;
    public const byte TlvLeftGesture = 0x01;
    public const byte TlvRightGesture = 0x02;

    // ---- ANC 主模式字节（TLV 0x01 两字节的 value[0]，与回读 C2A 的 [子,主] 低位一致）----
    public const byte AncModeOff = 0x00;
    public const byte AncModeNoiseCancellation = 0x01;
    public const byte AncModeTransparency = 0x02;

    // ---- ANC 子模式默认值 ----
    // 无离散档位型号的 NC 默认子模式（等同 modernFreeBudsEnabled[true] 的 01 FF）。
    public const byte AncSubModeDefault = 0xFF;
    // FreeBuds 6i 通透默认子模式 0x02，其余型号 0xFF。
    public const byte TransparencyDefault6i = 0x02;

    // ---- 手势动作值（modern 型号；FreeBuds 3 双击有独立映射）----
    public const byte GestureNone = 0xFF;
    public const byte GestureVoiceAssistant = 0x00;
    public const byte GesturePlayPause = 0x01;
    public const byte GestureNext = 0x02;
    public const byte GesturePrevious = 0x07;
    public const byte GestureNoiseControl = 0x0A;
    // FreeBuds 3 双击动作值（HuaweiGestureAction 旧枚举，勿复用于其他型号）。
    public const byte GestureFb3PlayNext = 0x04;
    public const byte GestureFb3NoiseCancellation = 0x03;
    // FreeBuds 3i 双击动作值（FreeBuddy _FB3iDoubleTap：voice=0/playPause=1/next=4/previous=8/nothing=255，
    // 位掩码风格，与 modern 型号 next=2/previous=7 不同，勿混用）。
    public const byte Gesture3iNext = 0x04;
    public const byte Gesture3iPrevious = 0x08;

    // ---- 滑动动作值（SwipeAction）----
    public const byte SwipeVolumeControl = 0x00;
    public const byte SwipeTrackControl = 0x01;
}
