namespace OppoPodsManager.Control.Core.Transport;

using OppoPodsManager.Control.Subsystems.Logging;

// 把"写命令 + 等待 ACK"封装为一个布尔结果：ACK 状态字节为空或首字节为 0 视为成功。
// 从 Oppo.Managers 上提到 Core，使 OPPO / Vivo 复用同一套写语义。
public sealed class CommandWriter
{
    private readonly ICommandRequester _channel;

    public CommandWriter(ICommandRequester channel)
    {
        _channel = channel;
    }

    public async Task<bool> WriteAsync(
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Debug("Protocol", $"发送写命令：command=0x{command:X4}，response=0x{responseCommand:X4}，bytes={payload.Length}。");
        var response = await _channel.RequestAsync(command, responseCommand, payload, cancellationToken);
        var success = response.Payload.IsEmpty || response.Payload.Span[0] == 0;
        if (!success && response.Payload.Length > 0)
            ApplicationLog.Current?.Debug("Protocol", $"写命令被设备拒绝：command=0x{command:X4}，status=0x{response.Payload.Span[0]:X2}，responseBytes={response.Payload.Length}。");
        ApplicationLog.Current?.Debug("Protocol", $"写命令完成：command=0x{command:X4}，success={success}，responseBytes={response.Payload.Length}。");
        return success;
    }
}
