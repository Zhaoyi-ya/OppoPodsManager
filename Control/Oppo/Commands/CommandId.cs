namespace OppoPodsManager.Control.Oppo.Commands;

public static class CommandId
{
    public const ushort Capabilities = 0x0100;
    public const ushort Mtu = 0x0101;
    public const ushort VendorId = 0x0102;
    public const ushort ProductId = 0x0103;
    public const ushort ColorId = 0x010B;
    public const ushort FirmwareVersion = 0x0105;
    public const ushort Battery = 0x0106;
    public const ushort WearStatus = 0x0109;
    public const ushort NoiseCancellation = 0x010C;
    public const ushort FeatureStates = 0x010D;
    public const ushort CurrentEqualizer = 0x010F;
    public const ushort HearingEnhancement = 0x0115;
    public const ushort SpatialAudio = 0x012A;
    public const ushort BassEngine = 0x0124;
    public const ushort MultiDeviceInformation = 0x0112;
    public const ushort Codec = 0x0114;
    public const ushort MultiDevicePriority = 0x0132;
    public const ushort EqualizerEntries = 0x0122;
    public const ushort GameSound = 0x012B;

    // 触控手势（KeyFunction）：Enco Free4 真机抓包确认。
    // GET=0x0108 / SET=0x0408，负载 = [count_hi,count_lo] + count×[deviceType,button,buttonAction,function]，
    // 与官方 KeyFunctionInfo.convertToData 字节序一致。
    public static readonly ushort KeyFunction = 0x0108; // GET 触控表

    public const ushort SetFeature = 0x0403;
    public const ushort SetNoiseCancellation = 0x0404;
    public const ushort SetEqualizer = 0x0406;
    public const ushort SetHearingEnhancement = 0x040D;
    public const ushort SetBassEngine = 0x041B;
    public const ushort SetFindDevice = 0x0400;
    public const ushort SetSpatialAudio = 0x0422;
    public const ushort OperateMultiDevice = 0x0429;
    public const ushort SetEqualizerEntry = 0x0418;
    public const ushort SetGameSound = 0x0423;
    public static readonly ushort SetKeyFunction = 0x0408; // SET 触控表

    public const ushort CapabilitiesResponse = 0x8100;
    public const ushort ProductIdResponse = 0x8103;
    public const ushort FirmwareVersionResponse = 0x8105;
    public const ushort BatteryResponse = 0x8106;
    public const ushort WearStatusResponse = 0x8109;
    public const ushort NoiseCancellationResponse = 0x810C;
    public const ushort FeatureStatesResponse = 0x810D;
    public const ushort CurrentEqualizerResponse = 0x810F;
    public const ushort HearingEnhancementResponse = 0x8115;
    public const ushort BassEngineResponse = 0x8124;
    public const ushort SpatialAudioResponse = 0x812A;
    public const ushort MultiDeviceInformationResponse = 0x8112;
    public const ushort CodecResponse = 0x8114;
    public const ushort MultiDevicePriorityResponse = 0x8132;
    public const ushort EqualizerEntriesResponse = 0x8122;
    public const ushort GameSoundResponse = 0x812B;
    public static readonly ushort KeyFunctionResponse = 0x8108; // GET 0x0108 响应

    public const ushort SetFeatureResponse = 0x8403;
    public const ushort SetNoiseCancellationResponse = 0x8404;
    public const ushort SetEqualizerResponse = 0x8406;
    public const ushort SetHearingEnhancementResponse = 0x840D;
    public const ushort SetBassEngineResponse = 0x841B;
    public const ushort SetFindDeviceResponse = 0x8400;
    public const ushort SetSpatialAudioResponse = 0x8422;
    public const ushort OperateMultiDeviceResponse = 0x8429;
    public const ushort SetEqualizerEntryResponse = 0x8418;
    public const ushort SetGameSoundResponse = 0x8423;
    public static readonly ushort SetKeyFunctionResponse = 0x8408; // SET 0x0408 响应
    public static readonly ushort Unknown041CResponse = 0x841C; // SET 0x041C 响应（与 0x0408 同族的另一个键功能 SET 候选写入入口）

    public const ushort NotificationCapabilities = 0x0200;
    public const ushort RegisterNotification = 0x0201;
    public const ushort NotificationEvent = 0x0204;
    // 耳机主动上报当前 EQ 变化，官方桌面参考项目对应 0x0504。
    public const ushort EqualizerChangedNotification = 0x0504;
    public const ushort RegisterNotifications = 0x0205;

    public const ushort NotificationCapabilitiesResponse = 0x8200;
    public const ushort RegisterNotificationResponse = 0x8201;
    public const ushort NotificationEventResponse = 0x8202;
    public const ushort RegisterNotificationsResponse = 0x8205;

    // 官方 ProtocolManager 始终允许使用的基础命令，不依赖 0x0100 位图。
    public static IReadOnlySet<ushort> AlwaysSupportedCommands { get; } = new HashSet<ushort>
    {
        Capabilities,
        Mtu,
        VendorId,
        ProductId,
        0x0104,
        ColorId,
        0x0F00,
        0x0F04,
        FeatureStates,
        0x0F03,
        Battery
    };

    // ===== 官方存在但尚未实现/命名的命令（欢律 APK 全量逆向，2026-08-16）=====
    // 权威命令空间在 HeadsetCoreService（统一发包 u0()）；响应 = 请求 | 0x8000。
    // 这些 opcode 已在官方 App 确认存在，本项目尚未接入对应功能。
    // 命名 Unknown<Hex> 表示功能语义待后续逆向/真机确认，接入功能时请改名。

    // GET（0x01xx）官方存在、项目未实现
    public const ushort Unknown0107 = 0x0107;
    public const ushort Unknown0116 = 0x0116;
    public const ushort Unknown0118 = 0x0118;
    public const ushort Unknown0119 = 0x0119;
    public const ushort Unknown011A = 0x011A;
    public const ushort Unknown011C = 0x011C;
    public const ushort Unknown011D = 0x011D;
    public const ushort Unknown011E = 0x011E;
    public const ushort Unknown011F = 0x011F;
    public const ushort Unknown0121 = 0x0121;
    public const ushort Unknown0123 = 0x0123;
    public const ushort Unknown0125 = 0x0125;
    public const ushort Unknown0126 = 0x0126;
    public const ushort Unknown0127 = 0x0127;
    public const ushort Unknown0129 = 0x0129;
    public const ushort Unknown012E = 0x012E;
    public const ushort KeyFunctionSubGet = 0x012F;   // 官方 KeyFunction 子命令（响应 0x812F）
    public const ushort Unknown0130 = 0x0130;
    public const ushort Unknown0131 = 0x0131;
    public const ushort Unknown0133 = 0x0133;
    public const ushort Unknown0134 = 0x0134;
    public const ushort Unknown0180 = 0x0180;

    // SET（0x04xx）官方存在、项目未实现（响应 = opcode | 0x8000）
    public const ushort Unknown0402 = 0x0402;
    public const ushort Unknown0405 = 0x0405;
    public const ushort Unknown040E = 0x040E;
    public const ushort Unknown040F = 0x040F;
    public const ushort Unknown0410 = 0x0410;
    public const ushort Unknown0411 = 0x0411;
    public const ushort Unknown0412 = 0x0412;
    public const ushort Unknown0413 = 0x0413;
    public const ushort KeyFunctionSubSet = 0x0414;   // 官方 KeyFunction 子命令（响应 0x8414）
    public const ushort Unknown0415 = 0x0415;
    public const ushort Unknown0417 = 0x0417;
    public const ushort Unknown041A = 0x041A;
    public const ushort Unknown041C = 0x041C;         // 与 0x0408 同族，疑另一键功能 SET
    public const ushort Unknown041D = 0x041D;
    public const ushort Unknown041E = 0x041E;
    public const ushort Unknown041F = 0x041F;         // payload=String.getBytes → 疑 SetDeviceName
    public const ushort Unknown0420 = 0x0420;
    public const ushort Unknown0421 = 0x0421;
    public const ushort Unknown0424 = 0x0424;
    public const ushort Unknown0425 = 0x0425;
    public const ushort Unknown0426 = 0x0426;
    public const ushort Unknown0427 = 0x0427;
    public const ushort Unknown0428 = 0x0428;
    public const ushort Unknown042B = 0x042B;
    public const ushort Unknown042C = 0x042C;
    public const ushort Unknown042D = 0x042D;
    public const ushort Unknown042E = 0x042E;
    public const ushort Unknown0430 = 0x0430;
    public const ushort Unknown0431 = 0x0431;

    // 0x08 家族（control/indication 类，未全映射）
    public const ushort Control0810 = 0x0810;         // HeadsetCoreService$e
    public const ushort Control0814 = 0x0814;         // commands/l（KeyFunction 相关）
    public const ushort Control0810Response = 0x8810;
    public const ushort Control0814Response = 0x8814;
    public const ushort KeyFunctionSubGetResponse = 0x812F;
    public const ushort KeyFunctionSubSetResponse = 0x8414;
}
