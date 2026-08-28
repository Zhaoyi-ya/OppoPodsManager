
using OppoPodsManager.Control.Core.Models;
namespace OppoPodsManager.Control.Brands.Vivo;
// vivo TWS GAIA 协议常量。
//
// ⚠ 真理之源：本文件命令码以今天（2026-08-09）用官方 App HCI 抓包逐字节验证过的
//    D:\System\Downloads\vivo_gui.py 为准（控制变量法：发已知帧→耳机状态变→看官方 App 变化）。
//    参考工程 OPPO-Pods-For-Windows 的旧 Vivo 常量未经验证，其噪声/查找/佩戴命令码均错，已据实测更正。
//
// 帧格式：FF [ver] [flags=00] [len] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload...]
    // GAIA 版本是「按命令」而非「按型号」——真机抓包已实锤：
    //   - 握手 0x0300：GAIA 厂商(0x000A) + version 4（版本协商帧，恒 v4）
    //   - 电量 0x0207：VIVO 厂商(0x001B) + version 4（TWS4 与 Air3 Pro 真机抓包均为 v4；
    //       旧实现按型号版本发会导致旧型号电量查询被耳机忽略）
    //   - 其余命令（噪声 0x0130、低延迟 0x0151、空间音频 0x0139、EQ 0x0118、双击 0x0102、
    //       长按 0x0131、查找 0x0120、双连 0x0249/0x014A…）：VIVO 厂商(0x001B) + 型号画像
    //       GAIA 版本(_noiseVersion)，随代际 v4(TWS4 系) / v3(Air3 Pro、TWS3e)。
    //   噪声模式切换 0x0130 用 v4（官方 App HCI 抓包逐字节实锤；用 v3 会被耳机忽略，
    //   表现为「软件内无法切换降噪模式但耳机切软件能跟」）。
//   report 帧 = query/set 帧 | 0x8000（与 GAIA 约定一致）。
internal static class VivoConstants
{
    // vivo 私有 RFCOMM 服务 UUID（与 Kotlin VivoEarbudAdapter.VIVO_GAIA_UUID 一致）。
    public static readonly Guid VivoServiceId = new("00000837-D102-11E1-9B23-00025B00A5A5");
    public const byte Preamble = 0xFF;
    public const ushort GaiaVendor = 0x000A;
    public const ushort VivoVendor = 0x001B;
    // 所有控制帧统一使用的 GAIA 版本（TWS 3e 实测：除握手/注册通知外均为 v3）。
    public const int ControlVersion = 3;
    // 握手与注册通知链使用的 GAIA 版本。
    public const int HandshakeVersion = 4;
    public const ushort Handshake = 0x0300;
    public const ushort HandshakeResponse = 0x8300;
    // ---- 注册通知链（实测：连接后依次发空载荷帧 0x0202~0x0206，耳机回 0x8202~0x8206 即开始主动推送）----
    // 注意：0x0202/0x8202 与「双击手势配置同步」复用同一命令字；0x0203/0x8203 与「佩戴检测」复用同一命令字。
    // 解析时按 payload 形态区分（空/短帧=注册 ACK，[00 L R]=双击配置，[.. state]=佩戴）。
    public const ushort RegisterNotificationsStart  = 0x0202;
    public const ushort RegisterNotificationsQuery  = 0x0203;
    public const ushort RegisterNotificationsEnable = 0x0204;
    public const ushort RegisterNotification        = 0x0205;
    public const ushort RegisterNotificationsEnd    = 0x0206;
    public const ushort RegisterNotificationAck     = 0x8205;
    // ---- 电量 ----
    public const ushort QueryBattery = 0x0207;
    public const ushort ReportBattery = 0x8207;   // [00][左%][右%][仓%][充电位]
    // ---- 噪声控制（APK 逆向实锤：set_noise_mode = 0x0130，真正切「当前出声模式」的命令）----
    // 证据链（EarbudSettingsFetcher.java 命令映射 / C7738b.java:m37736o 帧构造器 /
    //   C4258d.java:m20928W receiveNoiseModelState 回读 / 真机手动切换推送 0x8230）：
    //   SET   0x0130 payload = [mode, 0x03, 0x01]（3 字节；第二、三字节为固定后缀，非降噪档位；
    //         官方 App HCI 抓包逐字节实锤：降噪开 00 03 01 / 降噪关 01 03 01 / 通透 02 03 01；GAIA v4）
    //   QUERY 0x0230 payload = 空（GAIA 查询默认空载荷，m37736o 对 0x0230 无特例→空字节）
    //   REPORT/Ack 0x8230 / 0x8130 payload = [状态][mode][reduceModel][transparent?]
    //     · 长度==2（SET ack 回声）：mode=payload[0]，reduceModel=payload[1]
    //     · 长度>=3（查询响应/主动上报）：mode=payload[1]，reduceModel=payload[2]
    //   模式字节语义（APK 逆向全链路实锤，全 vivo 型号统一）：0=降噪(NC) / 1=关闭(Off) / 2=通透(Transparency)
    //   （UI 控件 m23148v2 的 int 参数 0/1/2/4 是 UI 主模式标签，被原样作为 set_noise_mode 首字节发出，APK 内无按型号重映射）
    //   降噪档位 reduceModel（真机推送实锤）：降噪(NC)→0x04，通透(Transparency)→0x01
    // 重要更正：旧工程把噪声命令族整体编为 0x0131/0x0231/0x8231，且误以为 0x010C(set_anc_mode) 才切出声模式；
    //   真机证明 0x010C 仅改 ancModeConfig、不切模式（ACK 但不出声），真正生效的是 0x0130 系。
    public const ushort SetNoiseMode = 0x0130;
    public const ushort QueryNoiseMode = 0x0230;
    public const ushort AckNoiseMode = 0x8130;
    public const ushort ReportNoiseMode = 0x8230;
    // set_noise_mode(0x0130) 第二字节 reduceModel 档位（真机手动切换推送 0x8230 实锤：NC=000104 / Trans=000201）。
    public const byte NoiseReduceNcDefault     = 0x04; // 降噪档位（NC）
    public const byte NoiseReduceTransDefault  = 0x01; // 通透档位（Transparency）
    // 当前降噪模式字节（set_noise_mode 0x0130 payload[0]），即 APK 权威值：0=降噪(NC) / 1=关闭(Off) / 2=通透(Trans)。
    // 全 vivo 型号统一（APK 逆向证实无按型号重映射）；如需按型号覆盖，请改 VivoNoiseModeMap 而非此处标量常量。
    public const byte NoiseModeOff         = 0x01; // 关闭（canonical）
    public const byte NoiseModeAnc         = 0x00; // 降噪（canonical）
    public const byte NoiseModeTransparency = 0x02; // 通透（canonical）
    // 兼容别名：历史代码用 NoiseOff/NoiseAnc/NoiseTransparency 作模式字节，现对齐官方 0/1/2。
    // （MapTo/MapFromVivoMode、NoiseOptionModel、SetNoiseCancellationProtocolAsync、VivoModelCatalog 均复用此名）
    public const byte NoiseOff          = NoiseModeOff;
    public const byte NoiseAnc          = NoiseModeAnc;
    public const byte NoiseTransparency = NoiseModeTransparency;
    // ---- 0x010C 系（set_anc_mode）：仅改 ancModeConfig 配置项，不切当前出声模式（仅供诊断/对照）----
    // APK 逆向实锤（C7738b.java:m37736o 帧构造器对 268 走单字节特例；C4258d.java:m20910F 取 bArr→ancModeConfig
    //   并广播 ANC_CHANGED；EarbudSettingsFetcher.java:receiveAncStateACK 路由到 ancModeConfig 更新）：
    //   SET   0x010C payload = [mode]（单字节；mode 0=关闭 / 1=降噪 / 2=通透）
    //   Ack   可能回 0x010C 自身（APK 路由表 268→receiveAncStateACK）或 SET|0x8000 = 0x810C
    //   主动上报可能 0x820C；查询 0x020C
    // ⚠️ 真机实锤：App 点选发 0x010C 并收 0x810C ACK（success=True），耳机实际不出声切换；
    //   末尾 0x8230 主动推送帧是用户在耳机上手动切的。故 0x010C 不能用于切当前出声模式。
    public const ushort SetAncMode   = 0x010C;
    public const ushort AckAncMode   = 0x810C; // SET|0x8000 约定（设备若回 0x010C 本身，路由订阅已覆盖）
    public const ushort QueryAncMode = 0x020C;
    public const ushort ReportAncMode = 0x820C;
    // 当前生效模式切换所用命令 = set_noise_mode(0x0130)。真机已验证 0x010C 无效，切模式必须走 0x0130 系。
    public const ushort ActiveNoiseSetCommand   = SetNoiseMode;   // 0x0130
    public const ushort ActiveNoiseAckCommand   = AckNoiseMode;   // 0x8130
    public const ushort ActiveNoiseQueryCommand = QueryNoiseMode; // 0x0230
    // （已废弃）旧误判的「长按循环集合」位掩码，保留仅供过渡参考，勿再用于当前模式切换。
    //   0x0b=全选 / 0x0a={通透,降噪} / 0x08={关闭,降噪} / 0x09={关闭,通透} / 0xff=无
    public const byte NoiseCycleAll          = 0x0b;
    public const byte NoiseCycleExcludeOff   = 0x0a;
    public const byte NoiseCycleExcludeTrans = 0x08;
    public const byte NoiseCycleExcludeAnc   = 0x09;
    public const byte NoiseCycleNone         = 0xff;
    // ---- 双击手势 ----
    // SET   0x0102 payload = [动作码]（单字节；耳机按数值范围判左右：0x00~0x06=左耳，0x10~0x16=右耳）
    // QUERY 0x0202 payload = 空（与注册通知-开始共用命令字）
    // REPORT 0x8202 payload = [00][左动作码][右动作码]（改设置时上报，非触发事件）
    public const ushort SetDoubleTap = 0x0102;
    public const ushort QueryDoubleTap = 0x0202;
    public const ushort AckDoubleTap = 0x8102;
    public const ushort ReportDoubleTapConfig = 0x8202;
    // ---- 长按手势功能（左右耳下拉仅 无 / 切换噪声控制）----
    // ⚠️ 命令字已据官方 App 反编译注册表（EarbudSettingsFetcher.fetchEarbudsSettingsFromCommand）更正：
    //    set_long_press = 305 = 0x0131（旧工程误用 0x0150，那是 set_touch_operation_button）。
    //    SET 0x0131 / QUERY 0x0231 / ACK 0x8131 / REPORT 0x8231。
    //    同步更正查询/ACK/上报命令字（0x0250/0x8150/0x8250 → 0x0231/0x8131/0x8231）。
    //    注：0x0231/0x8231 曾被 vivo-Protocol.txt 抓包误标为"查找"，实为长按查询/上报（注册表实锤）。
    // SET   0x0131 payload = [05][功能码]（APK m37720W(5, a, b) 构造）
    // QUERY 0x0231 payload = 空
    // ACK/Report 0x8131 / 0x8231 payload 待 payload 级核对
    public const ushort SetLongPressFunc = 0x0131;
    public const ushort QueryLongPressFunc = 0x0231;
    public const ushort AckLongPressFunc = 0x8131;
    public const ushort ReportLongPressFunc = 0x8231;
    // ---- 通话操作（set_touch_operation_button，ScrewVivoTWS AcceptCallMaker 同款；与双击手势 0x0102 互补）----
    // 这是「来电时」的独立触控开关（双击=接听/挂断、长按=拒接），与「双击/长按手势做什么」是两套功能。
    // SET 0x0150 payload = [0x03, mode]（mode 位域：bit1(0x02)=双击接听/挂断来电，bit0(0x01)=长按拒接来电）。
    // 仅 SET 实锤（ScrewVivoTWS 写命令）；QUERY/REPORT 未确认，按 GAIA 约定暂留占位。
    public const ushort SetTouchOperationButton = 0x0150;
    public const ushort AckTouchOperationButton  = 0x8150;
    public const byte CallOpPrefix          = 0x03; // payload 首字节固定前缀
    public const byte CallOpDoubleTapAnswer = 0x02; // bit1：双击接听/挂断
    public const byte CallOpLongPressReject = 0x01; // bit0：长按拒接
    // ---- 查找耳机（两耳同时响铃）----
    // SET   0x0120 payload = [01] 启动 / [00] 关闭
    // ACK   0x8120 payload = [00 01] 响铃中 / [00 00] 关闭
    public const ushort SetFindDevice = 0x0120;
    public const ushort AckFindDevice = 0x8120;
    // ---- 佩戴检测 / 实时佩戴状态 ----
    // 注意：vivo 有两套完全不同的"佩戴"帧，早前把它们混为一谈导致佩戴状态不刷新：
    //  • 佩戴检测开关：SET 0x0103 / QUERY 0x0203 / REPORT 0x8203，payload [..][state]，state 0=关 1=开
    //    （即 APP 里"佩戴检测"功能的总开关，仅连接时/改设置时上报一次，不随取放变化）。
    //  • 实时佩戴状态：QUERY 0x020D / REPORT 0x820D，payload [status:0][flags]。
    //    flags 位域（官方 AbstractC7500c 全 APK 唯一"在耳"判定，已逐位核对）：
    //      bit0(0x01)=左耳在盒、bit1(0x02)=右耳在盒、
    //      bit2(0x04)=左耳佩戴(Worn)、bit3(0x08)=右耳佩戴(Worn)。
    //    每耳占用 2 位，不会同时在盒与佩戴；两位皆 0 → 摘下(Removed)。
    //    UpgradeActivity 亦用 bit0&bit1 判定"双耳都在盒"，与上方一致。
    //    耳机取放时主动推送，是 UI 佩戴状态真正的实时来源（无需交叉电量充电位）。
    public const ushort SetWearDetection = 0x0103;
    public const ushort AckWearDetection = 0x8103;
    public const ushort QueryWearDetection = 0x0203;
    public const ushort ReportWearDetection = 0x8203;
        public const ushort QueryWearState = 0x020D;
        public const ushort ReportWearState = 0x820D;
    // ---- 听力保护（set_hearing_protection_switch / get_hearing_protection_switch，APK 逆向实锤 logical 338/594 = 0x0152/0x0252）----
    // SET 0x0152 payload = [enable:1]；QUERY 0x0252；ACK 0x8152；REPORT 0x8252 payload = [00][state]（state 0=关 1=开）。
    // 与佩戴检测同属开关型基础功能，但能力由 DeviceModels.json 的 hearing_protection 精确声明（49 机型支持）。
    public const ushort SetHearingProtection    = 0x0152;
    public const ushort QueryHearingProtection  = 0x0252;
    public const ushort AckHearingProtection    = 0x8152;
    public const ushort ReportHearingProtection = 0x8252;
    // ---- 音效 / EQ 预设 ----
    // SET   0x0118 payload = [eq_type]；QUERY 0x0218；ACK 0x8118；REPORT 0x8218 payload = [00][eq_type]
    // （已用官方 App 切音效抓包实锤：0=标准,1=清澈人声,2=超重低音,3=清亮高音,5=悠扬听书）
    public const ushort SetAudioEffect = 0x0118;
    public const ushort QueryAudioEffect = 0x0218;
    public const ushort AckAudioEffect = 0x8118;
    public const ushort ReportAudioEffect = 0x8218;
    // ---- 低延迟游戏模式（未实测确认，沿用旧工程占位；TWS 3e 可能不支持）----
    public const ushort SetLowLatencyGaming = 0x0151;
    public const ushort QueryLowLatencyGaming = 0x0251;
    public const ushort AckLowLatencyGaming = 0x8151;
    public const ushort ReportLowLatencyGaming = 0x8251;
    // ---- 空间音频（未实测确认；TWS 3e 大概率无此功能，能力白名单默认隐藏）----
    // 命令字源自反编译（set_spatial_audio），未经真机抓包确认。注意：旧工程的
    // 0x0218/0x8218 曾被误标为空间音效，实测那是 EQ 预设通知，已更正，请勿回填。
    public const ushort SetSpatialAudio = 0x0139;
    // 简单开/关开关（IBrandManager.SetSpatialSoundAsync）与 3 档模式（SetSpatialAudioAsync）
    // 是同一空间音频能力的两种 API；bool 开关路由到同一命令字（1=开/0=关）。
    public const ushort SetSpatialSound = 0x0139;
    public const ushort QuerySpatialAudio = 0x0239;
    public const ushort AckSpatialAudio = 0x8139;
    public const ushort ReportSpatialAudio = 0x8239;
    // 与 ReportSpatialAudio 共用同一上报命令字（避免与已验证的 EQ 0x8218 冲突）。
    public const ushort ReportSpatialSound = 0x8239;
    // ---- 多连接（双设备）命令（旧工程交叉验证；TWS 3e 不支持，运行期超时探测后隐藏面板）----
    public const ushort QueryMultiConnect      = 0x0249;
    public const ushort ReportMultiConnect      = 0x8249;
    public const ushort SetMultiConnect         = 0x014A;  // 全量设置设备列表（每条 MAC[6]+state[1]）
    public const ushort AckMultiConnect         = 0x814A;
    public const ushort EnableMultiConnect      = 0x014C;  // [enable:1] 开关双连功能（RFCOMM/GAIA 通道 payload 不带本机 MAC；PROTOCOL.md 总表的本机 MAC 前缀为 GATT/BLE 通道格式，通道不同不矛盾）
    public const ushort AckEnableMultiConnect   = 0x814C;
    public const ushort RemoveMultiConnect      = 0x014D;  // [目标MAC:6] 移除已记忆设备（RFCOMM/GAIA 通道不带本机 MAC；GATT 通道需追加本机 MAC 前缀，见 PROTOCOL.md）
    public const ushort AckRemoveMultiConnect   = 0x814D;
    // ---- 手机时间同步（耳机主动请求）----
    public const ushort PeerTimeRequest      = 0x8509;
    public const ushort HostTimeResponse     = 0x0509;
    // ---- 埋点上报（吸收忽略，V 字段含固件版本，但空闲态不主动推送，仅作兜底）----
    public const ushort TelemetryReport      = 0x8224;
    // ---- 设备信息主动查询（连接即查，填充"设备详情"页，与 OPPO 侧 0x0105 固件查询同理）----
    // 命令字来自官方 App 连接序列 btsnoop 归因（手机查询→耳机响应）：
    //   0x021C → 0x821C = 固件/版本字节；0x021B → 0x821B = 型号；0x0215 → 0x8215 = 序列号
    // vivo 空闲态不主动推送固件版本，必须首次连接时主动查询（同 OPPO 首次连接查 0x0105）。
    public const ushort QueryFirmware        = 0x021C;
    public const ushort ReportFirmware        = 0x821C;
    public const ushort QueryModel            = 0x021B;
    public const ushort ReportModel            = 0x821B;
    // ---- 双击手势动作码（实测；左/右耳编号不同）----
    // 0x00~0x06（左）/0x10~0x16（右）为全区间；官方 App 双击选项仅用 0,1,2,3,5,6（部分机型+7 快捷指令），
    // 0x04(左)/0x14(右) 为官方 App UI 未暴露的空档，固件支持、且 App 描述文案明确"双击接听/挂断通话"，
    // 故 0x04/0x14 = 来电接听/结束通话（[推断]：仅由代码空档+文案推断，待真机抓包确认）。
    public static readonly IReadOnlyDictionary<byte, string> TapLeftCodes = new Dictionary<byte, string>
    {
        [0x00] = "语音助手",
        [0x01] = "播放/暂停",
        [0x02] = "上一首",
        [0x03] = "下一首",
        [0x04] = "接听/挂断通话",
        [0x05] = "翻译",
        [0x06] = "无",
    };
    public static readonly IReadOnlyDictionary<byte, string> TapRightCodes = new Dictionary<byte, string>
    {
        [0x10] = "语音助手",
        [0x11] = "播放/暂停",
        [0x12] = "上一首",
        [0x13] = "下一首",
        [0x14] = "接听/挂断通话",
        [0x15] = "翻译",
        [0x16] = "无",
    };
    // 长按功能码 → 名称
    // 长按功能码 = 噪声模式码（权威：TWS-Pods-PC/vivo/vivo_protocol.py）。0xFF=无；
    // 0x0B=全场景(切换噪声控制出厂基线)、0x0A=排除关闭、0x08=排除通透、0x09=排除降噪。
    public static readonly IReadOnlyDictionary<byte, string> LongPressFuncCodes = new Dictionary<byte, string>
    {
        [0xFF] = "无",
        [0x0B] = "切换噪声控制",
        [0x0A] = "排除关闭",
        [0x08] = "排除通透",
        [0x09] = "排除降噪",
    };
}
// 型号画像（驱动帧 GAIA 版本 / 噪声查询载荷 / 噪声 SET 后缀；对齐官方 App / Windows 逆向参考 VivoProfiles）。
// 全部非握手命令统一用 GaiaVersion（v4 家族 / v3 旧系），由 VivoManagerFactory 注入 VivoFrameCodec。
public sealed record VivoProfile(int GaiaVersion, byte[] NoiseQueryPayload, byte[] NoiseSetSuffix)
{
    public static readonly VivoProfile Air3ProV3 = new(3, [], [4, 0]);
    public static readonly VivoProfile Tws3eV3 = new(3, [], [3]);
    public static readonly VivoProfile FamilyDefaultV4 = new(4, [0], [3, 1]);
}
// 噪声模式字节 + 降噪档位（reduceModel）映射，按型号可覆盖（见 VivoDeviceModelData.NoiseModeOverrides）。
// mode 字节：0=降噪(NC) / 1=关闭(Off) / 2=通透(Trans)（APK 逆向 + 多型号真机一致，全型号统一）。
// SET 噪声模式帧(0x0130) 第二字节起的载荷按 NoiseSetSuffix 生成（代际差异，故按型号覆盖，对齐官方 App）：
//   · NoiseSetSuffix 非 null → 固定后缀：payload = [mode, ..NoiseSetSuffix]
//     （TWS 4 系 = [mode, 0x03, 0x01] 官方 App 抓包逐字节实锤，GAIA v4；
//      TWS 3e = [mode, 0x03]；Air3 Pro 系 = [mode, 0x04, 0x00]，均见 Windows 逆向参考 VivoProfiles）。
//   · NoiseSetSuffix == null → legacy fallback：按模式取降噪档位 payload = [mode, ReduceForMode(mode)]
//     （当前无型号使用；旧工程曾误用此格式发 2 字节载荷，被 TWS 4 固件忽略 → 软件内无法切模式）。
public sealed record VivoNoiseModeMap(
    byte NoiseCancellation, byte Off, byte Transparency, byte ReduceNc, byte ReduceOff, byte ReduceTransparency,
    byte[]? NoiseSetSuffix = null)
{
    public static readonly VivoNoiseModeMap Canonical =
        new(0x00, 0x01, 0x02, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceNcDefault, VivoConstants.NoiseReduceTransDefault,
            NoiseSetSuffix: [0x03, 0x01]);
    public byte ModeByte(NoiseMode mode) => mode switch
    {
        NoiseMode.NoiseCancellation => NoiseCancellation,
        NoiseMode.Off => Off,
        NoiseMode.Transparency => Transparency,
        _ => Off,
    };
    public byte ReduceForMode(NoiseMode mode) => mode switch
    {
        NoiseMode.NoiseCancellation => ReduceNc,
        NoiseMode.Off => ReduceOff,
        NoiseMode.Transparency => ReduceTransparency,
        _ => ReduceOff,
    };
}
