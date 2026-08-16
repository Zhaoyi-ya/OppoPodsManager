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

    // 判断型号是否具备自定义 EQ 能力。与 main 分支 HasCustomEq 对齐：
    // 仅检查 JSON 白名单声明（custom-equalizer feature），不强制要求 0x0122/0x0418 在能力位图中。
    // 实际读写时的命令可用性由调用点 CanUseCommand() 保底。
    public bool SupportsCustomEqualizer
        => SupportsFeature("custom-equalizer");

    // 与 main 分支 ResolveCustomEqFreqs 对齐：JSON 声明了 customEqualizer 但未提供
    // customEqFrequency 的型号（如 Enco Free4）回退到标准 6 频段。
    public static IReadOnlyList<int> DefaultFrequencies { get; } = [62, 250, 1000, 4000, 8000, 16000];

    /// <summary>
    /// 获取用于自定义 EQ 编辑的有效频段列表。
    /// 当 JSON 未提供频段数据但声明了 custom-equalizer 特性时，返回默认 6 频段。
    /// </summary>
    public IReadOnlyList<int> ResolvedCustomEqFrequencies
        => CustomEqFrequencies.Count > 0 ? CustomEqFrequencies : DefaultFrequencies;

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
