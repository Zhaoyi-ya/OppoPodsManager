using System.Buffers;
using System.Buffers.Binary;

namespace OppoPodsManager.Control.Oppo.Commands;

// 编码和增量解码 OPPO 命令帧，保留跨读取块的未完成数据。
public sealed class FrameCodec
{
    private const byte Header = 0xAA;
    private const int HeaderLength = 9;
    private readonly List<byte> _buffer = [];

    // 按协议头、长度和命令字段构建可发送帧。
    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(payload));

        var bodyLength = 7 + payload.Length;
        var frame = new byte[bodyLength + 2];
        frame[0] = Header;
        frame[1] = (byte)bodyLength;
        frame[2] = 0;
        frame[3] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), command);
        frame[6] = 0xF0;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(7, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    // 接收任意数据块并尽可能提取完整帧。
    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var frames = new List<ProtocolFrame>();

        while (true)
        {
            var headerIndex = _buffer.IndexOf(Header);
            if (headerIndex < 0)
            {
                _buffer.Clear();
                break;
            }

            if (headerIndex > 0)
                _buffer.RemoveRange(0, headerIndex);

            if (_buffer.Count < 2)
                break;

            var frameLength = _buffer[1] + 2;
            if (frameLength < HeaderLength)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < frameLength)
                break;

            var frame = _buffer.GetRange(0, frameLength).ToArray();
            var command = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4, 2));
            var payloadLength = (int)BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(7, 2));
            payloadLength = Math.Min(payloadLength, frame.Length - HeaderLength);
            frames.Add(new ProtocolFrame(command, frame.AsMemory(HeaderLength, payloadLength)));
            _buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }
}

public sealed record ProtocolFrame(ushort Command, ReadOnlyMemory<byte> Payload);
