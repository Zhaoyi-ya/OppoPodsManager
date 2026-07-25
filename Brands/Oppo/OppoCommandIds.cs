namespace OppoPodsManager.Brands.Oppo;

internal static class OppoCommandIds
{
    public const ushort QueryBattery = 0x0106;
    public const ushort QueryAnc = 0x010C;
    public const ushort QueryEq = 0x010F;
    public const ushort QuerySpatialAudio = 0x012A;
    public const ushort QueryGameSound = 0x012B;
    public const ushort QueryMultiDevice = 0x0112;
    /// <summary>getMultiConnectPriorityDevice — 多连接优先设备/自动选择策略。</summary>
    public const ushort QueryMultiPriority = 0x0132;
    public const ushort QueryVersion = 0x0105;
    public const ushort QueryCodec = 0x0114;
    public const ushort QueryEqualizerDetails = 0x0122;
    public const ushort QueryFeatureState = 0x010D;
    public const ushort SetFeature = 0x0403;
    public const ushort SetAnc = 0x0404;
    public const ushort SetEq = 0x0406;
    public const ushort SetSpatialAudio = 0x0422;
    public const ushort SetGameSound = 0x0423;
    public const ushort OperateMultiDevice = 0x0429;
    public const ushort SetBassEngine = 0x041B;           // setBassEngineValue — 专用，非 0x0403
    /// <summary>processHearingEnhancementDetection — 听力检测流程命令， alone 不足以开 UI。</summary>
    public const ushort SetHearingDetect = 0x040D;
    /// <summary>getHearingEnhancementData — 听力增强数据查询，UI 入口必需。</summary>
    public const ushort QueryHearingEnhance = 0x0115;
    public const ushort SetHearingEnhancement = 0x040D;   // alias
    public const ushort SetEqualizerDetails = 0x0418;
    public const ushort SetFindDevice = 0x0400;
    public const ushort QueryNotifyCapability = 0x0200;
}
