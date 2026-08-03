using OppoPodsManager.Control.Oppo.Commands;

namespace OppoPodsManager.Control.Oppo.Managers;

// 按命令字分发协议帧，并提供可释放的短期订阅。
public sealed class FrameRouter
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, List<Action<ProtocolFrame>>> _handlers = [];

    // 注册命令响应处理器，返回值负责解除订阅。
    public IDisposable Subscribe(ushort command, Action<ProtocolFrame> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(command, out var handlers))
            {
                handlers = [];
                _handlers.Add(command, handlers);
            }

            handlers.Add(handler);
        }

        return new Subscription(this, command, handler);
    }

    // 复制当前处理器列表后调用，避免回调期间修改集合。
    public void Route(ProtocolFrame frame)
    {
        Action<ProtocolFrame>[] handlers;
        lock (_gate)
        {
            handlers = _handlers.TryGetValue(frame.Command, out var registered)
                ? [.. registered]
                : [];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(frame);
            }
            catch
            {
            }
        }
    }

    private void Unsubscribe(ushort command, Action<ProtocolFrame> handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(command, out var handlers))
                return;

            handlers.Remove(handler);
            if (handlers.Count == 0)
                _handlers.Remove(command);
        }
    }

    private sealed class Subscription(FrameRouter router, ushort command, Action<ProtocolFrame> handler) : IDisposable
    {
        private FrameRouter? _router = router;

        public void Dispose()
        {
            Interlocked.Exchange(ref _router, null)?.Unsubscribe(command, handler);
        }
    }
}
