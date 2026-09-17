using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Brands.Xiaomi;

// 小米 RCSP「设备配置」通道：0xF2 写 / 0xF3 读 / 0xF4 通知。
//
// 这是小米控制类功能（降噪 / EQ / 低延迟 / 多点连接 / 空间音频 / 查找设备）的**唯一入口**：
// 打通本通道后，各功能只是「填配置 ID + 值」的差别，不需要再逆向新协议。
//
// ── 帧布局（2026-09-17 修正）───────────────────────────────────────────────
// 三帧真值逐字节对齐后得出（电量 / 低延迟写 / 认证结果）：
//
//   [0-2] FE DC BA      帧头
//   [3]   类型字节       电量与配置用 0xC4；认证结果用 0x04；另有降噪帧 0xC7（语义未确认）
//   [4]   RCSP opCode   **真正的命令字**：电量 0x02 / 配置写 0xF2 / 配置读 0xF3 / 认证结果 0x51
//   [5-6] 长度（大端）   = 1 + 负载长度（那个 1 是下面的 seq 字节）
//   [7]   seq           请求序号：电量 0x0B / 低延迟 0x90 / 认证结果 0x00
//   [8+]  负载
//   [末]  EF            帧尾
//
// 依据（均为真值帧，非推测）：
//   电量     FE DC BA C4 02 0005 0B FFFFFFFF EF        → 长度 5 = 1 + 4 ✓
//   低延迟写 FE DC BA C4 F2 0005 90 03 00 2F 01 EF    → 长度 5 = 1 + 4 ✓（负载 03 00 2F 01）
//   认证结果 FE DC BA 04 51 0003 00 03 01 EF          → 长度 3 = 1 + 2 ✓（负载 03 01）
//
// ⚠️ 注意与既有实现的差异：`XiaomiFrameCodec.Encode` 把命令字写在 byte[7]、
//    并把 byte[4] 固定为 `OpCodeDefault(0x02)`。该布局**只对电量帧成立**（因为电量帧的
//    byte[4] 恰好就是 0x02），一旦要发 0xF2/0xF3 就会错位成 `... C4 02 0005 F2 ...`。
//    因此本通道**自行构造完整帧**并通过 `ConnectionLink.SendRawAsync` 发送，
//    不改动 Encode（避免影响已在用的电量通路）。
//
// ── 负载布局（写 / 读）────────────────────────────────────────────────────
//   写： [seq 1B] [03] [configId 2B 大端] [value 1B]
//   读： [seq 1B] [02] [configId 2B 大端]
//   其中 0x03 / 0x02 = 其后字节数（写为 configId 2 + value 1 = 3；读为 configId 2 = 2）
//
//   依据：外部实现 CesurPolat/MiBudsClient（GPL-3.0）的常量
//     MODE_COMMAND_TEMPLATE = "fedcbac4f20005{counter}03002f{param}ef"
//   其 0xF2 = CmdSetDeviceConfig(242)、0x2F = LowLatency(47)，与本项目 XiaomiConstants
//   的取值**双重吻合**。
//
// ── 应答识别（重要取舍）──────────────────────────────────────────────────
// 设备应答帧的「命令字字节」位置尚未在真机确认（XiaomiManager 的认证流程也因同样的
// 不确定性改用了「按负载形状匹配」）。故此处同样**不订阅命令字**，而是：
//   · 累积 RawDataReceived 的字节流 → XiaomiFrameCodec.Decode 切帧
//   · 按「负载里含目标 configId」匹配（读）
//   · 写命令只等一个短窗口收 ack，不强制匹配内容
// 收到的每一帧都会打进日志（含十六进制 payload），便于真机首测时直接读证据。
//
// ⚠️ 发送走 ConnectionLink.SendRawAsync（不经 _requestGate、也不经 FrameCodec）——
//    这是**必需**的：XiaomiFrameCodec.Encode 的字节布局对配置帧不成立（见上）。
//    代价是它与 RequestAsync 的发送没有互斥。本工程里配置写只由 UI 操作触发（单线程、串行），
//    风险可接受；若将来出现后台轮询写配置，需改为经 _requestGate 串行化。
internal sealed class XiaomiConfigChannel
{
    /// <summary>默认应答窗口。设备通常几十毫秒内回，窗口过长会让 UI 操作有拖沓感。</summary>
    private static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(800);

    /// <summary>缓冲上限：异常流下避免无上限增长。</summary>
    private const int BufferCeiling = 2048;

    // 请求序号。外部实现固定用 0x90；此处改为递增，便于在日志里区分并发请求。
    // 读到哪个值由设备决定，序号本身不影响语义（真值帧里 0x0B / 0x90 / 0x00 都出现过）。
    private byte _seq = XiaomiConstants.ConfigSeqStart;

    private byte NextSeq()
    {
        var value = _seq;
        _seq = _seq == byte.MaxValue ? XiaomiConstants.ConfigSeqStart : (byte)(_seq + 1);
        return value;
    }

    // ---- 帧构造 ----

    /// <summary>构造一个完整的小米帧。长度字段 = 1(seq) + 负载长度。</summary>
    internal static byte[] BuildFrame(byte type, ushort opCode, byte seq, ReadOnlySpan<byte> payload)
    {
        // Overhead(=9) 已含「帧头3 + 类型1 + opCode1 + 长度2 + seq1 + 帧尾1」，再加负载即为总长。
        var frame = new byte[XiaomiConstants.Overhead + payload.Length];
        frame[0] = XiaomiConstants.Header0;
        frame[1] = XiaomiConstants.Header1;
        frame[2] = XiaomiConstants.Header2;
        frame[3] = type;
        frame[4] = (byte)(opCode & 0xFF);
        var length = 1 + payload.Length;
        frame[5] = (byte)(length >> 8);
        frame[6] = (byte)(length & 0xFF);
        frame[7] = seq;
        payload.CopyTo(frame.AsSpan(XiaomiConstants.PayloadOffset));
        frame[^1] = XiaomiConstants.Footer;
        return frame;
    }

    /// <summary>读配置负载：<c>[seq][0x02][configId 2B 大端]</c>。</summary>
    internal static byte[] BuildReadPayload(byte seq, int configId) => new[]
    {
        seq,
        XiaomiConstants.ConfigReadMarker,
        (byte)((configId >> 8) & 0xFF),
        (byte)(configId & 0xFF)
    };

    /// <summary>写配置负载：<c>[seq][0x03][configId 2B 大端][value]</c>。</summary>
    internal static byte[] BuildWritePayload(byte seq, int configId, byte value) => new[]
    {
        seq,
        XiaomiConstants.ConfigWriteMarker,
        (byte)((configId >> 8) & 0xFF),
        (byte)(configId & 0xFF),
        value
    };

    // ---- 读写 ----

    /// <summary>读配置项。返回设备上报的值；无应答或格式不符返回 null。</summary>
    public async Task<byte?> ReadAsync(
        ConnectionLink link,
        int configId,
        CancellationToken cancellationToken,
        TimeSpan? window = null)
    {
        var seq = NextSeq();
        var payload = BuildReadPayload(seq, configId);
        var frame = BuildFrame(XiaomiConstants.TypeRequest, XiaomiConstants.CmdGetDeviceConfig, seq, payload);

        var match = await SendAndAwaitAsync(link, frame, configId, window, cancellationToken);
        if (match is null)
            return null;

        if (TryExtractValue(match.Payload.Span, configId, out var value))
            return value;

        ApplicationLog.Current?.Info("Xiaomi",
            $"配置读取应答形状不符（configId={configId}，payload={ToHex(match.Payload.Span)}），原文已记入日志。");
        return null;
    }

    /// <summary>写配置项。返回是否收到应答；<paramref name="ackReceived"/> 为 false 表示无应答（不代表一定失败）。</summary>
    public async Task<bool> WriteAsync(
        ConnectionLink link,
        int configId,
        byte value,
        CancellationToken cancellationToken,
        TimeSpan? window = null)
    {
        var seq = NextSeq();
        var payload = BuildWritePayload(seq, configId, value);
        var frame = BuildFrame(XiaomiConstants.TypeRequest, XiaomiConstants.CmdSetDeviceConfig, seq, payload);

        var match = await SendAndAwaitAsync(link, frame, configId, window, cancellationToken);
        return match is not null;
    }

    // ---- 内部：发送 + 收帧 ----

    private static async Task<ProtocolFrame?> SendAndAwaitAsync(
        ConnectionLink link,
        byte[] frame,
        int configId,
        TimeSpan? window,
        CancellationToken cancellationToken)
    {
        var codec = new XiaomiFrameCodec();
        var buffer = new List<byte>();
        var completion = new TaskCompletionSource<ProtocolFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnRawData(object? sender, ReadOnlyMemory<byte> raw)
        {
            if (completion.Task.IsCompleted)
                return;

            buffer.AddRange(raw.ToArray());
            foreach (var candidate in codec.Decode(buffer.ToArray()))
            {
                // 全部入日志：真机首测时这段就是最直接的证据来源。
                ApplicationLog.Current?.Debug("Xiaomi",
                    $"配置通道收帧：cmd=0x{candidate.Command:X2}，payload({candidate.Payload.Length})={ToHex(candidate.Payload.Span)}。");

                // 应答不一定回显 configId（未确认），因此只要求「不是空帧」即视为候选，
                // 优先选带目标 configId 的那一帧。
                if (ContainsConfigId(candidate.Payload.Span, configId))
                {
                    completion.TrySetResult(candidate);
                    return;
                }
            }

            if (buffer.Count > BufferCeiling)
                buffer.RemoveRange(0, buffer.Count - 512);
        }

        link.RawDataReceived += OnRawData;
        try
        {
            ApplicationLog.Current?.Info("Xiaomi",
                $"配置通道发送：opCode=0x{frame[4]:X2}，configId={configId}，frame={ToHex(frame)}。");
            await link.SendRawAsync(frame, cancellationToken);

            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(window ?? DefaultWindow);
            try
            {
                return await completion.Task.WaitAsync(probe.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            ApplicationLog.Current?.Error("Xiaomi", $"配置通道发送失败（configId={configId}）：{ex.Message}", ex);
            return null;
        }
        finally
        {
            link.RawDataReceived -= OnRawData;
        }
    }

    /// <summary>负载里是否含目标 configId（大端 2 字节）。</summary>
    private static bool ContainsConfigId(ReadOnlySpan<byte> payload, int configId)
    {
        var hi = (byte)((configId >> 8) & 0xFF);
        var lo = (byte)(configId & 0xFF);
        for (var i = 0; i + 1 < payload.Length; i++)
        {
            if (payload[i] == hi && payload[i + 1] == lo)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 从应答负载里取值。
    /// 期望形状：<c>[seq][0x02|[0x03]][configId 2B 大端][value]</c>（与请求同构），
    /// 因此定位到 configId 后取其后一字节即值。
    /// </summary>
    private static bool TryExtractValue(ReadOnlySpan<byte> payload, int configId, out byte value)
    {
        value = 0;
        var hi = (byte)((configId >> 8) & 0xFF);
        var lo = (byte)(configId & 0xFF);
        for (var i = 0; i + 2 < payload.Length; i++)
        {
            if (payload[i] != hi || payload[i + 1] != lo)
                continue;
            value = payload[i + 2];
            return true;
        }
        return false;
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
        => bytes.Length == 0 ? "(空)" : Convert.ToHexString(bytes);

    /// <summary>
    /// 连接后的认证结果上报（opCode 0x51 = CmdAuthSendCalcResult，负载 <c>[0x03, 0x01]</c>）。
    /// 依据：外部实现 MiBudsClient 在连接建立后固定发送 <c>fedcba04510003000301ef</c>。
    /// 失败静默忽略 —— 该步骤是否必需尚未确认（本项目的挑战/应答认证已单独完成）。
    /// </summary>
    public async Task TrySendAuthResultAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var frame = BuildFrame(
                XiaomiConstants.TypeAuthResult,
                XiaomiConstants.CmdAuthSendCalcResult,
                XiaomiConstants.AuthResultSeq,
                XiaomiConstants.AuthResultPayload);
            ApplicationLog.Current?.Debug("Xiaomi", $"上报认证结果：frame={ToHex(frame)}。");
            await link.SendRawAsync(frame, cancellationToken);
        }
        catch (Exception ex)
        {
            ApplicationLog.Current?.Debug("Xiaomi", $"上报认证结果失败（忽略）：{ex.Message}");
        }
    }
}
