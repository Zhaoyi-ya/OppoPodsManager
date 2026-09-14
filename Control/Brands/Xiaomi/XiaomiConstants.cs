namespace OppoPodsManager.Control.Brands.Xiaomi;

// 小米 TWS 私有协议常量。
//
// 数据来源：对「小米耳机 1.38.0」（com.mi.earphone，versionCode 138000）的静态逆向。
// 所有取值均来自 `dexdump -d` 打印的静态字段初值（dex encoded static values），
// 未经人工转录；对应产物见 小米/逆向产物/02_协议与命令码/。
//
// 帧格式（三条独立证据一致）：
//   [FE DC BA] 帧头(3) + [类型 1] + [命令 1] + [长度 2, 大端] + [命令字 1 + 负载 N] + [EF] 帧尾(1)
//
//   1) RCSP 类中存在 byte[]{0xFE,0xDC,0xBA} 的 array-data 常量（classes11.dex @0x4FDE38）。
//   2) 官方 APK 的 12 个 dex 中不存在任何 CRC16 查表（含 poly 0x1021/0x8005/0xA001 三种变体），
//      故帧尾是单字节 0xEF 而非 2 字节校验。
//   3) 公开逆向资料（LineageOS android_packages_apps_XiaomiTWS、多篇实机抓包分析）给出的
//      报文均以 FE DC BA 开头、EF 结尾。
//
// 长度字段语义：= 1 + 负载长度（即「命令字 + 负载」的字节数）。已用已知真值帧
//   FE DC BA C4 02 0005 0B FF FF FF FF EF 核对（长度 0x0005 = 1 + 4）。
public static class XiaomiConstants
{
    // ---------------------------------------------------------------- 帧定界

    public const byte Header0 = 0xFE;
    public const byte Header1 = 0xDC;
    public const byte Header2 = 0xBA;

    /// <summary>帧尾定界字节。取代了早期实现里的 2 字节 CRC16（该 CRC 无法复现任何已知真值帧）。</summary>
    public const byte Footer = 0xEF;

    public const int HeaderSize = 3;
    /// <summary>长度字段偏移（大端 2 字节）。</summary>
    public const int LengthOffset = 5;
    /// <summary>命令字偏移。已知真值帧中此字节为 0x0B。</summary>
    public const int CommandOffset = 7;
    /// <summary>负载起始偏移。</summary>
    public const int PayloadOffset = 8;
    /// <summary>固定开销 = 帧头3 + 类型1 + 命令1 + 长度2 + 命令字1 + 帧尾1。</summary>
    public const int Overhead = 9;

    /// <summary>类型字节（帧内第 4 字节）。位语义据公开逆向资料：bit7 0=应答/1=请求，bit6 1=需要应答。</summary>
    /// <remarks>已知真值帧为 0xC4，另有 0xC7（降噪帧）。**该字节随命令变化，不能作为帧头校验的一部分。**</remarks>
    public const byte TypeRequest = 0xC4;

    /// <summary>已知真值帧的第 5 字节（命令字之前的固定字节）。</summary>
    public const byte OpCodeDefault = 0x02;

    // ---------------------------------------------------------------- 传输层 UUID

    /// <summary>小爱专用 SPP 服务 UUID（非标准尾部 008584d01810）。对应官方 BluetoothConstant.UUID_SPP_XIAOAI。</summary>
    public static readonly Guid UuidSppXiaoAi = new("00001101-0000-1000-8000-008584D01810");

    /// <summary>MIUI 专用 SPP 服务 UUID。官方在 &lt;clinit&gt; 中由 "0000" + intToHexString(0xFD2D) + 标准尾部拼出。</summary>
    public static readonly Guid UuidSppMiui = new("0000fd2d-0000-1000-8000-00805f9b34fb");

    /// <summary>标准 SPP。</summary>
    public static readonly Guid UuidSpp = new("00001101-0000-1000-8000-00805F9B34FB");

    /// <summary>自定义 GATT 服务（BLE 通道）。</summary>
    public static readonly Guid UuidGattService = new("0000AF00-0000-1000-8000-00805F9B34FB");
    /// <summary>自定义 GATT 写特征。</summary>
    public static readonly Guid UuidGattWrite = new("0000AF05-0000-1000-8000-00805F9B34FB");
    /// <summary>自定义 GATT 通知特征。</summary>
    public static readonly Guid UuidGattNotify = new("0000AF06-0000-1000-8000-00805F9B34FB");

    // ---------------------------------------------------------------- 识别

    /// <summary>小米蓝牙公司 ID（= 10007 = ThirdPartyVendor.THIRD_PARTY_VENDOR_ID_2717）。BLE 广播厂商数据的公司标识。</summary>
    public const ushort CompanyId = 0x2717;

    /// <summary>MIUI 侧广播名过滤串（RCSP.SCAN_FILTER_DATA_MIUI）。</summary>
    public const string ScanFilterMiui = "XM";
    /// <summary>小爱侧广播名过滤串（RCSP.SCAN_FILTER_DATA_XIAOAI）。</summary>
    public const string ScanFilterXiaoAi = "XMSMART";

    /// <summary>协议参数：普通 MTU（RCSP.DEFAULT_PROTOCOL_MTU）。</summary>
    public const int DefaultProtocolMtu = 512;
    /// <summary>协议参数：OTA MTU（RCSP.OTA_PROTOCOL_MTU）。</summary>
    public const int OtaProtocolMtu = 516;

    // ---------------------------------------------------------------- RCSP 主命令码
    // 来源：com.xiaomi.aivsbluetoothsdk.constant.Command，共 48 条，全部取自静态字段初值。

    public const ushort CmdData = 1;
    public const ushort CmdGetTargetInfo = 2;
    public const ushort CmdRebootDevice = 3;
    public const ushort CmdNotifyDeviceAppInfo = 4;
    public const ushort CmdSettingsCommunicationMtu = 5;
    public const ushort CmdDisconnectClassicBluetooth = 6;
    public const ushort CmdF2aEdrStatus = 7;
    public const ushort CmdSetTargetInfo = 8;
    public const ushort CmdGetDeviceRunInfo = 9;
    public const ushort CmdNotifyCommunicationWay = 10;
    public const ushort CmdWakeupClassicBluetooth = 11;
    public const ushort CmdNotifyPhoneVirtualAddr = 12;
    public const ushort CmdNotifyF2aBtOp = 13;
    public const ushort CmdReportDeviceStatus = 14;
    public const ushort CmdNotifyDeviceUnbound = 15;
    public const ushort CmdNotifyA2fStatus = 16;

    // 语音（小爱）
    public const ushort CmdAsrRequest = 48;
    public const ushort CmdAsrResult = 49;
    public const ushort CmdTtsRequest = 50;
    public const ushort CmdTtsResult = 51;
    public const ushort CmdNlpRequest = 52;
    public const ushort CmdNlpResult = 53;
    public const ushort CmdStartSpeech = 208;
    public const ushort CmdStopSpeech = 209;
    public const ushort CmdCancelSpeech = 210;
    public const ushort CmdLongHoldSpeech = 211;

    // 认证（配合 XiaomiAuth，见该类注释）
    public const ushort CmdAuthCheck = 80;
    public const ushort CmdAuthSendCalcResult = 81;

    // 设备配置读写（能力表的读写通道）
    public const ushort CmdSetDeviceConfig = 242;
    public const ushort CmdGetDeviceConfig = 243;
    public const ushort CmdNotifyDeviceConfig = 244;

    // 厂商扩展
    public const ushort CmdVendorJieliS18 = 240;
    public const ushort CmdVendorExtend = 241;

    // 大块数据传输
    public const ushort CmdBlobMessage = 250;
    public const ushort CmdBlobChunkData = 251;
    public const ushort CmdLaunchBulkDataTransmission = 232;

    // 查找设备
    public const ushort CmdFindDevice = 66;

    // OTA 全流程
    public const ushort CmdOtaGetDeviceUpdateFileInfoOffset = 225;
    public const ushort CmdOtaInquireDeviceIfCanUpdate = 226;
    public const ushort CmdOtaEnterUpdateMode = 227;
    public const ushort CmdOtaExitUpdateMode = 228;
    public const ushort CmdOtaSendFirmwareUpdateBlock = 229;
    public const ushort CmdOtaGetDeviceRefreshFirmwareStatus = 230;
    public const ushort CmdOtaNotifyUbootUpdateMode = 231;

    // 日志上传
    public const ushort CmdEnterLogUploadMode = 193;
    public const ushort CmdLogUploadPrecess = 194;
    public const ushort CmdExitLogUploadMode = 195;
    public const ushort CmdLogUploadRoleSwitch = 196;

    // ---------------------------------------------------------------- 设备配置项 ID
    // 来源：com.mi.earphone.bluetoothsdk.constant.DeviceConfigIdConstantKt，共 73 项。
    // 经 RCSP 的 Set(242)/Get(243)/Notify(244)DeviceConfig 读写；设备能应答哪些 ID 即代表开放哪些功能。

    public const int ConfigAudioMode = 1;
    public const int ConfigCustomClick = 2;
    public const int ConfigAutoAnswerPhone = 3;
    public const int ConfigMultipointConnection = 4;
    public const int ConfigOpenCompactness = 5;
    public const int ConfigCompactnessListener = 6;
    public const int ConfigEqModel = 7;
    public const int ConfigVirtualSurround = 36;
    public const int ConfigDataGetFail = -1;

    public const int NoiseModeChoose = 10;
    public const int NoiseLevelChoose = 11;
    public const int AutoNoise = 37;
    public const int PersonalizedNoiseReduction = 59;
    public const int SmartDenoiseStatus = 102;
    public const int AdaptiveSense = 41;
    public const int DeviceCallListener = 13;
    public const int DeviceTime = 40;
    public const int FindDevice = 9;
    public const int RemindLost = 12;
    public const int GetEarphoneSn = 39;
    public const int EarphoneVersion = 71;
    public const int LowLatency = 47;
    public const int CustomEq = 55;
    public const int NotificationVolume = 161;
    public const int SceneRendering = 54;
    public const int BluetoothInfo = 74;
    public const int DongleStatus = 56;
    public const int DongleMode = 77;
    public const int DongleGesture = 83;
    public const int DongleMonitorSwitch = 81;
    public const int DongleMonitorVolume = 82;

    // 空间音频
    public const int SwitchSpatialAudio = 29;
    public const int SpatialAudioConfig = 30;
    public const int SpatialAudioNotifySound = 58;
    public const int PersonalSpatialAudio = 79;
    public const int PersonalSpatialAudioStatus = 85;
    public const int PersonalSpatialAudioControl = 87;
    public const int DolbyAudioSpatialMode = 104;
    public const int DolbyAudioIsSupported = 118;

    // 佩戴 / 耳道
    public const int EarCanalDetection = 60;
    public const int EarCanalEarCanalFit = 61;
    public const int EarCanalDetectionExist = 62;
    public const int AudibilityAdaptation = 89;

    // 骨传导 / 游泳
    public const int BoneConductionGesture = 80;
    public const int SwimLength = 84;

    // 录音 / 翻译
    public const int StartRecord = 75;
    public const int RecordConfig = 112;
    public const int RecordGesture = 113;
    public const int RecordCodecHeader = 86;
    public const int TranslateSettings = 120;
    public const int TranslateRecordHeader = 119;

    // 通话 / 语音广播
    public const int TtsSpeakerStatus = 95;
    public const int VoiceBroadcastSwitch = 96;
    public const int VoiceBroadcastRate = 97;
    public const int VoiceBroadcastInterruption = 100;
    public const int VoiceStatus = 68;
    public const int VoiceList = 69;
    public const int VoiceConfig = 70;

    // 大块数据 / 受限配置 / 耳盒音效
    public const int BigDataTransfer = 52;
    public const int RestrictedConfig = 93;
    public const int CommutingImmerseStatus = 103;
    public const int EarboxSoundListConfig = 114;
    public const int EarboxSoundConfig = 115;
    public const int EarboxSoundSet = 116;
    public const int DualConnectionSync = 126;
    public const int SendRegion = 76;

    // 小爱（AIVS）
    public const int AivsWakeUpSwitch = 122;
    public const int AivsContinuousDialogueDuration = 123;
    public const int AivsVoiceTone = 124;
    public const int AivsSetting = 125;
    public const int AivsCodecHeader = 127;

    /// <summary>日志模式（非 1..127 区间的特殊 ID）。</summary>
    public const int LogMode = 61166;

    // ---------------------------------------------------------------- 电量（已知真值帧）
    // 真值帧 FE DC BA C4 02 00 05 0B FF FF FF FF EF 中，第 8 字节为 0x0B、负载为 4×0xFF。
    // 应答命令字 0x07 取自原实现注释（MiBudsClient 实测），尚未在官方包内独立确认。

    /// <summary>电量查询命令字。</summary>
    public const ushort BatteryQueryCommand = 0x0B;
    /// <summary>电量应答命令字。</summary>
    public const ushort BatteryResponseCommand = 0x07;
    /// <summary>电量查询负载：4×0xFF（查询全部电池位）。</summary>
    public static readonly byte[] BatteryQueryPayload = { 0xFF, 0xFF, 0xFF, 0xFF };

    // ---------------------------------------------------------------- 状态码
    // 来源：constant.RCSP / bluetoothsdk.constant.CMDStateConstantKt（两处取值一致）。

    public const int StatusSuccess = 0;
    public const int StatusFail = 1;
    public const int StatusUnknownCmd = 2;
    public const int StatusBusy = 3;
    public const int StatusNoResource = 4;
    public const int StatusCrcError = 5;
    public const int StatusAllDataCrcError = 6;
    public const int StatusParameterError = 7;
    public const int StatusResponseDataOverLimit = 8;
    public const int StatusDeviceNotSupport = 9;
    public const int StatusPartialOperateFailed = 10;
    public const int InvalidValue = -1;

    // ---------------------------------------------------------------- 属性类型（节选）
    // 来源：constant.RCSP 的 ATTR_TYPE_*（共 240 条常量，此处仅取与本工程功能相关的项）。

    public const int AttrTypeDeviceBattery = 0;
    public const int AttrTypeBattery = 2;
    public const int AttrTypeMultBattery = 7;
    public const int AttrTypeAncStatus = 4;
    public const int AttrTypeGetAncStatus = 9;
    public const int AttrTypeSpatialAudioType = 14;
    public const int AttrTypeVirtualSurroundType = 15;
    public const int AttrTypeAutoPlay = 10;

    /// <summary>厂商扩展子命令：TWS 属性（ZM_TWS_ATTR_*）。</summary>
    public const int ZmTwsAttrEqMode = 21;
    public const int ZmTwsAttrGameMode = 22;
    public const int ZmTwsAttrAncTurbo = 23;
    public const int ZmTwsAttrFindPhone = 24;
    public const int ZmTwsAttrKeyFunction = 19;
    public const int ZmTwsAttrVolumeAutoControl = 20;

    /// <summary>厂商扩展子命令：厂商自有命令码（VendorZiMiCmd）。</summary>
    public const int ZimiTwsSetKeyFunction = 81;
    public const int ZimiTwsSetVolumeAutoControl = 82;
    public const int ZimiTwsSetEqMode = 83;
    public const int ZimiTwsSetGameMode = 84;
    public const int ZimiTwsSetAncTurbo = 85;
    public const int ZimiTwsFindPhone = 86;

    /// <summary>厂商类型命令（VendorTypeConstantKt）。</summary>
    public const byte VendorTypePowerSave = 0;
    public const byte VendorTypeSetFunctionKey = 1;
    public const byte VendorTypeHotWorld = 2;
    public const byte VendorTypeSupPowerSaveNew = 3;
    public const byte VendorCmdSetNoiseReduction = 4;
    public const byte VendorCmdSetGbPlayMode = 5;
    public const byte VendorCmdSetEarDetection = 6;
}
