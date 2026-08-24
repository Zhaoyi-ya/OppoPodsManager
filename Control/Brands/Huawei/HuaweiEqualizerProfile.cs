using System;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Equalizers;

namespace OppoPodsManager.Control.Brands.Huawei;

/// <summary>
/// 华为均衡器档案：仅覆盖内置预设（默认/重低音/高音增强/人声），自定义 EQ 编辑暂缓。
/// 协议来自 OpenFreebuds config_equalizer.py（S2B C49 写 / S2B C4A 读）。
/// 解码写入的业务状态仍在 <see cref="BusinessState"/>，本档案只负责“协议 ⇄ 业务”翻译。
/// </summary>
public sealed class HuaweiEqualizerProfile : IEqualizerProfile
{
    private readonly BusinessState _state;

    public HuaweiEqualizerProfile(BusinessState state)
    {
        _state = state;
    }

    // 内置预设显示名直接用中文，无需再查表。
    public string ResolvePresetDisplayName(string protocolName) => protocolName;

    // 自定义 EQ 编辑暂缓：名称校验恒为 false，UI 不会开放自定义编辑入口。
    public bool IsValidCustomEqualizerName(string name) => false;

    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
        => Array.Empty<sbyte>();

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, 0, 0, Array.Empty<ushort>(), Array.Empty<sbyte>());

    // 写内置预设：TLV (1, presetId) → 0x01 0x01 <id>，与 OpenFreebuds change_rq(0x2b49, [(1, mode_id)]) 一致。
    public byte[] EncodeSetPreset(byte presetId) => new byte[] { 0x01, 0x01, presetId };

    // 读回当前预设：响应 param2 = 当前预设 ID（单字节），映射到中文名写入业务状态。
    public void ApplyCurrentPreset(ReadOnlySpan<byte> payload)
    {
        var fields = HuaweiManager.ParseTlv(payload);
        if (!fields.TryGetValue(0x02, out var value) || value.Length < 1)
            return;
        var presetId = value.Span[0];
        var name = HuaweiConstants.EqPresetNames.TryGetValue(presetId, out var display) ? display : $"预设{presetId}";
        _state.SetEqualizer(new EqualizerSnapshot(presetId, name));
    }

    public bool ApplyCustomEqualizerEntries(ReadOnlySpan<byte> payload) => false;

    public bool TryEncodeCustomEqualizerEntry(byte action, EqualizerEntrySnapshot entry, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        return false;
    }
}
