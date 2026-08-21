using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Huawei;

// 华为私有帧编解码器，实现项目统一的 IFrameCodec 契约。
//
// 帧格式（无分包，单帧收发，参考 HuaweiPods 的 HuaweiRfcommResponseParser）：
//   [5A 00][len_lo][len_hi][service][command][TLV...][crc_hi][crc_lo]
//   - 魔数 5A 00；len（uint16 LE）从 command 字节起算（command + TLV + CRC2），即 TLV 长度 + 3
//   - service/command 编码为 ushort（service << 8 | command），供 FrameRouter 按命令字匹配
//   - TLV 序列：[type(1)][len(1)][value(len)...]
//   - CRC16/XMODEM（poly=0x1021，init=0x0000），覆盖从 0x5A 到 TLV 末尾，大端追加 2 字节
//
// 已对照华为参考项目全部抓包封包验证 CRC 算法：11/11 全部匹配
// （如电量查询 5A0009000108010002000300→FBB9、ANC 查询 5A0005002B2A0100→427E、佩戴关
//  5A0006002B10010100→B977、ANC 档位 5A0007002B0401020101→F13D 等）。
internal sealed class HuaweiFrameCodec : IFrameCodec
{
    private readonly List<byte> _buffer = [];

    // command 高字节为 service、低字节为 command；payload 为 TLV 序列。
    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        var service = (byte)(command >> 8);
        var cmd = (byte)(command & 0xFF);
        var length = payload.Length + 3; // command + TLV + CRC
        var frame = new byte[HuaweiConstants.HeaderSize + payload.Length + HuaweiConstants.CrcSize];
        frame[0] = HuaweiConstants.MagicHigh;
        frame[1] = HuaweiConstants.MagicLow;
        frame[2] = (byte)(length & 0xFF);
        frame[3] = (byte)(length >> 8);
        frame[4] = service;
        frame[5] = cmd;
        payload.CopyTo(frame.AsSpan(HuaweiConstants.HeaderSize));
        var crc = Crc16Xmodem(frame.AsSpan(0, HuaweiConstants.HeaderSize + payload.Length));
        frame[^2] = (byte)(crc >> 8);
        frame[^1] = (byte)(crc & 0xFF);
        return frame;
    }

    // 增量解码响应流，保留跨读取块的未完成数据；损坏帧按单字节滑动跳过（与漫步者一致）。
    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var frames = new List<ProtocolFrame>();
        while (true)
        {
            // 扫描 5A 00 魔数。
            var headerIndex = -1;
            for (var i = 0; i + 1 < _buffer.Count; i++)
            {
                if (_buffer[i] == HuaweiConstants.MagicHigh && _buffer[i + 1] == HuaweiConstants.MagicLow)
                {
                    headerIndex = i;
                    break;
                }
            }
            if (headerIndex < 0)
            {
                _buffer.Clear();
                break;
            }
            if (headerIndex > 0)
                _buffer.RemoveRange(0, headerIndex);
            if (_buffer.Count < HuaweiConstants.HeaderSize)
                break; // 头部未齐，等待更多数据
            var payloadLength = _buffer[2] | (_buffer[3] << 8);
            var frameSize = HuaweiConstants.HeaderSize + payloadLength;
            if (frameSize <= HuaweiConstants.HeaderSize || _buffer.Count < frameSize)
                break; // 帧未完整，等待更多数据
            if (!VerifyCrc(_buffer, frameSize))
            {
                _buffer.RemoveAt(0);
                continue;
            }
            var command = (ushort)((_buffer[4] << 8) | _buffer[5]); // service << 8 | command
            var tlvLength = payloadLength - 3; // 扣除 command 与 CRC 2 字节
            var payload = _buffer.GetRange(HuaweiConstants.HeaderSize, tlvLength).ToArray();
            frames.Add(new ProtocolFrame(command, payload));
            _buffer.RemoveRange(0, frameSize);
        }
        return frames;
    }

    private static bool VerifyCrc(List<byte> buffer, int frameSize)
    {
        var crc = Crc16Xmodem(buffer.GetRange(0, frameSize - HuaweiConstants.CrcSize).ToArray());
        return buffer[frameSize - 2] == (byte)(crc >> 8) && buffer[frameSize - 1] == (byte)(crc & 0xFF);
    }

    // CRC16/XMODEM：poly=0x1021，init=0x0000（与 HuaweiPods crc16Xmodem 一致）。
    internal static ushort Crc16Xmodem(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0;
        foreach (var value in bytes)
        {
            crc ^= (ushort)(value << 8);
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
