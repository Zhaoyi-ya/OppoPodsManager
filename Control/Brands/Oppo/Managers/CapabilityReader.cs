
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Core.Transport;
namespace OppoPodsManager.Control.Brands.Oppo.Managers;
// 读取设备动态命令能力，用于收敛型号静态能力表。
public sealed class CapabilityReader
{
    private readonly CommandCapabilityMap _commandMap;
    public CapabilityReader(CommandCapabilityMap commandMap)
    {
        _commandMap = commandMap;
    }
    // 请求能力位图并映射为可用命令集合。
    public async Task<CapabilityBitmap> ReadAsync(ICommandRequester requester, CancellationToken cancellationToken)
    {
        var response = await requester.RequestAsync(
            CommandId.Capabilities,
            CommandId.CapabilitiesResponse,
            Array.Empty<byte>(),
            cancellationToken);
        var result = CapabilityBitmap.Parse(response.Payload.Span, _commandMap);
        ApplicationLog.Current?.Debug(
            "Capability",
            $"0x8100 能力响应：raw={Convert.ToHexString(response.Payload.Span)}，bitmap={Convert.ToHexString(result.RawBitmap.Span)}，bits={CapabilityBitmap.FormatBits(result.RawBitmap.Span)}，always={string.Join(',', _commandMap.AlwaysSupportedCommands.OrderBy(command => command).Select(command => $"0x{command:X4}"))}，commands={string.Join(',', result.SupportedCommands.OrderBy(command => command).Select(command => $"0x{command:X4}"))}。");
        return result;
    }
    // 能力位图读取失败时仍保留官方定义的常驻命令。
    public CapabilityBitmap Empty => CapabilityBitmap.Empty(_commandMap);
}
public sealed class CommandCapabilityMap
{
    private readonly IReadOnlyDictionary<int, IReadOnlyCollection<ushort>> _commandsByBit;
    private readonly IReadOnlySet<ushort> _alwaysSupportedCommands;
    private readonly int _bitCount;
    public CommandCapabilityMap(
        IReadOnlyDictionary<int, IReadOnlyCollection<ushort>> commandsByBit,
        IReadOnlySet<ushort>? alwaysSupportedCommands = null)
    {
        _commandsByBit = commandsByBit;
        _alwaysSupportedCommands = alwaysSupportedCommands is null
            ? new HashSet<ushort>()
            : new HashSet<ushort>(alwaysSupportedCommands);
        _bitCount = _commandsByBit.Count == 0 ? 0 : _commandsByBit.Keys.Max() + 1;
    }
    public IReadOnlySet<ushort> AlwaysSupportedCommands => _alwaysSupportedCommands;
    // 按官方 bit0 到 bit66 的低位优先规则解码，并合并不依赖位图的常驻命令。
    public IReadOnlySet<ushort> Decode(ReadOnlySpan<byte> bitmap)
    {
        var commands = new HashSet<ushort>(_alwaysSupportedCommands);
        var bitCount = Math.Min(bitmap.Length * 8, _bitCount);
        for (var bit = 0; bit < bitCount; bit++)
        {
            if ((bitmap[bit / 8] & (1 << (bit % 8))) == 0)
                continue;
            if (_commandsByBit.TryGetValue(bit, out var mappedCommands))
                commands.UnionWith(mappedCommands);
        }
        return commands;
    }
    public static CommandCapabilityMap Empty { get; } = new(new Dictionary<int, IReadOnlyCollection<ushort>>());
    public static CommandCapabilityMap MelodyV16 { get; } = new(
        new Dictionary<int, IReadOnlyCollection<ushort>>
        {
            [0] = [0x0105],
            [1] = [0x0106],
            [2] = [0x0107],
            [3] = [0x0108, 0x0401, 0x0416],
            [4] = [0x0109],
            [5] = [0x0400],
            [6] = [0x0402],
            [7] = [0x0403],
            [8] = [0x010C, 0x0404],
            [9] = [0x0405],
            [10] = [0x0406, 0x010F],
            [11] = [0x0407],
            [13] = [0x0408],
            [14] = [0x0409],
            [17] = [0x0114],
            [19] = [0x040E, 0x040D, 0x0115, 0x0116],
            [20] = [0x040F],
            [21] = [0x0410, 0x0119],
            [22] = [0x0205],
            [23] = [0x0F00],
            [25] = [0x0118, 0x0411],
            [26] = [0x011A, 0x0412],
            [27] = [0x011C, 0x0413],
            [29] = [0x0112, 0x040B],
            [30] = [0x011E, 0x011F, 0x0415],
            [31] = [0x040D],
            [33] = [0x0121, 0x0417],
            [34] = [0x0122, 0x0418],
            [36] = [0x011D, 0x0414],
            [37] = [0x0123, 0x041A],
            [38] = [0x0124, 0x041B],
            [39] = [0x0125, 0x041C, 0x0127, 0x041D, 0x041F],
            [40] = [0x0421, 0x0023, 0x0024, 0x0022, 0x0126, 0x0129],
            [41] = [0xEF01],
            [42] = [0xEF02],
            [43] = [0xEF03, 0x041E],
            [44] = [0x0420],
            [45] = [0x001C],
            [47] = [0x0422, 0x012A],
            [48] = [0xEF04],
            [49] = [0x0423, 0x012B],
            [51] = [0x0424],
            [52] = [0xEF06],
            [55] = [0x0425, 0x012E, 0x0426],
            [56] = [0x012F],
            [57] = [0x0427, 0x0130],
            [58] = [0x0131, 0x0428],
            [59] = [0x0429, 0x0132],
            [60] = [0x0014],
            [61] = [0x042D, 0x0133],
            [62] = [0x042E],
            [63] = [0xEF07],
            [64] = [0xEF08],
            [65] = [0xEF09],
            [66] = [0x0431, 0x0134]
        },
        CommandId.AlwaysSupportedCommands);
}
public sealed record CapabilityBitmap(ReadOnlyMemory<byte> RawBitmap, IReadOnlySet<ushort> SupportedCommands)
{
    // 创建读取失败时仍符合官方 ProtocolManager 规则的空位图结果。
    public static CapabilityBitmap Empty(CommandCapabilityMap commandMap)
        => new(ReadOnlyMemory<byte>.Empty, commandMap.AlwaysSupportedCommands);
    // 按官方协议输出 bit0 到 bit66，便于直接和 JADX 的 Protocol 表逐位核对。
    public static string FormatBits(ReadOnlySpan<byte> bitmap)
    {
        var bitCount = Math.Min(bitmap.Length * 8, 67);
        var bits = new char[bitCount];
        for (var bit = 0; bit < bitCount; bit++)
            bits[bit] = (bitmap[bit / 8] & (1 << (bit % 8))) != 0 ? '1' : '0';
        return new string(bits);
    }
    public static CapabilityBitmap Parse(ReadOnlySpan<byte> payload, CommandCapabilityMap commandMap)
    {
        if (payload.Length < 2 || payload[0] != 0)
            return Empty(commandMap);
        var bitmap = payload[1..].ToArray();
        return new CapabilityBitmap(bitmap, commandMap.Decode(bitmap));
    }
}
