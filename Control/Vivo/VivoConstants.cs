namespace OppoPodsManager.Control.Vivo;

// vivo TWS GAIA 协议常量（HyperEars Python 移植，GPL-3.0-only）。
// 源：dev.hyperears.protocol.vivo.VivoTwsProtocol.kt
internal static class VivoConstants
{
    // vivo 私有 RFCOMM 服务 UUID（与 Kotlin VivoEarbudAdapter.VIVO_GAIA_UUID 一致）。
    public static readonly Guid VivoServiceId = new("00000837-D102-11E1-9B23-00025B00A5A5");

    public const byte Preamble = 0xFF;
    public const ushort GaiaVendor = 0x000A;
    public const ushort VivoVendor = 0x001B;

    public const ushort Handshake = 0x0300;
    public const ushort HandshakeResponse = 0x8300;

    public const ushort QueryBattery = 0x0207;
    public const ushort ReportBattery = 0x8207;

    public const ushort SetNoiseMode = 0x0130;
    public const ushort QueryNoiseMode = 0x0230;
    public const ushort AckNoiseMode = 0x8130;
    public const ushort ReportNoiseMode = 0x8230;

    // vivo 降噪模式字节（对应 vivo_protocol.NoiseMode）：ANC=0, OFF=1, TRANSPARENCY=2。
    public const byte NoiseAnc = 0;
    public const byte NoiseOff = 1;
    public const byte NoiseTransparency = 2;
}

// 型号画像：不同 vivo/iQOO 型号 GAIA 版本与降噪载荷不同（对应 vivo_protocol.Profile）。
// 源画像：Air3 Pro(v3, 空查询, [4,0])、TWS 3e(v3, 空查询, [3])、家族默认(v4, 查询[0], [3,1])。
// 握手与电量固定走 GAIA v4；仅降噪命令使用画像内的 GaiaVersion。
public sealed record VivoProfile(int GaiaVersion, byte[] NoiseQueryPayload, byte[] NoiseSetSuffix)
{
    public static readonly VivoProfile Air3ProV3 = new(3, [], [4, 0]);
    public static readonly VivoProfile Tws3eV3 = new(3, [], [3]);
    public static readonly VivoProfile FamilyDefaultV4 = new(4, [0], [3, 1]);
}
