using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

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

    private readonly HashSet<IPodTransport> _connecting = new();
    private readonly Dictionary<(ulong addr, int channel), int> _channelFailures = new();
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
        IPodTransport[] previousAttempts;
        IPodTransport? previous;
        lock (_gate)
        {
            ThrowIfDisposed();
            previous = _active;
            previousAttempts = _connecting.ToArray();
            _connecting.Clear();
            _active = null;
            generation = ++_generation;
        }
        foreach (var attempt in previousAttempts) Release(attempt);
        Release(previous);

        var candidates = Normalize(_discoverConnected());
        Log.D("WINCONNECT", $"发现 {candidates.Count} 个当前已连接候选，仅尝试活动设备");

        if (candidates.Count == 0)
        {
            LastError = "未发现当前已连接的受支持耳机";
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
            Log.D("WINCONNECT", $"目标 addr={addr:X12} name=\"{name}\"，传输尝试数={attemptFactories.Count}");
            var selected = TryConnectTarget(generation, addr, attemptFactories, budget);
            if (selected != null)
            {
                Attach(selected);
                var accepted = false;
                lock (_gate)
                {
                    if (!_disposed && generation == _generation)
                    {
                        _active = selected;
                        accepted = true;
                    }
                }
                if (!accepted)
                {
                    Detach(selected);
                    ReleaseLocked(selected);
                    return Cancelled();
                }
                LastError = null;
                Log.Result("WINCONNECT", "Connect", true,
                    $"addr={addr:X12} transport={selected.GetType().Name} elapsed={budget.ElapsedMilliseconds}ms");
                return true;
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
        IPodTransport[] connecting;
        IPodTransport? active;
        lock (_gate)
        {
            _generation++;
            connecting = _connecting.ToArray();
            active = _active;
            _connecting.Clear();
            _active = null;
        }

        foreach (var attempt in connecting) Release(attempt);
        if (active != null && !connecting.Contains(active)) Release(active);
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

    private IPodTransport? TryConnectTarget(
        int generation,
        ulong addr,
        IReadOnlyList<Func<IPodTransport>> factories,
        Stopwatch budget)
    {
        if (factories.Count == 0 || !IsCurrent(generation)) return null;

        // Windows 上实际命中率最高的是两条经典路径；GATT 仅在经典路径全部失败后兜底。
        // 工厂顺序由 CreateTargetAttempts 保证：RFCOMM、Winsock、GATT。
        var classicFactories = factories.Count > 1
            ? factories.Take(factories.Count - 1).ToArray()
            : factories.ToArray();
        var selected = TryConnectBranches(generation, addr, classicFactories, budget, 0);
        if (selected != null || factories.Count == classicFactories.Length || !IsCurrent(generation))
            return selected;

        Log.D("WINCONNECT", $"经典路径均失败，启动 RFCOMM/Winsock/GATT 全路径并行重试 addr={addr:X12}");
        return TryConnectBranches(generation, addr, factories, budget, 0);
    }

    private IPodTransport? TryConnectBranches(
        int generation,
        ulong addr,
        IReadOnlyList<Func<IPodTransport>> factories,
        Stopwatch budget,
        int channelOffset)
    {
        var available = factories
            .Select((factory, index) => (factory, channel: channelOffset + index))
            .Where(item => !IsChannelPaused(addr, item.channel))
            .ToArray();
        if (available.Length == 0) return null;

        using var raceCts = new CancellationTokenSource();
        var branches = available
            .Select(item => StartBranch(() => TrySingle(
                generation, addr, item.channel, item.factory, budget, raceCts.Token)))
            .ToArray();
        var pending = branches.ToList();

        while (pending.Count > 0 && IsCurrent(generation))
        {
            var remainingMs = (int)Math.Max(1, _connectBudgetMs - budget.ElapsedMilliseconds);
            var deadline = Task.Delay(remainingMs);
            var completed = Task.WhenAny(pending.Cast<Task>().Append(deadline)).GetAwaiter().GetResult();
            if (ReferenceEquals(completed, deadline))
            {
                LastError = $"Windows 连接总超时（{_connectBudgetMs}ms）";
                raceCts.Cancel();
                CancelAndWaitForBranches(pending, null);
                return null;
            }

            var branch = (Task<AttemptResult>)completed;
            pending.Remove(branch);
            var result = branch.GetAwaiter().GetResult();
            if (result.Transport == null)
            {
                if (!string.IsNullOrEmpty(result.Error)) LastError = result.Error;
                continue;
            }

            raceCts.Cancel();
            CancelAndWaitForBranches(pending, result.Transport);
            return result.Transport;
        }

        raceCts.Cancel();
        CancelAndWaitForBranches(pending, null);
        return null;
    }

    private AttemptResult TrySingle(
        int generation,
        ulong addr,
        int channel,
        Func<IPodTransport> factory,
        Stopwatch budget,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || !IsCurrent(generation))
            return new AttemptResult(null, "连接已取消");

        var attempt = factory();
        if (!TryAddConnecting(generation, attempt))
        {
            ReleaseLocked(attempt);
            return new AttemptResult(null, "连接已取消");
        }

        var remainingMs = (int)Math.Max(1, _connectBudgetMs - budget.ElapsedMilliseconds);
        try
        {
            Log.D("WINCONNECT", $"并行尝试 {attempt.GetType().Name} addr={addr:X12}，总预算剩余={remainingMs}ms");
            var ok = attempt.Connect();
            RemoveConnecting(attempt);
            if (ok && !cancellationToken.IsCancellationRequested && IsCurrent(generation))
                return new AttemptResult(attempt, null);

            var error = attempt.LastError;
            RemoveConnecting(attempt);
            if (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
                RecordChannelFailure(addr, channel);
            ReleaseLocked(attempt);
            return new AttemptResult(null, error);
        }
        catch (Exception ex)
        {
            var error = Log.DescribeException(ex);
            Log.Ex("WINCONNECT", attempt.GetType().Name + ".Connect", ex);
            RemoveConnecting(attempt);
            if (!cancellationToken.IsCancellationRequested && IsCurrent(generation))
                RecordChannelFailure(addr, channel);
            ReleaseLocked(attempt);
            return new AttemptResult(null, error);
        }
    }

    private bool IsChannelPaused(ulong addr, int channel)
    {
        lock (_gate)
            return _channelFailures.GetValueOrDefault((addr, channel)) > 20;
    }

    private void RecordChannelFailure(ulong addr, int channel)
    {
        lock (_gate)
        {
            var key = (addr, channel);
            var failures = _channelFailures.GetValueOrDefault(key) + 1;
            _channelFailures[key] = failures;
            if (failures == 21)
                Log.D("WINCONNECT", $"暂停设备 addr={addr:X12} 通道={channel}（失败{failures}次）");
        }
    }

    private bool TryAddConnecting(int generation, IPodTransport attempt)
    {
        lock (_gate)
        {
            if (_disposed || generation != _generation) return false;
            _connecting.Add(attempt);
            return true;
        }
    }

    private void RemoveConnecting(IPodTransport attempt)
    {
        lock (_gate) _connecting.Remove(attempt);
    }

    private void CancelConnectingExcept(IPodTransport? winner)
    {
        IPodTransport[] losers;
        lock (_gate)
            losers = _connecting.Where(attempt => !ReferenceEquals(attempt, winner)).ToArray();
        foreach (var loser in losers)
            try { loser.Close(); } catch { }
    }

    private void CancelAndWaitForBranches(IEnumerable<Task<AttemptResult>> branches, IPodTransport? winner)
    {
        var pending = branches.ToArray();
        CancelConnectingExcept(winner);
        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            foreach (var branch in pending)
                _ = branch.Exception;
        }

        foreach (var branch in pending)
        {
            if (branch.Status == TaskStatus.RanToCompletion
                && branch.Result.Transport is { } transport
                && !ReferenceEquals(transport, winner))
                Release(transport);
        }
    }

    private static Task<AttemptResult> StartBranch(Func<AttemptResult> connect) =>
        Task.Factory.StartNew(
            connect,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private sealed record AttemptResult(IPodTransport? Transport, string? Error);

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
