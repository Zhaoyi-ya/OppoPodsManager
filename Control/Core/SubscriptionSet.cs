namespace OppoPodsManager.Control.Core;

using System;
using System.Collections.Generic;

// 统一管理的 IDisposable 订阅集合：连接释放时一次性 Dispose 所有订阅，避免重复手写
// foreach + Clear 的样板，并防止旧连接的订阅泄漏到下一次会话。
public sealed class SubscriptionSet : IDisposable
{
    private readonly List<IDisposable> _items = new();
    private bool _disposed;

    public void Add(IDisposable subscription)
        => _items.Add(subscription);

    public void DisposeAll()
    {
        foreach (var item in _items)
            item.Dispose();
        _items.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeAll();
    }
}
