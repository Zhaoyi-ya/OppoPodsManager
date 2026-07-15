using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;

namespace OppoPodsManager;

/// <summary>
/// Windows 连接总控。候选发现、目标锁定、传输回退、取消和总超时均由这一层统一负责。
/// 一旦选定候选地址，所有子传输只能连接该地址。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsConnectionTransport : IPodTransport
{
    private const int DefaultConnectBudgetMs = 15000;

    private readonly Func<IReadOnlyList<(ulong addr, string name)>> _discoverConnected;
    private readonly Func<IReadOnlyList<(ulong addr, string name)>> _discoverCandidates;
    private readonly Func<ulong, string, IReadOnlyList<Func<IPodTransport>>> _createAttempts;
    private readonly int _connectBudgetMs;
    private readonly object _gate = new();

    private IPodTransport? _connecting;
    private IPodTransport? _active;
    private int _generation;
    private bool _disposed;

    public WindowsConnectionTransport()
        : this(
            DeviceDiscovery.ListConnected,
            DeviceDiscovery.ListCandidates,
            CreateTargetAttempts,
            DefaultConnectBudgetMs)
    {
    }

    public WindowsConnectionTransport(
        Func<IReadOnlyList<(ulong addr, string name)>> discoverConnected,
        Func<IReadOnlyList<(ulong addr, string name)>> discoverCandidates,
        Func<ulong, string, IReadOnlyList<Func<IPodTransport>>> createAttempts,
        int connectBudgetMs = DefaultConnectBudgetMs)
    {
        _discoverConnected = discoverConnected ?? throw new ArgumentNullException(nameof(discoverConnected));
        _discoverCandidates = discoverCandidates ?? throw new ArgumentNullException(nameof(discoverCandidates));
        _createAttempts = createAttempts ?? throw new ArgumentNullException(nameof(createAttempts));
        _connectBudgetMs = Math.Max(1000, connectBudgetMs);
    }

    public string? DeviceName => _active?.DeviceName;
    public bool IsConnected => _active?.IsConnected ?? false;
    public string? LastError { get; private set; }

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    public bool Connect()
    {
        int generation;
        IPodTransport? previous;
        lock (_gate)
        {
            ThrowIfDisposed();
            previous = _active;
            _active = null;
            generation = ++_generation;
        }
        Release(previous);

        var connected = Normalize(_discoverConnected());
        var candidates = connected.Count > 0 ? connected : Normalize(_discoverCandidates());
        Log.D("WINCONNECT", connected.Count > 0
            ? $"发现 {connected.Count} 个当前已连接候选，仅尝试活动设备"
            : $"当前无活动设备，发现 {candidates.Count} 个已配对候选");

        if (candidates.Count == 0)
        {
            LastError = "未发现已连接或已配对的受支持耳机";
            return false;
        }

        var budget = Stopwatch.StartNew();
        foreach (var (addr, name) in candidates)
        {
            if (!IsCurrent(generation)) return Cancelled();
            if (budget.ElapsedMilliseconds >= _connectBudgetMs)
            {
                LastError = $"Windows 连接总超时（{_connectBudgetMs}ms）";
                return false;
            }

            var attemptFactories = _createAttempts(addr, name);
            IPodTransport? selected = null;
            var createdAttempts = new List<IPodTransport>();
            Log.D("WINCONNECT", $"目标 addr={addr:X12} name=\"{name}\"，传输尝试数={attemptFactories.Count}");
            try
            {
                foreach (var createAttempt in attemptFactories)
                {
                    if (!IsCurrent(generation)) return Cancelled();
                    if (budget.ElapsedMilliseconds >= _connectBudgetMs)
                    {
                        LastError = $"Windows 连接总超时（{_connectBudgetMs}ms）";
                        return false;
                    }

                    var attempt = createAttempt();
                    createdAttempts.Add(attempt);
                    if (!TrySetConnecting(generation, attempt))
                        return Cancelled();
                    Attach(attempt);
                    bool ok;
                    int attemptTimedOut = 0;
                    int remainingMs = (int)Math.Max(1, _connectBudgetMs - budget.ElapsedMilliseconds);
                    var deadline = new Timer(_ =>
                    {
                        Interlocked.Exchange(ref attemptTimedOut, 1);
                        Log.D("WINCONNECT", $"总预算到期，关闭 {attempt.GetType().Name} addr={addr:X12}");
                        try { attempt.Close(); } catch { }
                    }, null, remainingMs, Timeout.Infinite);
                    try
                    {
                        Log.D("WINCONNECT", $"尝试 {attempt.GetType().Name} addr={addr:X12}，总预算剩余={remainingMs}ms");
                        ok = attempt.Connect();
                    }
                    catch (Exception ex)
                    {
                        Log.Ex("WINCONNECT", attempt.GetType().Name + ".Connect", ex);
                        ok = false;
                    }
                    finally
                    {
                        using var drained = new ManualResetEvent(false);
                        if (deadline.Dispose(drained))
                            drained.WaitOne();
                    }

                    ClearConnecting(attempt);
                    if (!IsCurrent(generation)) return Cancelled();
                    if (Volatile.Read(ref attemptTimedOut) != 0)
                    {
                        LastError = $"Windows 连接总超时（{_connectBudgetMs}ms）";
                        return false;
                    }
                    if (ok)
                    {
                        lock (_gate)
                        {
                            if (generation != _generation) return Cancelled();
                            _active = attempt;
                        }
                        selected = attempt;
                        LastError = null;
                        Log.Result("WINCONNECT", "Connect", true,
                            $"addr={addr:X12} transport={attempt.GetType().Name} elapsed={budget.ElapsedMilliseconds}ms");
                        return true;
                    }

                    LastError = attempt.LastError;
                    Detach(attempt);
                    ReleaseLocked(attempt);
                    createdAttempts.Remove(attempt);
                }
            }
            finally
            {
                foreach (var attempt in createdAttempts)
                {
                    if (ReferenceEquals(attempt, selected)) continue;
                    ClearConnecting(attempt);
                    Detach(attempt);
                    ReleaseLocked(attempt);
                }
            }
        }

        LastError ??= "所有 Windows 蓝牙连接方式均失败";
        Log.Result("WINCONNECT", "Connect", false, $"elapsed={budget.ElapsedMilliseconds}ms; {LastError}");
        return false;
    }

    public void Send(ushort cmd, byte[] payload) => _active?.Send(cmd, payload);
    public void Poll(int timeoutMs) => _active?.Poll(timeoutMs);

    public void Close()
    {
        IPodTransport? connecting;
        IPodTransport? active;
        lock (_gate)
        {
            _generation++;
            connecting = _connecting;
            active = _active;
            _connecting = null;
            _active = null;
        }

        Release(connecting);
        if (!ReferenceEquals(active, connecting)) Release(active);
    }

    private static IReadOnlyList<Func<IPodTransport>> CreateTargetAttempts(ulong addr, string name)
    {
        var locator = new FixedDeviceLocator(addr, name);
        return
        [
            () => new WindowsRfcommStreamTransport(addr),
            () => new SppTransport(locator),
            () => new WindowsGattTransport(addr, name),
        ];
    }

    private static List<(ulong addr, string name)> Normalize(IReadOnlyList<(ulong addr, string name)> source) =>
        source.Where(d => d.addr != 0)
            .GroupBy(d => d.addr)
            .Select(g => g.First())
            .ToList();

    private bool IsCurrent(int generation)
    {
        lock (_gate) return !_disposed && generation == _generation;
    }

    private bool Cancelled()
    {
        LastError = "连接已取消";
        Log.D("WINCONNECT", LastError);
        return false;
    }

    private bool TrySetConnecting(int generation, IPodTransport attempt)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return false;
            _connecting = attempt;
            return true;
        }
    }

    private void ClearConnecting(IPodTransport attempt)
    {
        lock (_gate)
            if (ReferenceEquals(_connecting, attempt)) _connecting = null;
    }

    private void Attach(IPodTransport transport)
    {
        transport.FrameReceived += ForwardFrame;
        transport.Disconnected += ForwardDisconnected;
    }

    private void Detach(IPodTransport transport)
    {
        transport.FrameReceived -= ForwardFrame;
        transport.Disconnected -= ForwardDisconnected;
    }

    private void ForwardFrame(PodFrame frame) => FrameReceived?.Invoke(frame);
    private void ForwardDisconnected() => Disconnected?.Invoke();

    private void Release(IPodTransport? transport)
    {
        if (transport == null) return;
        Detach(transport);
        ReleaseLocked(transport);
    }

    private static void ReleaseLocked(IPodTransport? transport)
    {
        if (transport == null) return;
        try { transport.Close(); } catch { }
        try { transport.Dispose(); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsConnectionTransport));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Close();
    }
}
