
using OppoPodsManager.Control.Core.Features;
namespace OppoPodsManager.Control.Abstractions;
// 汇总当前设备可供窗口展示的能力，避免界面直接依赖品牌协议模型。
public sealed record BrandPresentation(
    string ModelName,
    bool IsKnownModel,
    bool SupportsSpatialAudio,
    bool SupportsCustomEqualizer,
    bool SupportsNoiseCancellation,
    bool CanManageMultiDevice,
    IReadOnlyList<ushort> CustomEqFrequencies,
    sbyte CustomEqMinimumGain,
    sbyte CustomEqMaximumGain,
    IReadOnlyList<string> EqualizerPresets,
    IReadOnlySet<string> VisibleControls,
    IReadOnlyDictionary<string, bool> ControlStates,
    IReadOnlyDictionary<string, bool> ControlEnabledStates,
    IReadOnlyList<NoiseOptionModel> NoiseOptions,
    string CurrentNoiseModeKey)
{
    // 提供未连接设备时用于初始化界面控件的默认 EQ 范围。
    public const sbyte DefaultCustomEqMinimumGain = -6;
    public const sbyte DefaultCustomEqMaximumGain = 6;
}
