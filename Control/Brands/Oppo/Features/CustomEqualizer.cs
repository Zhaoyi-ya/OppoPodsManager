using System.Text;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析自定义均衡器条目并构造编辑命令负载。
public sealed class CustomEqualizer
{
    public const sbyte DefaultMinimumGain = -6;
    public const sbyte DefaultMaximumGain = 6;

    private readonly BusinessState _state;

    public CustomEqualizer(BusinessState state)
    {
        _state = state;
    }

    // 根据编辑结果创建统一的自定义 EQ 业务条目。
    public static EqualizerEntrySnapshot CreateEntry(
        byte id,
        string name,
        IReadOnlyList<ushort> frequencies,
        IReadOnlyList<sbyte> gains,
        sbyte minimumGain,
        sbyte maximumGain)
        => new(id, name, true, minimumGain, maximumGain, frequencies.ToArray(), gains.ToArray());

    // 将界面输入的连续滑块值限制到设备允许的整数增益范围。
    public static EqualizerEntrySnapshot CreateEntry(
        byte id,
        string name,
        IReadOnlyList<ushort> frequencies,
        IReadOnlyList<double> gains,
        sbyte minimumGain,
        sbyte maximumGain)
        => CreateEntry(
            id,
            name,
            frequencies,
            gains.Select(value => (sbyte)Math.Clamp(
                (int)Math.Round(value),
                minimumGain,
                maximumGain)).ToArray(),
            minimumGain,
            maximumGain);

    // 根据设备条目 ID 选择新增或更新操作。
    public static byte ResolveWriteAction(byte id)
        => id == 0 ? (byte)1 : (byte)2;

    // 按型号白名单频率对齐设备条目中的增益，避免窗口直接处理协议数组。
    public static IReadOnlyList<sbyte> AlignGains(
        IReadOnlyList<ushort> frequencies,
        EqualizerEntrySnapshot entry)
    {
        var gains = new sbyte[frequencies.Count];
        for (var index = 0; index < frequencies.Count; index++)
        {
            for (var candidate = 0; candidate < entry.Frequencies.Count; candidate++)
            {
                if (entry.Frequencies[candidate] != frequencies[index])
                    continue;

                if (candidate < entry.Gains.Count)
                    gains[index] = entry.Gains[candidate];
                break;
            }
        }

        return gains;
    }

    // 校验设备端自定义 EQ 名称允许使用的字符范围。
    public static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.All(character =>
            character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or >= '\u4e00' and <= '\u9fa5');
    }

    public bool Apply(ReadOnlySpan<byte> payload)
    {
        ApplicationLog.Current?.Debug("Equalizer.Protocol", $"解析自定义 EQ：bytes={payload.Length}。");
        if (payload.Length < 2 || payload[0] != 0)
        {
            ApplicationLog.Current?.Error("Equalizer.Protocol", "自定义 EQ 响应头无效。");
            return false;
        }

        var entries = new List<EqualizerEntrySnapshot>(payload[1]);
        var position = 2;
        for (var index = 0; index < payload[1]; index++)
        {
            if (position + 5 > payload.Length)
                return false;

            var selected = payload[position++] != 0;
            var minimum = unchecked((sbyte)payload[position++]);
            var maximum = unchecked((sbyte)payload[position++]);
            var id = payload[position++];
            var nameLength = payload[position++];
            if (position + nameLength + 1 > payload.Length)
                return false;

            var name = Encoding.UTF8.GetString(payload.Slice(position, nameLength)).TrimEnd('\0');
            position += nameLength;
            var bandCount = payload[position++];
            if (position + bandCount * 3 > payload.Length)
                return false;

            var frequencies = new ushort[bandCount];
            var gains = new sbyte[bandCount];
            for (var band = 0; band < bandCount; band++)
            {
                frequencies[band] = (ushort)(payload[position] | payload[position + 1] << 8);
                gains[band] = unchecked((sbyte)payload[position + 2]);
                position += 3;
            }

            entries.Add(new EqualizerEntrySnapshot(id, name, selected, minimum, maximum, frequencies, gains));
        }

        _state.SetEqualizerEntries(entries);
        ApplicationLog.Current?.Info("Equalizer.Protocol", $"自定义 EQ 解析完成：count={entries.Count}。");
        return true;
    }

    public static bool TryBuildPayload(
        byte action,
        EqualizerEntrySnapshot entry,
        out byte[] payload)
    {
        payload = [];
        if (action is < 1 or > 3 || entry.Frequencies.Count == 0 || entry.Frequencies.Count != entry.Gains.Count)
            return false;

        var name = Encoding.UTF8.GetBytes(entry.Name ?? string.Empty);
        if (name.Length > byte.MaxValue || entry.Frequencies.Count > byte.MaxValue)
            return false;

        payload = new byte[6 + name.Length + entry.Frequencies.Count * 3];
        payload[0] = action;
        payload[1] = unchecked((byte)entry.MinimumGain);
        payload[2] = unchecked((byte)entry.MaximumGain);
        payload[3] = entry.Id;
        payload[4] = (byte)name.Length;
        name.CopyTo(payload, 5);
        var position = 5 + name.Length;
        payload[position++] = (byte)entry.Frequencies.Count;
        for (var index = 0; index < entry.Frequencies.Count; index++)
        {
            var frequency = entry.Frequencies[index];
            payload[position++] = (byte)frequency;
            payload[position++] = (byte)(frequency >> 8);
            payload[position++] = unchecked((byte)entry.Gains[index]);
        }

        return true;
    }
}
