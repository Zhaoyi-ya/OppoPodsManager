using System.Buffers;
using OppoPodsManager.Control.Core.Transport;
namespace OppoPodsManager.Control.Brands.Edifier;
// 漫步者帧编解码器，实现项目统一的 IFrameCodec 契约。
//
// 请求帧：AA <len> <cmd> <data...> <checksum(2, BE)>
//   - len = 1(cmd) + data 长度
//   - 校验和：sum = 8217 + 所有前导字节（AA/len/cmd/data），结果按大端追加 2 字节
//
// 响应帧：BB|CC <len> <cmd> <data...> <checksum(2)>
//   - len = 1(cmd) + data 长度
//   - 总长 = len + 4
//   - 设备回显请求命令字节（cmd），由 ConnectionLink 的 FrameRouter 按命令匹配
internal sealed class EdifierFrameCodec : IFrameCodec
{
    private readonly List<byte> _buffer = [];
    // command 取低字节作为单命令字节，payload 作为后续数据字节。
    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        var cmd = (byte)(command & 0xFF);
        var bodyLength = 1 + payload.Length;
        var frame = new byte[bodyLength + 3]; // AA + len + cmd + payload + chk(2)
        frame[0] = EdifierConstants.RequestHead;
        frame[1] = (byte)bodyLength;
        frame[2] = cmd;
        payload.CopyTo(frame.AsSpan(3));
        ushort sum = EdifierConstants.ChecksumSeed;
        for (var i = 0; i < bodyLength + 1; i++)
            sum += frame[i];
        frame[bodyLength + 1] = (byte)(sum >> 8);
        frame[bodyLength + 2] = (byte)(sum & 0xFF);
        return frame;
    }
    // 增量解码响应流，保留跨读取块的未完成数据。
    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        _buffer.AddRange(bytes.ToArray());
        var frames = new List<ProtocolFrame>();
        while (true)
        {
            // 找到首个 BB 或 CC 头。
            var headerIndex = -1;
            for (var i = 0; i < _buffer.Count; i++)
            {
                var value = _buffer[i];
                if (value == EdifierConstants.ResponseHeadBb || value == EdifierConstants.ResponseHeadCc)
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
            if (_buffer.Count < 2)
                break;
            var frameLength = _buffer[1] + 4;
            if (_buffer.Count < frameLength)
                break; // 等待更多数据
            if (!VerifyChecksum(_buffer, frameLength))
            {
                // 校验失败：丢弃一个字节后重试（与 Klinkore 跳过坏字节一致）。
                _buffer.RemoveAt(0);
                continue;
            }
            var cmd = (ushort)_buffer[2];
            var dataLength = Math.Max(0, _buffer[1] - 1);
            var payload = _buffer.GetRange(3, dataLength).ToArray();
            frames.Add(new ProtocolFrame(cmd, payload));
            _buffer.RemoveRange(0, frameLength);
        }
        return frames;
    }
    // 校验帧校验和：对除最后 2 字节外的所有字节累加 8217，结果应等于末尾 2 字节（大端）。
    private static bool VerifyChecksum(List<byte> buffer, int frameLength)
    {
        ushort sum = EdifierConstants.ChecksumSeed;
        for (var i = 0; i < frameLength - 2; i++)
            sum += buffer[i];
        var expectedHigh = (byte)(sum >> 8);
        var expectedLow = (byte)(sum & 0xFF);
        return buffer[frameLength - 2] == expectedHigh && buffer[frameLength - 1] == expectedLow;
    }
}
