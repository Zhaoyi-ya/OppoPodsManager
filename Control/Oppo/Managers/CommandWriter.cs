namespace OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Logging;

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
        ApplicationLog.Current?.Debug("Protocol", $"写命令完成：command=0x{command:X4}，success={success}，responseBytes={response.Payload.Length}。");
        return success;
    }
}
