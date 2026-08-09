using System.Buffers.Binary;
using System.Collections.Generic;
using OppoPodsManager.Control.Oppo.Commands;

namespace OppoPodsManager.Control.Vivo;

// 编码/增量解码 vivo Compact GAIA 帧（对应 vivo_gui.py 的 frame / Decoder）。
// 帧格式：FF [version] [flags] [payloadLen] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload...]
// vendor / command 均为大端 16 位；默认 flags=0、无校验、无长度扩展（payload <= 254）。
//
// 版本/厂商选择（已用官方 App HCI 抓包逐字节验证）：
//   - 握手 0x0300          → GAIA 厂商(0x000A) + version 4
//   - 注册通知链 0x0202~0x0206 → VIVO 厂商(0x001B) + version 4
//   - 其余所有控制帧        → VIVO 厂商(0x001B) + version 3
internal sealed class VivoFrameCodec : IFrameCodec
{
    private readonly List<byte> _buffer = [];

    // noiseVersion 保留用于兼容 VivoManagerFactory 调用约定；当前所有已验证控制帧统一用
    // VivoConstants.ControlVersion(3)，握手/注册通知用 HandshakeVersion(4)，不再按型号切换。
    public VivoFrameCodec(int noiseVersion = VivoConstants.ControlVersion)
    {
    }

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        var isHandshake = command == VivoConstants.Handshake;
        var isRegisterNotification =
            command is VivoConstants.RegisterNotificationsStart or
                       VivoConstants.RegisterNotificationsQuery or
                       VivoConstants.RegisterNotificationsEnable or
                       VivoConstants.RegisterNotification or
                       VivoConstants.RegisterNotificationsEnd;

        var vendor = isHandshake ? VivoConstants.GaiaVendor : VivoConstants.VivoVendor;
        var version = (isHandshake || isRegisterNotification)
            ? VivoConstants.HandshakeVersion
            : VivoConstants.ControlVersion;

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
