using System;
using System.Threading.Tasks;

namespace OppoPodsManager;

/// <summary>为可能忽略取消的系统异步操作提供调用方可控的硬截止时间。</summary>
public static class BoundedTaskWait
{
    public static bool Wait<T>(Task<T> task, int timeoutMs, Action cancel, out T? result)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(cancel);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        var deadline = Task.Delay(timeoutMs);
        if (ReferenceEquals(Task.WhenAny(task, deadline).GetAwaiter().GetResult(), task))
        {
            result = task.GetAwaiter().GetResult();
            return true;
        }

        cancel();
        task.ContinueWith(
            completed => _ = completed.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        result = default;
        return false;
    }
}
