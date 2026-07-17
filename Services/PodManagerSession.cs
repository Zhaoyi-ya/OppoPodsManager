using System;

namespace OppoPodsManager;

/// <summary>拥有当前连接管理器，并在手动重连时完整替换其传输栈。</summary>
public sealed class PodManagerSession : IDisposable
{
    private readonly object _lock = new();
    private readonly Func<IPodManager> _factory;
    private readonly Action _stateChanged;
    private IPodManager _current;
    private bool _disposed;

    public PodManagerSession(Func<IPodManager> factory, Action stateChanged)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _current = CreateManager();
    }

    public IPodManager Current
    {
        get
        {
            lock (_lock)
                return _current;
        }
    }

    public bool IsCurrent(IPodManager manager)
    {
        lock (_lock)
            return ReferenceEquals(_current, manager);
    }

    public IPodManager Replace()
    {
        var replacement = CreateManager();
        IPodManager previous;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            _current = replacement;
        }

        Release(previous);
        return replacement;
    }

    public IPodManager Replace(IPodManager replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        IPodManager previous;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _current;
            _current = replacement;
            replacement.StateChanged += _stateChanged;
        }

        Release(previous);
        return replacement;
    }

    /// <summary>创建全新连接会话并释放旧会话；旧管理器的异步结果不再属于当前会话。</summary>
    public IPodManager Reset() => Replace();

    public void Dispose()
    {
        IPodManager current;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            current = _current;
        }

        Release(current);
    }

    private IPodManager CreateManager()
    {
        var manager = _factory();
        manager.StateChanged += _stateChanged;
        return manager;
    }

    private void Release(IPodManager manager)
    {
        manager.StateChanged -= _stateChanged;
        manager.Disconnect();
        manager.Dispose();
    }
}
