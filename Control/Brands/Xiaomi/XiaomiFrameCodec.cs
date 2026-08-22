using System;
using System.Collections.Generic;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Xiaomi;

// 小米 XIAOAI SPP 帧编解码（与 MiBudsClient 协议对齐）。
// 帧结构：Magic(4) + Flag(1) + Length(2,BE, = 命令1 + 负载N) + Command(1) + Payload(N) + Checksum(2)。
// 已知验证过的电池请求帧（MiBudsClient 硬编码发送）直接复用；其余命令走通用封装，校验待真机验证。
public sealed class XiaomiFrameCodec : IFrameCodec
{
    private static readonly byte[] Magic = { 0xFE, 0xDC, 0xBA, 0xC4 };
    private const byte Flag = 0x02;

    // fedcbac4 02 0005 0b ffffffff ef4f  —— MiBudsClient 实测电池请求真值帧。
    private static readonly byte[] KnownBatteryRequest =
        { 0xFE, 0xDC, 0xBA, 0xC4, 0x02, 0x00, 0x05, 0x0B, 0xFF, 0xFF, 0xFF, 0xFF, 0xEF, 0x4F };

    public byte[] Encode(ushort command, ReadOnlySpan<byte> payload)
    {
        // MVP：仅电池请求（0x0b + 4×0xff）有真值，直接复用已知帧以保证连接期可读电量。
        if (command == 0x0B && payload.Length == 4 &&
            payload[0] == 0xFF && payload[1] == 0xFF && payload[2] == 0xFF && payload[3] == 0xFF)
            return (byte[])KnownBatteryRequest.Clone();

        var body = new List<byte>(Magic);
        body.Add(Flag);
        int length = 1 + payload.Length;
        body.Add((byte)(length >> 8));
        body.Add((byte)(length & 0xFF));
        body.Add((byte)(command & 0xFF));
        body.AddRange(payload.ToArray());
        ushort crc = Crc16Ccitt(body, 4, body.Count - 4); // 校验区间：Flag 起到 Payload 末尾
        body.Add((byte)(crc >> 8));
        body.Add((byte)(crc & 0xFF));
        return body.ToArray();
    }

    public IEnumerable<ProtocolFrame> Decode(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<ProtocolFrame>();
        int i = 0;
        while (i + 7 <= bytes.Length)
        {
            if (bytes[i] != Magic[0] || bytes[i + 1] != Magic[1] ||
                bytes[i + 2] != Magic[2] || bytes[i + 3] != Magic[3])
            {
                i++;
                continue;
            }

            int length = (bytes[i + 5] << 8) | bytes[i + 6];
            int payloadLen = length - 1;
            int frameSize = 4 + 1 + 2 + 1 + payloadLen + 2; // magic + flag + len + cmd + payload + checksum
            if (i + frameSize > bytes.Length)
                break; // 分片，等下一块数据补齐

            ushort command = bytes[i + 7];
            var payload = new byte[payloadLen];
            bytes.Slice(i + 8, payloadLen).CopyTo(payload);
            frames.Add(new ProtocolFrame(command, payload));
            i += frameSize;
        }

        return frames;
    }

    // CRC16-CCITT (poly 0x1021, init 0xFFFF, 不反射, 无 XOR)——小米 XIAOAI SPP 的推测校验实现，
    // 需用真机验证（电池命令已硬编码为已知真值帧，不依赖此算法）。
    private static ushort Crc16Ccitt(List<byte> data, int start, int count)
    {
        ushort crc = 0xFFFF;
        for (int k = start; k < start + count; k++)
        {
            crc ^= (ushort)(data[k] << 8);
            for (int b = 0; b < 8; b++)
                crc = (crc & 0x8000) != 0
                    ? (ushort)(((crc << 1) ^ 0x1021) & 0xFFFF)
                    : (ushort)((crc << 1) & 0xFFFF);
        }

        return crc;
    }
}
