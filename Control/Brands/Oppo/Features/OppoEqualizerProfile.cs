using System;
using System.Linq;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

/// <summary>
/// OPPO（欢律/Melody 协议）均衡器档案：把 EQ 的协议编解码与预设名解析收敛到单一实现，
/// 供 <see cref="OppoManager"/> 通过 <c>IEqualizerProfile</c> 接口消费。
/// 解码写入的业务状态仍在 <see cref="BusinessState"/>，本档案只负责“协议 ⇄ 业务”的翻译。
/// </summary>
public sealed class OppoEqualizerProfile : IEqualizerProfile
{
    private readonly BusinessState _state;
    private readonly Func<DeviceCapability> _getCapability;

    // 通过实时能力访问器而非构造时快照，确保切换到手动型号或能力收敛后，
    // 频段白名单与预设名表始终与当前 Capability 同步（与重构前 manager 直接读 Capability 的行为一致）。
    public OppoEqualizerProfile(BusinessState state, Func<DeviceCapability> getCapability)
    {
        _state = state;
        _getCapability = getCapability;
    }

    public string ResolvePresetDisplayName(string protocolName)
        => DeviceProfileLoader.LocalizedEqName(protocolName);

    public bool IsValidCustomEqualizerName(string name)
        => CustomEqualizer.IsValidName(name);

    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
    {
        var frequencies = _getCapability().ResolvedCustomEqFrequencies
            .Select(value => (ushort)Math.Clamp(value, 0, ushort.MaxValue))
            .ToArray();
        return CustomEqualizer.AlignGains(frequencies, entry);
    }

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
    {
        var capability = _getCapability();
        var frequencies = capability.ResolvedCustomEqFrequencies
            .Select(value => (ushort)Math.Clamp(value, 0, ushort.MaxValue))
            .ToArray();
        var existing = _state.Snapshot().EqualizerEntries.FirstOrDefault(entry =>
            id > 0 && entry.Id == id
            || string.Equals(entry.Name, name, StringComparison.Ordinal));
        var minimumGain = existing?.MinimumGain ?? CustomEqualizer.DefaultMinimumGain;
        var maximumGain = existing?.MaximumGain ?? CustomEqualizer.DefaultMaximumGain;
        return CustomEqualizer.CreateEntry(id, name, frequencies, gains, minimumGain, maximumGain);
    }

    public byte[] EncodeSetPreset(byte presetId) => new[] { presetId };

    public void ApplyCurrentPreset(ReadOnlySpan<byte> payload)
        => new Equalizer(_state, _getCapability()).ApplyCurrentPreset(payload);

    public bool ApplyCustomEqualizerEntries(ReadOnlySpan<byte> payload)
        => new CustomEqualizer(_state).Apply(payload);

    public bool TryEncodeCustomEqualizerEntry(byte action, EqualizerEntrySnapshot entry, out byte[] payload)
        => CustomEqualizer.TryBuildPayload(action, entry, out payload);
}
