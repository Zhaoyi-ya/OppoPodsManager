namespace OppoPodsManager.Brands.Oppo;

public static class OppoFrameCodec
{
    public static byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > byte.MaxValue - 7)
            throw new ArgumentOutOfRangeException(nameof(payload), "Oppo SPP 帧载荷超过协议长度限制。");

        var totalLength = 7 + payload.Length;
        var frame = new byte[totalLength + 2];
        frame[0] = 0xAA;
        frame[1] = (byte)totalLength;
        frame[4] = (byte)command;
        frame[5] = (byte)(command >> 8);
        frame[6] = 0xF0;
        frame[7] = (byte)payload.Length;
        payload.CopyTo(frame.AsSpan(9));
        return frame;
    }
}
