namespace OppoPodsManager;

/// <summary>功能开关 featureType 常量（命令 0x0403 的第二字节，标识对应功能）。</summary>
public static partial class OppoProtocol
{
    // ========== 功能开关 featureType（命令 0x0403 + [featureType][status]）==========
    // 权威来源：melody 逆向 LD8/w.accept 的 refreshSwitch 分发表（featureType → setXxxStatus）。
    // 全部经 0x0403 通用功能开关命令承载，status 0=关 1=开。
    public const byte FeatureWearDetection    = 0x04;
    public const byte FeatureGameLL           = 0x06;
    public const byte FeatureVocalEnhance     = 0x09;
    public const byte FeatureHearingEnhance   = 0x0B;
    public const byte FeaturePersonalNoise    = 0x0C;
    public const byte FeatureClickTakePhoto   = 0x0D;
    public const byte FeatureZenMode          = 0x0F;
    public const byte FeatureDualDevice       = 0x11;
    public const byte FeatureSoundRecord      = 0x13;
    public const byte FeatureVoiceAssist      = 0x14;
    public const byte FeatureFreeDialog       = 0x15;
    public const byte FeatureSafeRemind       = 0x16;
    public const byte FeatureLongPowerMode    = 0x17;
    public const byte FeatureHiQualityAudio   = 0x18;
    public const byte FeatureVoiceCommand     = 0x19;
    public const byte FeatureSpatial          = 0x1B;
    public const byte FeatureAutoVolume       = 0x1C;
    public const byte FeatureBassEngine       = 0x1D;
    public const byte FeatureSaveLog          = 0x1E;
    public const byte FeatureGameEqualizer    = 0x21;
    public const byte FeatureSpineLiveMonitor = 0x22;
    public const byte FeatureSpineCervical    = 0x23;
    public const byte FeatureSpineExercise    = 0x24;
    public const byte FeatureGameSound        = 0x27;
    public const byte FeatureGameMain         = 0x28;
    public const byte FeatureAdaptiveVolume   = 0x30;
    public const byte FeatureAdaptiveEar      = 0x31;
    public const byte FeatureSpeechPerception = 0x32;
    public const byte FeatureMicControl       = 0x34;
    public const byte FeatureLongPressVolume  = 0x35;
    public const byte FeatureSwiftPair        = 0x37;
    public const byte FeatureHearingOptimize  = 0x38;
    public const byte FeatureIncomingCallCtrl = 0x39;
    public const byte FeatureSleepDetection   = 0x3A;
    public const byte FeatureHeadMotion       = 0x3B;

    /// <summary>
    /// 按 Melody GameModeItem 的兼容逻辑选择游戏模式开关：
    /// 支持游戏音效的新设备使用主游戏开关 0x28，旧设备使用低延迟开关 0x06。
    /// </summary>
    public static byte GameModeFeature(bool supportsGameSound) =>
        supportsGameSound ? FeatureGameMain : FeatureGameLL;
}
