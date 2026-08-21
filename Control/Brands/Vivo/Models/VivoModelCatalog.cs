
using OppoPodsManager.Control.Core.Models;
namespace OppoPodsManager.Control.Brands.Vivo.Models;
// vivo 官方型号目录：按蓝牙名称解析当前设备，并向统一的型号选择界面提供只读列表。
public sealed class VivoModelCatalog
{
    private readonly IReadOnlyList<VivoModelDefinition> _allModels;
    private readonly IReadOnlyList<VivoModelDefinition> _models;
    private readonly IReadOnlyList<ModelDefinition> _uiModels;
    public VivoModelCatalog(IEnumerable<VivoModelDefinition> models)
    {
        _allModels = models
            .Where(model => !string.IsNullOrWhiteSpace(model.DisplayName))
            .ToArray();
        _models = _allModels
            .GroupBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(model => model.EnabledFeatureCount)
                .ThenByDescending(model => model.ModelId)
                .First())
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _uiModels = _models.Select(model => model.ToModelDefinition()).ToArray();
    }
    public IReadOnlyList<VivoModelDefinition> Models => _models;
    public IReadOnlyList<string> ModelNames => _models.Select(model => model.DisplayName).ToArray();
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree
        => _uiModels
            .GroupBy(model => model.Brand, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>)group
                    .GroupBy(model => model.Series, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(series => series.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(series => series.Key, series => (IReadOnlyList<ModelDefinition>)series.ToArray(), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    public VivoModelDefinition? Find(string? deviceName)
    {
        return Match(deviceName)?.Model;
    }
    // 使用官方蓝牙名称和 project_name 双通道匹配，项目代号常直接作为设备蓝牙名称出现。
    public VivoModelMatch? Match(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return null;
        var normalizedName = Normalize(deviceName);
        if (normalizedName.Length == 0)
            return null;
        var nameTokens = GetTokens(deviceName);
        var candidates = _allModels
            .Select(model => CreateMatch(model, normalizedName, nameTokens))
            .Where(match => match is not null)
            .Cast<VivoModelMatch>()
            .OrderByDescending(match => match.Kind)
            .ThenByDescending(match => match.MatchedValue.Length)
            .ThenByDescending(match => match.Model.EnabledFeatureCount)
            .ThenByDescending(match => match.Model.ModelId)
            .ToArray();
        return candidates.FirstOrDefault();
    }
    public ModelCatalogLocation? FindLocation(string? modelName)
    {
        var model = _models.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, modelName, StringComparison.OrdinalIgnoreCase));
        return model is null ? null : new ModelCatalogLocation(model.Brand, model.Series);
    }
    private static VivoModelMatch? CreateMatch(
        VivoModelDefinition model,
        string normalizedName,
        IReadOnlySet<string> nameTokens)
    {
        var normalizedDisplayName = Normalize(model.DisplayName);
        if (string.Equals(normalizedName, normalizedDisplayName, StringComparison.Ordinal))
            return new VivoModelMatch(model, VivoModelMatchKind.ExactDisplayName, model.DisplayName);
        var normalizedProjectName = Normalize(model.ProjectName);
        if (normalizedProjectName.Length > 0 && nameTokens.Contains(normalizedProjectName))
            return new VivoModelMatch(model, VivoModelMatchKind.ProjectName, model.ProjectName);
        if (normalizedDisplayName.Length > 0
            && normalizedName.Contains(normalizedDisplayName, StringComparison.Ordinal))
            return new VivoModelMatch(model, VivoModelMatchKind.DisplayNameInDeviceName, model.DisplayName);
        return null;
    }
    // 蓝牙名称可能包含空格、连字符或 LE 后缀，统一去除格式差异后比较。
    private static string Normalize(string value)
    {
        var characters = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
            if (char.IsLetterOrDigit(character))
                characters.Append(char.ToLowerInvariant(character));
        return characters.ToString();
    }
    // project_name 只允许作为完整令牌匹配，避免 DPD22 之类的片段误识别。
    private static IReadOnlySet<string> GetTokens(string value)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var characters = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                characters.Append(char.ToLowerInvariant(character));
                continue;
            }
            if (characters.Length > 0)
            {
                tokens.Add(characters.ToString());
                characters.Clear();
            }
        }
        if (characters.Length > 0)
            tokens.Add(characters.ToString());
        return tokens;
    }
}
// 匹配结果保留来源，便于连接日志解释型号为何被识别为该官方条目。
public sealed record VivoModelMatch(VivoModelDefinition Model, VivoModelMatchKind Kind, string MatchedValue);
public enum VivoModelMatchKind
{
    DisplayNameInDeviceName = 1,
    ExactDisplayName = 2,
    ProjectName = 3
}
// 保留官方 model、project_name、device_type 和 feature 位，避免将厂商能力压缩成名称特判。
public sealed record VivoModelDefinition(
    int ModelId,
    string DisplayName,
    string ProjectName,
    int DeviceType,
    IReadOnlyDictionary<string, int> Features)
{
    // 按型号噪声模式映射（mode 字节 + reduceModel 档位），默认 Canonical（全 vivo 型号统一）。
    // 非位置属性：运行时由 VivoDeviceModelData 按 ModelId 覆盖，不纳入相等性比较。
    public VivoNoiseModeMap NoiseMap { get; init; } = VivoNoiseModeMap.Canonical;
    public string Brand => DisplayName.StartsWith("iQOO", StringComparison.OrdinalIgnoreCase) ? "iQOO" : "vivo";
    public string Series => DeviceType == 4 ? "Headphones" : "TWS";
    public int EnabledFeatureCount => Features.Values.Count(value => value > 0);
    public bool HasFeature(string name) => Features.TryGetValue(name, out var value) && value > 0;
    public bool SupportsNoiseCancellation => HasFeature("noise_reduction");
    public bool SupportsLowLatencyGaming => HasFeature("low_latency_gaming");
    public bool SupportsSpatialAudio => HasFeature("spatial_audio") || HasFeature("spatial_audio_3d");
    public bool SupportsAudioEffect => HasFeature("audio_effect");
    public int AudioEffectVersion => Features.TryGetValue("audio_effect", out var version) ? version : 0;
    public ModelDefinition ToModelDefinition()
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        var noiseModes = new Dictionary<byte, NoiseMode>();
        if (SupportsNoiseCancellation)
        {
            features.Add("noise-cancellation");
            noiseModes[NoiseMap.NoiseCancellation] = NoiseMode.NoiseCancellation;
            noiseModes[NoiseMap.Off] = NoiseMode.Off;
            noiseModes[NoiseMap.Transparency] = NoiseMode.Transparency;
        }
        if (SupportsLowLatencyGaming)
            features.Add("game-mode");
        if (SupportsSpatialAudio)
            features.Add("spatial-audio");
        if (SupportsAudioEffect)
            features.Add("equalizer");
        return new ModelDefinition(
            ProjectName,
            DisplayName,
            Brand,
            Series,
            SupportsAudioEffect ? VivoAudioEffectCatalog.GetPresetKeys(AudioEffectVersion) : [],
            features,
            noiseModes,
            [],
            [],
            [],
            0,
            0,
            null,
            new HashSet<int>());
    }
}
// 从官方目录解析出的运行时能力，只暴露当前桌面端已经实现协议读写的功能。
public sealed record VivoDeviceCapability(VivoModelDefinition? Model)
{
    public bool IsKnownModel => Model is not null;
    public string ModelName => Model?.DisplayName ?? string.Empty;
    public bool SupportsNoiseCancellation => Model?.SupportsNoiseCancellation == true;
    public bool SupportsLowLatencyGaming => Model?.SupportsLowLatencyGaming == true;
    public bool SupportsSpatialAudio => Model?.SupportsSpatialAudio == true;
    public bool SupportsAudioEffect => Model?.SupportsAudioEffect == true;
    public IReadOnlyList<string> AudioEffectPresetKeys => Model is null
        ? []
        : VivoAudioEffectCatalog.GetPresetKeys(Model.AudioEffectVersion);
    public DeviceCapability ToDeviceCapability()
    {
        if (Model is null)
            return DeviceCapability.Unknown;
        var definition = Model.ToModelDefinition();
        return new DeviceCapability(
            definition.ProductId,
            definition.DisplayName,
            true,
            new HashSet<ushort>(),
            definition.Features,
            definition.NoiseModes,
            definition.NoiseGroups,
            definition.EqualizerPresets,
            definition.CustomEqFrequencies,
            definition.CustomEqMaxPresets,
            definition.CustomEqUiVersion,
            definition.PreferredGameSoundType,
            definition.GameSoundMutexes);
    }
}
