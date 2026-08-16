using System;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Equalizers;

/// <summary>
/// 跨品牌均衡器（EQ）协议抽象。UI 与控制层只依赖此接口，不感知具体品牌的命令字、
/// 负载编码、预设名解析规则与频段白名单对齐方式。未来接入华为/索尼等新品牌时，
/// 只需提供各自的 <see cref="IEqualizerProfile"/> 实现，无需改动 EQ 页面与控制编排。
///
/// 与 <see cref="OppoPodsManager.Control.Gestures.IGestureProfile"/> 同一思路：把协议差异收敛到
/// 可插拔的“品牌档案”里，型号/能力判断仍由各 manager 的 <c>DeviceCapability</c> 负责。
/// </summary>
public interface IEqualizerProfile
{
    /// <summary>将协议层预设名解析为界面显示名（如 OPPO 的 "M0" → 本地化文案；vivo 的 "Vivo.AudioEffect.x" → 对应翻译）。</summary>
    string ResolvePresetDisplayName(string protocolName);

    /// <summary>校验设备端自定义 EQ 名称是否合法（字符集/长度等由品牌协议规定）。</summary>
    bool IsValidCustomEqualizerName(string name);

    /// <summary>按当前型号白名单频率，将设备条目的协议频段增益对齐成 UI 编辑所需的顺序数组。</summary>
    IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry);

    /// <summary>根据界面输入（频段增益、名称）构造统一业务条目，解析默认增益范围与白名单频率。</summary>
    EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains);

    /// <summary>将内置/设备端预设 ID 编码为下发负载字节。品牌协议不同时（如多字节/组合命令）各自实现。</summary>
    byte[] EncodeSetPreset(byte presetId);

    /// <summary>解析“当前 EQ”响应并写入业务状态；解析失败或空负载时静默忽略。</summary>
    void ApplyCurrentPreset(ReadOnlySpan<byte> payload);

    /// <summary>解析“自定义 EQ 列表”响应并写入业务状态；返回是否成功解析。</summary>
    bool ApplyCustomEqualizerEntries(ReadOnlySpan<byte> payload);

    /// <summary>将自定义 EQ 写入动作（新增/更新/删除）编码为下发负载；返回是否编码成功。</summary>
    bool TryEncodeCustomEqualizerEntry(byte action, EqualizerEntrySnapshot entry, out byte[] payload);
}

/// <summary>
/// 空实现：用于尚未适配 EQ 协议的品牌（如 Edifier）或会话未建立前的占位。
/// 所有解码/编码均为安全空操作，UI 通过 <c>Presentation.SupportsCustomEqualizer</c> 判定可见性，不会触发实际下发。
/// </summary>
public sealed class NullEqualizerProfile : IEqualizerProfile
{
    public static readonly NullEqualizerProfile Instance = new();

    public string ResolvePresetDisplayName(string protocolName) => protocolName;

    public bool IsValidCustomEqualizerName(string name) => false;

    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
        => Array.Empty<sbyte>();

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, 0, 0, Array.Empty<ushort>(), Array.Empty<sbyte>());

    public byte[] EncodeSetPreset(byte presetId) => new[] { presetId };

    public void ApplyCurrentPreset(ReadOnlySpan<byte> payload)
    {
    }

    public bool ApplyCustomEqualizerEntries(ReadOnlySpan<byte> payload) => false;

    public bool TryEncodeCustomEqualizerEntry(byte action, EqualizerEntrySnapshot entry, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        return false;
    }
}
