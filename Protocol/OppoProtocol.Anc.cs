using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>ANC（降噪）模式字节编码、回退匹配表与帧构造辅助。</summary>
public static partial class OppoProtocol
{
    // ========== ANC 模式字节编码 ==========
    public static readonly byte[] AncOff         = { 0x01, 0x01, 0x01 };
    public static readonly byte[] AncSmart       = { 0x01, 0x01, 0x80 };
    public static readonly byte[] AncLight       = { 0x01, 0x01, 0x40 };
    public static readonly byte[] AncMedium      = { 0x01, 0x01, 0x20 };
    public static readonly byte[] AncDeep        = { 0x01, 0x01, 0x10 };
    public static readonly byte[] AncAdaptive    = { 0x01, 0x01, 0x00, 0x08 };
    public static readonly byte[] AncTransparency = { 0x01, 0x01, 0x04 };

    /// <summary>ANC 响应字节到模式名的反向查找（静态回退表）。</summary>
    public static readonly Dictionary<(byte, byte), string> AncValues = new()
    {
        [(8, 0)]   = "Off",
        [(2, 0)]   = "Smart",
        [(0x80, 0)] = "Smart",
        [(0x40, 0)] = "Light",
        [(0x20, 0)] = "Medium",
        [(0x10, 0)] = "Deep",
        [(0, 1)]   = "Transparency",
        [(0, 2)]   = "Transparency",
        [(4, 0)]   = "Transparency",
        [(0, 8)]   = "Adaptive",
    };

    /// <summary>旧版 ANC 值交换：部分无子模式的设备 NC ↔ Transparency 对调。</summary>
    public static string LegacyAncSwap(string mode) => mode switch
    {
        "Smart" or "Light" or "Medium" or "Deep" => "Transparency",
        "Transparency" => "Smart",
        _ => mode
    };

    /// <summary>按 protocolIndex 生成 ANC 设置帧（位图 type=1，bit=protocolIndex）。</summary>
    public static byte[] PktAncByIndex(byte protocolIndex)
    {
        int byteCount = (protocolIndex / 8) + 1;
        var payload = new byte[2 + byteCount];
        payload[0] = 0x01;
        payload[1] = 0x01;
        int bitPos = protocolIndex % 8;
        int bitVal = 1;
        for (int k = 0; k < bitPos; k++) bitVal *= 2;
        payload[2 + (protocolIndex / 8)] = (byte)bitVal;
        return BuildPacket(CmdAnc, payload);
    }

    /// <summary>根据 ANC 模式名生成设置帧。</summary>
    public static byte[] PktAncMode(string mode) => mode switch
    {
        "Off"           => BuildPacket(CmdAnc, AncOff),
        "Smart"         => BuildPacket(CmdAnc, AncSmart),
        "Light"         => BuildPacket(CmdAnc, AncLight),
        "Medium"        => BuildPacket(CmdAnc, AncMedium),
        "Deep"          => BuildPacket(CmdAnc, AncDeep),
        "Adaptive"      => BuildPacket(CmdAnc, AncAdaptive),
        "Transparency"  => BuildPacket(CmdAnc, AncTransparency),
        _ => PktBattery
    };

    /// <summary>按 mode 名返回 ANC 载荷字节。</summary>
    public static byte[] AncPayloadByName(string mode) => mode switch
    {
        "Off" => AncOff,
        "Smart" => AncSmart,
        "Light" => AncLight,
        "Medium" => AncMedium,
        "Deep" => AncDeep,
        "Adaptive" => AncAdaptive,
        "Transparency" => AncTransparency,
        _ => AncOff
    };

    /// <summary>按 protocolIndex 生成 ANC 载荷。</summary>
    public static byte[] AncPayloadByIndex(byte protocolIndex)
    {
        int byteCount = (protocolIndex / 8) + 1;
        var payload = new byte[2 + byteCount];
        payload[0] = 0x01;
        payload[1] = 0x01;
        int bitPos = protocolIndex % 8;
        int bitVal = 1;
        for (int k = 0; k < bitPos; k++) bitVal *= 2;
        payload[2 + (protocolIndex / 8)] = (byte)bitVal;
        return payload;
    }

    /// <summary>
    /// 构造长按切换选中降噪模式的 setSupportNoiseReduction 载荷。
    /// 对应 Melody NoiseReductionInfo: [action=2][type][value little-endian]。
    /// </summary>
    public static byte[] LongPressNoisePayload(byte noiseType, int modeMask)
    {
        int byteCount = 1;
        for (int i = 3; i > 0; i--)
        {
            if ((modeMask & (0xFF << (i * 8))) != 0)
            {
                byteCount = i + 1;
                break;
            }
        }

        var payload = new byte[2 + byteCount];
        payload[0] = 0x02; // action=2: support noise reduction selection
        payload[1] = noiseType;
        for (int i = 0; i < byteCount; i++)
            payload[2 + i] = (byte)(modeMask >> (i * 8));
        return payload;
    }
}
