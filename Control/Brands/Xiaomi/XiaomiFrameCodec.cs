using System;
using System.Collections.Generic;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Xiaomi;

// 小米耳机帧编解码。
//
// 帧格式：
//   [FE DC BA] 帧头(3) + [类型 1] + [命令 1] + [长度 2, 大端] + [命令字 1 + 负载 N] + [EF] 帧尾(1)
//   长度 = 1 + 负载长度（即「命令字 + 负载」的字节数）
//
// 修正记录（2026-09-14）：
//   原实现把帧头当成 4 字节 `FE DC BA C4`，并在帧尾追加 2 字节 CRC16-CCITT；二者均不成立：
//   · 官网 APK（小米耳机 1.38.0）的 12 个 dex 中不存在任何 CRC16 查表（poly 0x1021 / 0x8005 /
//     0xA001 三种变体逐一验证），而 RCSP 类中确有 byte[]{0xFE,0xDC,0xBA} 常量 → 帧头 3 字节。
//   · 原 CRC 实现无法复现已知真值帧的尾部字节（算出 0x88A9，帧里是 0xEF4F）；对 80,652 组
//     「连续区间 × 常见 CRC/求和/异或变体」暴力搜索亦无解 → 该 2 字节不是校验，帧尾是单字节 0xEF。
//
//   原实现把第 4 字节（本处 0xC4）当作帧头的一部分，导致解码器只认类型字节恰为 0xC4 的帧。
//   实测抓包中降噪帧的类型字节为 0xC7，其它命令各不相同 —— 这些帧会被逐字节滑过而丢弃。
//   现在帧头只匹配 3 字节，类型字节不再参与识别。
//
// 已知真值帧（电量查询，MiBudsClient 实测）：
//   FE DC BA C4 02 00 05 0B FF FF FF FF EF   （13 字节）
// 本编解码器的通用路径 Encode(0x0B, {FF,FF,FF,FF}) 可逐字节复现该帧，故不再需要硬编码常量。
public sealed class XiaomiFrameCodec : IFrameCodec
{
    private static readonly byte[] Header =
        { XiaomiConstants.Header0, XiaomiConstants.Header1, XiaomiConstants.Header2 };

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        int length = 1 + payload.Length;
        var body = new byte[XiaomiConstants.Overhead + payload.Length];

        body[0] = XiaomiConstants.Header0;
        body[1] = XiaomiConstants.Header1;
        body[2] = XiaomiConstants.Header2;
        body[3] = XiaomiConstants.TypeRequest;
        body[4] = XiaomiConstants.OpCodeDefault;
        body[5] = (byte)(length >> 8);
        body[6] = (byte)(length & 0xFF);
        body[7] = (byte)(command & 0xFF);
        payload.CopyTo(body.AsSpan(XiaomiConstants.PayloadOffset));
        body[body.Length - 1] = XiaomiConstants.Footer;

        return body;
    }

    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<ProtocolFrame>();
        int i = 0;
        while (i + XiaomiConstants.Overhead <= bytes.Length)
        {
            // 帧头只比 3 字节：类型字节随命令变化（0xC4 / 0xC7 / …），不能纳入识别条件。
            if (bytes[i] != Header[0] || bytes[i + 1] != Header[1] || bytes[i + 2] != Header[2])
            {
                i++;
                continue;
            }

            int length = (bytes[i + 5] << 8) | bytes[i + 6];
            int payloadLen = length - 1;
            if (payloadLen < 0)
            {
                i++;
                continue;
            }

            int frameSize = XiaomiConstants.Overhead + payloadLen;
            if (i + frameSize > bytes.Length)
                break; // 分片，等下一块数据补齐

            // 帧尾必须是 0xEF；不匹配说明是噪声里偶然出现的 FE DC BA，继续滑动。
            if (bytes[i + frameSize - 1] != XiaomiConstants.Footer)
            {
                i++;
                continue;
            }

            ushort command = bytes[i + XiaomiConstants.CommandOffset];
            var payload = new byte[payloadLen];
            bytes.Slice(i + XiaomiConstants.PayloadOffset, payloadLen).CopyTo(payload);
            frames.Add(new ProtocolFrame(command, payload));
            i += frameSize;
        }

        return frames;
    }
}
