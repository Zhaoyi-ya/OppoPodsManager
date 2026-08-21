namespace OppoPodsManager.Control.Core.Models;

// 表示型号在官方目录中的层级位置。
public sealed record ModelCatalogLocation(string Brand, string Series);

public sealed record ModelDefinition(
    string ProductId,
    string DisplayName,
    string Brand,
    string Series,
    IReadOnlyList<string> Aliases,
    IReadOnlySet<string> Features,
    IReadOnlyDictionary<byte, NoiseMode> NoiseModes,
    IReadOnlyList<NoiseModeGroup> NoiseGroups,
    IReadOnlyList<string> EqualizerPresets,
    IReadOnlyList<int> CustomEqFrequencies,
    int CustomEqMaxPresets,
    int CustomEqUiVersion,
    byte? PreferredGameSoundType,
    IReadOnlySet<int> GameSoundMutexes)
{
    public IEnumerable<string> Names => new[] { DisplayName }.Concat(Aliases);
}
