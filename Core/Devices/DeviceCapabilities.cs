namespace OppoPodsManager.Core.Devices;

public sealed record DeviceCapabilities(
    IReadOnlySet<DeviceFeature> Features,
    bool SupportsCustomEqualizer = false,
    bool SupportsMultiDevice = false,
    int? EqualizerBandCount = null,
    IReadOnlyDictionary<string, byte>? EqualizerPresets = null,
    IReadOnlyDictionary<string, byte>? AncModes = null,
    IReadOnlyList<string>? SpatialModes = null)
{
    public string ModelName { get; init; } = "Unknown";
    public string ModelId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public bool IsSupported { get; init; } = true;
    public int ProtocolType { get; init; } = 1;
    public bool SupportSpp { get; init; } = true;

    public byte GameSoundType { get; init; }
    public byte GameModeFeature { get; init; }
    public IReadOnlySet<int> GameSoundMutexes { get; init; } = new HashSet<int>();
    public bool GameSoundMutexEq => GameSoundMutexes.Contains(1) || GameSoundMutexes.Contains(3);
    public bool GameSoundMutexSpatial => GameSoundMutexes.Contains(2);

    public IReadOnlyList<int> CustomEqFrequencies { get; init; } = [];
    public int CustomEqMaxPresets { get; init; }
    public int CustomEqUiVersion { get; init; }

    public IReadOnlyList<int> SpatialTypes { get; init; } = [];
    public bool HasSpatialAudio { get; init; }
    public bool HasSpatialSound { get; init; }

    public int MultiDevicesConnect { get; init; }
    public bool IsMultiConnectV1 => MultiDevicesConnect == 1;
    public bool IsMultiConnectV2 => MultiDevicesConnect >= 2;
    public bool HasMultiConnectManage { get; init; }

    public bool IsLegacyAnc { get; init; }
    public bool HasAdaptiveAnc { get; init; }
    public IReadOnlyList<AncOption> AncOptions { get; init; } = [];
    public IReadOnlyDictionary<byte, string> AncIndexToName { get; init; } =
        new Dictionary<byte, string>();

    public bool Supports(DeviceFeature feature) => Features.Contains(feature);

    public bool Has(DeviceFeature feature) => Supports(feature);

    /// <summary>双设备开关或列表任一可用即显示多设备相关 UI。</summary>
    public bool HasDualDevice => Supports(DeviceFeature.DualDevice) || Supports(DeviceFeature.MultiDevice);
    public bool HasGameMode => Supports(DeviceFeature.Gaming);
    /// <summary>仅游戏音效（0x012B/0x0423），与游戏模式（低延迟）分离。</summary>
    public bool HasGameSound => Supports(DeviceFeature.GameSound);
    public bool HasFindDevice => Supports(DeviceFeature.FindDevice);
    public bool HasBassEngine => Supports(DeviceFeature.BassEngine);
    public bool HasVocalEnhance => Supports(DeviceFeature.VocalEnhance);
    public bool HasHearingEnhancement => Supports(DeviceFeature.HearingEnhance);
    public bool HasLongPowerMode => Supports(DeviceFeature.LongPowerMode);
    public bool HasWearDetection => Supports(DeviceFeature.WearDetection);
    public bool HasSpineHealth => Supports(DeviceFeature.SpineHealth);
    public bool HasCustomEq => SupportsCustomEqualizer;
    public bool HasFirmwareUpdate => Supports(DeviceFeature.FirmwareUpdate);

    public bool CanUnpairMultiConnectDevice(IReadOnlySet<ushort> supportedCommands) =>
        IsMultiConnectV2 && supportedCommands.Contains(0x0429);

    public string DefaultEqPreset
    {
        get
        {
            if (EqualizerPresets is null || EqualizerPresets.Count == 0)
                return "Default";
            byte min = byte.MaxValue;
            var name = "Default";
            foreach (var kv in EqualizerPresets)
            {
                if (kv.Value < min)
                {
                    min = kv.Value;
                    name = kv.Key;
                }
            }
            return name;
        }
    }

    public DeviceCapabilities WithSpatialProtocol(IReadOnlySet<ushort> supportedCommands)
    {
        var whitelisted = SpatialTypes.Count > 0;
        var supportsV2 = supportedCommands.Contains(0x012A);
        return this with
        {
            HasSpatialAudio = whitelisted && supportsV2,
            HasSpatialSound = whitelisted && !supportsV2,
        };
    }

    public DeviceCapabilities WithGameModeFeature(byte featureId) =>
        this with { GameModeFeature = featureId };
}
