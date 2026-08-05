using System.Buffers.Binary;
using System.Collections.Generic;
using OppoPodsManager.Control.Oppo.Commands;

namespace OppoPodsManager.Control.Vivo;

// 编码/增量解码 vivo Compact GAIA 帧（对应 vivo_protocol.py frame / Decoder）。
// 帧格式：FF [version] [flags] [payloadLen] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload...]
// vendor / command 均为大端 16 位；默认 flags=0、无校验、无长度扩展（payload <= 254）。
internal sealed class VivoFrameCodec : IFrameCodec
{
    private readonly List<byte> _buffer = [];
    private readonly int _noiseVersion;

    // noiseVersion：降噪命令（查询/设置）使用的 GAIA 版本，按型号画像给定
    // （TWS 3e / Air3 Pro 用 v3，家族默认用 v4）。握手与电量固定走 v4。
    public VivoFrameCodec(int noiseVersion) => _noiseVersion = noiseVersion;

    // Encode 仅接收 command + payload；vendor 与 version 按命令推断：
    //  - 握手 / 电量查询：GAIA 厂商或 VIVO 厂商 + version 4
    //  - 降噪命令：VIVO 厂商 + 画像版本（_noiseVersion）
    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        var vendor = command == VivoConstants.Handshake ? VivoConstants.GaiaVendor : VivoConstants.VivoVendor;
        var version = command == VivoConstants.Handshake || command == VivoConstants.QueryBattery
            ? 4
            : _noiseVersion;
        var frame = new byte[8 + payload.Length];
        frame[0] = VivoConstants.Preamble;
        frame[1] = (byte)version;
        frame[2] = 0; // flags
        frame[3] = (byte)payload.Length;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), vendor);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), command);
        payload.CopyTo(frame.AsSpan(8));
        return frame;
    }

    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var frames = new List<ProtocolFrame>();

        while (true)
        {
            var headerIndex = _buffer.IndexOf(VivoConstants.Preamble);
            if (headerIndex < 0)
            {
                _buffer.Clear();
                break;
            }

            if (headerIndex > 0)
                _buffer.RemoveRange(0, headerIndex);

            if (_buffer.Count < 4)
                break;

            var payloadLength = _buffer[3];
            var frameLength = 8 + payloadLength;
            if (_buffer.Count < frameLength)
                break;

            var frame = _buffer.GetRange(0, frameLength).ToArray();
            var command = BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(6, 2));
            frames.Add(new ProtocolFrame(command, frame.AsMemory(8, payloadLength)));
            _buffer.RemoveRange(0, frameLength);
        }

        return frames;
    }
}
