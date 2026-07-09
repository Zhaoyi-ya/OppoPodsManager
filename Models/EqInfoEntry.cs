using System;

namespace OppoPodsManager;

/// <summary>设备端 EQ 预设条目，由 0x0122 getAllEqInfo 响应解析而来。</summary>
public class EqInfoEntry
{
    /// <summary>EQ 预设唯一 ID（protocolIndex）。</summary>
    public byte EqId { get; set; }

    /// <summary>设备端允许的最小增益值。</summary>
    public int MinValue { get; set; } = -6;

    /// <summary>设备端允许的最大增益值。</summary>
    public int MaxValue { get; set; } = 6;

    /// <summary>设备端存储的预设名称（UTF-8）；空串表示未命名。</summary>
    public string Name { get; set; } = "";

    /// <summary>是否当前选中激活的预设。</summary>
    public bool IsSelected { get; set; }

    /// <summary>频段值列表（Hz），int16 LE 解析。</summary>
    public int[] Frequencies { get; set; } = Array.Empty<int>();

    /// <summary>频段增益值列表（dB，signed byte）。</summary>
    public int[] Gains { get; set; } = Array.Empty<int>();
}
