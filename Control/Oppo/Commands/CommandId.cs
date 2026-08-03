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
}
