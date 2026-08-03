using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control;

// 负责从控制管理器取得当前会话并执行前端发起的设备命令。
public sealed class CommandDispatcher
{
    private readonly ControlManager _controlManager;
    private readonly ApplicationLog _log;

    public CommandDispatcher(ControlManager controlManager, ApplicationLog log)
    {
        _controlManager = controlManager;
        _log = log;
    }

    // 执行设备命令并统一记录跳过、成功和失败结果。
    public async Task<bool> RunAsync(string operation, Func<IBrandManager, Task<bool>> command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(command);

        var manager = _controlManager.ActiveManager;
        if (manager is null)
        {
            _log.Debug("Control", $"忽略操作：没有活动设备会话，operation={operation}。");
            return false;
        }

        try
        {
            var success = await command(manager);
            _log.Info("Control", $"操作完成：operation={operation}，success={success}。");
            return success;
        }
        catch (Exception exception)
        {
            _log.Error("Control", $"操作失败：operation={operation}。", exception);
            return false;
        }
    }
}
