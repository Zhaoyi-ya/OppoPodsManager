using System;

namespace OppoPodsManager;

/// <summary>
/// 组合传输：按顺序尝试多个 IPodTransport，第一个 Connect 成功的成为活动链路，
/// 事件在构造时就绪：上层（PodManager）在 Connect 前订阅 FrameReceived/Disconnected，
/// 本类把活动子传输的事件转发出去。
/// </summary>
public sealed class FallbackTransport : IPodTransport
{
    private readonly Func<IPodTransport>[] _factories;
    private readonly object _lifecycleLock = new();
    private IPodTransport? _active;
    private IPodTransport? _connecting;
    private bool _disposed;
    private int _connectionGeneration;

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    /// <summary>按优先级传入子传输工厂。惰性创建，避免未用到的链路占资源。</summary>
    public FallbackTransport(params Func<IPodTransport>[] factories)
    {
        if (factories == null || factories.Length == 0)
            throw new ArgumentException("至少需要一个子传输工厂", nameof(factories));
        _factories = factories;
    }

    public string? DeviceName => _active?.DeviceName;
    public bool IsConnected => _active?.IsConnected ?? false;
    public string? LastError { get; private set; }

    public bool Connect()
    {
        int generation;
        lock (_lifecycleLock)
        {
            if (_disposed) return false;
            generation = _connectionGeneration;
        }

        var swAll = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < _factories.Length; i++)
        {
            var candidate = _factories[i]();
            var label = candidate.GetType().Name;
            Log.D("FALLBACK", $"尝试传输 [{i + 1}/{_factories.Length}] {label}");

            candidate.FrameReceived += ForwardFrame;
            candidate.Disconnected += ForwardDisconnected;

            lock (_lifecycleLock)
            {
                if (_disposed || generation != _connectionGeneration)
                {
                    candidate.FrameReceived -= ForwardFrame;
                    candidate.Disconnected -= ForwardDisconnected;
                    candidate.Dispose();
                    return false;
                }
                _connecting = candidate;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            try { ok = candidate.Connect(); }
            catch (Exception ex) { Log.Ex("FALLBACK", $"{label}.Connect", ex); ok = false; }

            bool cancelled;
            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_connecting, candidate)) _connecting = null;
                cancelled = _disposed || generation != _connectionGeneration;
                if (ok && !cancelled) _active = candidate;
            }

            if (ok && !cancelled)
            {
                LastError = null;
                Log.Result("FALLBACK", "Connect", true, $"活动链路={label} (该链路耗时{sw.ElapsedMilliseconds}ms, 总耗时{swAll.ElapsedMilliseconds}ms)");
                return true;
            }

            // 失败：解绑并释放，继续下一个
            LastError = candidate.LastError;
            candidate.FrameReceived -= ForwardFrame;
            candidate.Disconnected -= ForwardDisconnected;
            try { candidate.Dispose(); } catch { }
            if (cancelled)
            {
                Log.D("FALLBACK", $"{label} 连接已取消 (耗时{sw.ElapsedMilliseconds}ms)");
                return false;
            }
            Log.D("FALLBACK", $"[{i + 1}/{_factories.Length}] {label} 连接失败 (耗时{sw.ElapsedMilliseconds}ms): {candidate.LastError}");
        }

        Log.Result("FALLBACK", "Connect", false, $"所有传输均失败 (总耗时{swAll.ElapsedMilliseconds}ms; 末次原因: {LastError})");
        return false;
    }

    private void ForwardFrame(PodFrame f) => FrameReceived?.Invoke(f);
    private void ForwardDisconnected() => Disconnected?.Invoke();

    public void Send(ushort cmd, byte[] payload) => _active?.Send(cmd, payload);
    public void Poll(int timeoutMs) => _active?.Poll(timeoutMs);
    public void Close()
    {
        IPodTransport? active;
        IPodTransport? connecting;
        lock (_lifecycleLock)
        {
            _connectionGeneration++;
            active = _active;
            connecting = _connecting;
        }

        try { connecting?.Close(); } catch { }
        if (!ReferenceEquals(active, connecting))
        {
            try { active?.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { _active?.Dispose(); } catch { }
        _disposed = true;
    }
}
