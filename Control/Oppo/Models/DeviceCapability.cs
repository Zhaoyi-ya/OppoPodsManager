namespace OppoPodsManager.Control.Oppo.Models;

using OppoPodsManager.Control.Oppo.Commands;

public sealed record DeviceCapability(
    string ProductId,
    string ModelName,
    bool IsKnownModel,
    IReadOnlySet<ushort> SupportedCommands,
    IReadOnlySet<string> SupportedFeatures,
    IReadOnlyDictionary<byte, NoiseMode> NoiseModes,
    IReadOnlyList<NoiseModeGroup> NoiseGroups,
    IReadOnlyList<string> EqualizerPresets,
    IReadOnlyList<int> CustomEqFrequencies,
    int CustomEqMaxPresets,
    int CustomEqUiVersion,
    byte? PreferredGameSoundType,
    IReadOnlySet<int> GameSoundMutexes)
{
    public bool SupportsCommand(ushort command) => SupportedCommands.Contains(command);

    public bool SupportsFeature(string feature) => SupportedFeatures.Contains(feature);

    // 判断型号是否同时具备白名单、自定义 EQ 频段和完整读写命令。
    public bool SupportsCustomEqualizer
        => SupportsFeature("custom-equalizer")
            && CustomEqFrequencies.Count > 0
            && SupportsCommand(CommandId.EqualizerEntries)
            && SupportsCommand(CommandId.SetEqualizerEntry);

    // 判断三模式空间音频是否具备完整的查询和设置能力。
    public bool SupportsSpatialAudio
        => SupportsFeature("spatial-audio")
            && SupportsCommand(CommandId.SpatialAudio)
            && SupportsCommand(CommandId.SetSpatialAudio);

    // 判断降噪控制是否具备完整协议能力和至少一个可用模式。
    public bool SupportsNoiseCancellation
        => SupportsFeature("noise-cancellation")
            && NoiseModes.Count > 0
            && SupportsCommand(CommandId.NoiseCancellation)
            && SupportsCommand(CommandId.SetNoiseCancellation);

    public bool GameSoundBlocksEqualizer => GameSoundMutexes.Contains(1) || GameSoundMutexes.Contains(3);

    public bool GameSoundBlocksSpatialSound => GameSoundMutexes.Contains(2);

    public static DeviceCapability Unknown { get; } = new(
        string.Empty,
        string.Empty,
        false,
        new HashSet<ushort>(),
        new HashSet<string>(),
        new Dictionary<byte, NoiseMode>(),
        [],
        [],
        [],
        0,
        0,
        null,
        new HashSet<int>());
}

public enum NoiseMode
{
    Unknown,
    Off,
    NoiseCancellation,
    Transparency,
    Smart,
    Light,
    Medium,
    Deep
}

// 表示原项目中的降噪父模式及其可选子模式。
public sealed record NoiseModeGroup(NoiseMode Parent, IReadOnlyList<NoiseModeOption> Children);

// 保存子模式协议索引，保证 UI 选择后能发送准确的设备值。
public sealed record NoiseModeOption(byte ProtocolIndex, NoiseMode Mode);
