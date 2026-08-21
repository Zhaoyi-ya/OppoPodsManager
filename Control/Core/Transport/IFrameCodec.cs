namespace OppoPodsManager.Control.Core.Transport;

// 帧编解码契约：连接层只依赖此接口，不绑定具体品牌协议实现，
// 从而让 OPPO / Vivo / Edifier 的私有帧编解码器共用同一 ConnectionLink。
public interface IFrameCodec
{
    byte[] Encode(ushort command, ReadOnlySpan<byte> payload);
    IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes);
}

public sealed record ProtocolFrame(ushort Command, ReadOnlyMemory<byte> Payload);
