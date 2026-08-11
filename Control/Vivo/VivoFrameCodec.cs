using System.Buffers.Binary;
using System.Collections.Generic;
using OppoPodsManager.Control.Oppo.Commands;

namespace OppoPodsManager.Control.Vivo;

// 编码/增量解码 vivo Compact GAIA 帧（对应 vivo_gui.py 的 frame / Decoder）。
// 帧格式：FF [version] [flags] [payloadLen] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload...]
// vendor / command 均为大端 16 位；默认 flags=0、无校验、无长度扩展（payload <= 254）。
//
// 版本/厂商选择（对齐官方 App：Windows 逆向参考 VivoCommands.Message 对全部命令用型号画像 GAIA 版本）：
//   - 握手 0x0300                → GAIA 厂商(0x000A) + version 4（版本协商帧，恒为 v4）
//   - 其余所有命令（注册通知链 0x0202~0x0206、噪声 0x0130、电量 0x0207、EQ、查找、双连、佩戴检测…）
//                                  → VIVO 厂商(0x001B) + 型号画像 GAIA 版本(_noiseVersion)
//                                    · TWS 4 系 / TWS Air3 / iQOO TWS 2 等 = v4（真值源电量/噪声帧均为 v4）
//                                    · TWS 3e = v3，Air3 Pro = v3（Windows 参考 Tws3eV3 / Air3ProV3）
internal sealed class VivoFrameCodec : IFrameCodec
{
    private readonly List<byte> _buffer = [];

    private readonly int _noiseVersion;

    // noiseVersion = 型号画像 GAIA 版本（来自 VivoManagerFactory，见 VivoModels.SelectProfile）。
    // 除握手(恒 v4 / GAIA 厂商)外，所有命令（注册通知、噪声、电量、EQ、查找、双连、佩戴检测…）均用此版本，
    // 以对齐官方 App（Windows 逆向参考对全部命令统一用 profile.GaiaVersion）。
    public VivoFrameCodec(int noiseVersion = VivoConstants.ControlVersion)
    {
        _noiseVersion = noiseVersion;
    }

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        // GAIA 版本是「按命令」而非「按型号」——两套真实抓包已实锤：
        //   · 握手 0x0300：恒 GAIA 厂商(0x000A) + version 4（版本协商帧）。
        //   · 电量 0x0207：恒 version 4。TWS 4 抓包(FF040000001B0207) 与 Air3 Pro 真机抓包(FF040000001B0207)
        //     均为 v4，而 Air3 Pro 的噪声等控制帧是 v3 —— 证明电量查询不随型号代际变化，必须恒 v4，
        //     旧实现按型号版本发会导致旧型号电量查询被耳机忽略（"编码结果不准"的典型表现）。
    //   · 其余命令（噪声 0x0130/0x0230、双连 0x0249/0x014A、查找 0x0120、EQ 0x0118、佩戴 0x0103/0x020D、
    //     空间音频 0x0139、低延迟 0x0151、双击 0x0102、长按 0x0131 等）：用型号画像 GAIA 版本(_noiseVersion)，
        //     随代际 v4(TWS4 系) / v3(TWS3e、Air3 Pro)，与 Windows 逆向参考 VivoProfiles 一致。
        var isHandshake = command == VivoConstants.Handshake;
        var isBattery = command is VivoConstants.QueryBattery or VivoConstants.ReportBattery;
        var vendor = isHandshake ? VivoConstants.GaiaVendor : VivoConstants.VivoVendor;
        var version = (isHandshake || isBattery) ? VivoConstants.HandshakeVersion : _noiseVersion;

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
