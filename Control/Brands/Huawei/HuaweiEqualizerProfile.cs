using System;
using System.Linq;
using System.Text;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Equalizers;

namespace OppoPodsManager.Control.Brands.Huawei;

/// <summary>
/// 华为均衡器档案：内置预设 + 自定义 10 段 EQ。
/// 协议来自参考 HuaweiPods（HuaweiEqualizerCodec.kt）已抓包确认的 0x2B/0x4A 状态帧与
/// 0x2B/0x49 写命令；频段增益范围 -60..60，10 段中心频率沿用常见 FreeBuds 10 段布局。
/// </summary>
public sealed class HuaweiEqualizerProfile : IEqualizerProfile
{
    // 10 段中心频率（Hz）。仅用于 UI 滑块标签，写入设备时只认增益顺序。
    public static readonly IReadOnlyList<ushort> BandFrequencies =
        new ushort[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    public const sbyte CustomEqMinimumGain = -60;
    public const sbyte CustomEqMaximumGain = 60;
    private const int MaxNameBytes = 32;
    private const int BandCount = 10;

    private readonly BusinessState _state;

    /// <summary>当前型号；自定义写命令的 operation 值随型号不同（6i/Pro5/FreeArc=0x01，7i=0x00）。</summary>
    public HuaweiRoute Route { get; set; } = HuaweiRoute.Unsupported;

    public HuaweiEqualizerProfile(BusinessState state)
    {
        _state = state;
    }

    public string ResolvePresetDisplayName(string protocolName) => protocolName;

    // 自定义 EQ 名称：非空、≤32 字节、允许字母/数字/中文/下划线/点（参考 MAX_WRITE_NAME_BYTES=32）。
    public bool IsValidCustomEqualizerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length == 0 || bytes.Length > MaxNameBytes)
            return false;
        return name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or (>= '\u4e00' and <= '\u9fa5') or '.' or '-' or '_');
    }

    // 华为频段固定 10 段、顺序与白名单一致，按频段对齐即可。
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
    {
        var gains = new sbyte[BandFrequencies.Count];
        for (var i = 0; i < BandFrequencies.Count; i++)
        {
            for (var candidate = 0; candidate < entry.Frequencies.Count; candidate++)
            {
                if (entry.Frequencies[candidate] != BandFrequencies[i])
                    continue;
                if (candidate < entry.Gains.Count)
                    gains[i] = entry.Gains[candidate];
                break;
            }
        }
        return gains;
    }

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(id, name, true, CustomEqMinimumGain, CustomEqMaximumGain,
            BandFrequencies.ToArray(),
            gains.Select(v => (sbyte)Math.Clamp((int)Math.Round(v), CustomEqMinimumGain, CustomEqMaximumGain)).ToArray());

    // 内置预设写：TLV (1, presetId) → 0x01 0x01 <id>（OpenFreebuds change_rq(0x2b49, [(1, mode_id)])）。
    public byte[] EncodeSetPreset(byte presetId) => new byte[] { 0x01, 0x01, presetId };

    // 0x2B/0x4A 状态帧同时携带内置预设与自定义预设，统一在此解析。
    public void ApplyCurrentPreset(ReadOnlySpan<byte> payload) => ApplyState(payload);

    public bool ApplyCustomEqualizerEntries(ReadOnlySpan<byte> payload)
    {
        ApplyState(payload);
        return true;
    }

    private void ApplyState(ReadOnlySpan<byte> payload)
    {
        var fields = HuaweiManager.ParseTlv(payload);
        if (!fields.TryGetValue(0x01, out var supported) || supported.Length < 1 || supported.Span[0] == 0)
            return;
        if (!fields.TryGetValue(0x02, out var selected) || selected.Length < 1)
            return;
        var selectedId = selected.Span[0];

        // 内置预设：参考 builtInIds 用于可用列表，selectedId 命中内置名则写入。
        var bandCount = fields.TryGetValue(0x05, out var b5) && b5.Length >= 1
            ? b5.Span[0]
            : (fields.TryGetValue(0x06, out var b6) ? b6.Length : BandCount);
        if (bandCount is < 1 or > BandCount)
            bandCount = BandCount;

        if (HuaweiConstants.EqPresetNames.TryGetValue(selectedId, out var presetName))
            _state.SetEqualizer(new EqualizerSnapshot(selectedId, presetName));
        else if (selectedId is >= 0x64 and <= 0x66)
        {
            var customName = fields.TryGetValue(0x07, out var n) && n.Length > 0
                ? Encoding.UTF8.GetString(n.Span).TrimEnd('\0')
                : $"自定义{selectedId:X2}";
            _state.SetEqualizer(new EqualizerSnapshot(selectedId, customName));
        }

        if (fields.TryGetValue(0x08, out var custom) && custom.Length >= CustomRecordSize &&
            custom.Length % CustomRecordSize == 0)
        {
            _state.SetEqualizerEntries(ParseCustomPresets(custom.Span));
        }
    }

    // 每个自定义预设记录：1 字节 id + 1 字节频段数(10) + 10 字节增益 + 24 字节名称（参考 parseCustomPresets）。
    private const int CustomRecordSize = 36;
    private const int CustomNameBytes = 24;

    private IReadOnlyList<EqualizerEntrySnapshot> ParseCustomPresets(ReadOnlySpan<byte> bytes)
    {
        var entries = new List<EqualizerEntrySnapshot>();
        for (var offset = 0; offset + CustomRecordSize <= bytes.Length; offset += CustomRecordSize)
        {
            var id = bytes[offset];
            var bands = bytes[offset + 1];
            if (id is < 0x64 or > 0x66 || bands != BandCount)
                continue;
            var gains = new sbyte[BandCount];
            for (var band = 0; band < BandCount; band++)
                gains[band] = unchecked((sbyte)bytes[offset + 2 + band]);
            var nameBytes = bytes.Slice(offset + 2 + BandCount, CustomNameBytes);
            var terminator = nameBytes.IndexOf((byte)0);
            var name = Encoding.UTF8.GetString(terminator >= 0 ? nameBytes[..terminator] : nameBytes).Trim();
            entries.Add(new EqualizerEntrySnapshot(id, name, id is >= 0x64 and <= 0x66,
                CustomEqMinimumGain, CustomEqMaximumGain, BandFrequencies.ToArray(), gains));
        }
        return entries;
    }

    public bool TryEncodeCustomEqualizerEntry(byte action, EqualizerEntrySnapshot entry, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        // 参考 buildCustomPacket：presetId 必须是 0x64..0x66；action 1/2 写、3 删除（设备侧删除协议未逆向，仅支持写）。
        if (action is < 1 or > 2 || entry.Frequencies.Count != BandCount)
            return false;
        if (!SupportsCustomEqualizer(Route))
            return false;

        var presetId = entry.Id is >= 0x64 and <= 0x66 ? entry.Id : (byte)0x64;
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name ?? string.Empty);
        if (nameBytes.Length == 0 || nameBytes.Length > MaxNameBytes)
            return false;

        var operationValue = Route switch
        {
            HuaweiRoute.FreeBuds6I => 0x01,
            HuaweiRoute.FreeBudsPro5 => 0x01,
            HuaweiRoute.FreeArc => 0x01,
            HuaweiRoute.FreeBuds7I => 0x00,
            _ => 0x00,
        };

        var gains = AlignCustomEqualizerGains(entry);
        var body = new byte[3 + 3 + 3 + (3 + BandCount) + (3 + nameBytes.Length)];
        var position = 0;
        // TLV (1, [presetId])
        body[position++] = 0x01; body[position++] = 0x01; body[position++] = presetId;
        // TLV (2, [10])
        body[position++] = 0x02; body[position++] = 0x01; body[position++] = BandCount;
        // TLV (5, [operationValue])
        body[position++] = 0x05; body[position++] = 0x01; body[position++] = (byte)operationValue;
        // TLV (3, [10 gains])
        body[position++] = 0x03; body[position++] = BandCount;
        foreach (var gain in gains)
            body[position++] = unchecked((byte)gain);
        // TLV (4, [name])
        body[position++] = 0x04; body[position++] = (byte)nameBytes.Length;
        nameBytes.CopyTo(body.AsSpan(position));
        payload = body;
        return true;
    }

    public static bool SupportsCustomEqualizer(HuaweiRoute route) => route switch
    {
        HuaweiRoute.FreeBuds6I or HuaweiRoute.FreeBudsPro5 or HuaweiRoute.FreeBuds7I or HuaweiRoute.FreeArc => true,
        _ => false,
    };
}
