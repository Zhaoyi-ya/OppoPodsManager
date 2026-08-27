using System;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Equalizers;

namespace OppoPodsManager.Control.Brands.Vivo;

/// <summary>
/// vivo 均衡器档案：vivo 的内置音效走独立音频效果协议（VivoAudioEffectCatalog），
/// 自定义 EQ 的编码/增益对齐不通过 <see cref="IEqualizerProfile"/> 消费（vivo 的
/// SupportsCustomEqualizer=false，UI 不触发）。本档案只需把协议键 "Vivo.AudioEffect.x" 解析为
/// 本地化显示名（标准/清晰人声/澎湃低音…），其余方法委托空实现占位。
/// </summary>
public sealed class VivoEqualizerProfile : IEqualizerProfile
{
    public static readonly VivoEqualizerProfile Instance = new();

    public string ResolvePresetDisplayName(string protocolName)
        => DeviceProfileLoader.LocalizedEqName(protocolName);

    public bool IsValidCustomEqualizerName(string name)
        => NullEqualizerProfile.Instance.IsValidCustomEqualizerName(name);

    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
        => NullEqualizerProfile.Instance.AlignCustomEqualizerGains(entry);

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => NullEqualizerProfile.Instance.CreateCustomEqualizerEntry(id, name, gains);

    public byte[] EncodeSetPreset(byte presetId)
        => NullEqualizerProfile.Instance.EncodeSetPreset(presetId);

    public void ApplyCurrentPreset(ReadOnlySpan<byte> payload)
        => NullEqualizerProfile.Instance.ApplyCurrentPreset(payload);

    public bool ApplyCustomEqualizerEntries(ReadOnlySpan<byte> payload)
        => NullEqualizerProfile.Instance.ApplyCustomEqualizerEntries(payload);

    public bool TryEncodeCustomEqualizerEntry(byte action, EqualizerEntrySnapshot entry, out byte[] payload)
        => NullEqualizerProfile.Instance.TryEncodeCustomEqualizerEntry(action, entry, out payload);
}
