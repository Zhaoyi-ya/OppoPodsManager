namespace OppoPodsManager.Control.Core.Transport;

using System;
using System.Threading;
using System.Threading.Tasks;

// 统一请求/写命令的超时与异常归一化：把"通道超时 / 断开 / 取消"等传输故障转换为可恢复的
// 失败（写返回 false / 读返回 null），避免每个品牌管理器重复编写相同的 try/catch 模板。
// 语义与 OPPO 原 WriteAsync / TryRequestAsync 完全一致。
public static class CommandSender
{
    private static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DefaultReadTimeout = TimeSpan.FromSeconds(2);

    // 写命令：超时 / 断开 / 取消（非用户主动）均视为可恢复的失败，返回 false。
    public static async Task<bool> WriteAsync(
        ConnectionLink link,
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultWriteTimeout);
        try
        {
            return await new CommandWriter(link).WriteAsync(command, responseCommand, payload, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // 读取并等待响应：超时 / 断开 / 取消（非用户主动）均返回 null，由调用方决定是否重试。
    public static async Task<ProtocolFrame?> RequestAsync(
        ConnectionLink link,
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout ?? DefaultReadTimeout);
        try
        {
            return await link.RequestAsync(command, responseCommand, payload, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
