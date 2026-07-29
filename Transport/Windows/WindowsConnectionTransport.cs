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

        // 顺序尝试各传输：RFCOMM(首选) → Winsock SPP → GATT。
        // 从同一本地蓝牙射频并发发起多个 RFCOMM 连接会造成本地 RFCOMM 通道分配冲突
        // (WSAEADDRINUSE=10048 “地址已被使用”)，导致本应成功的 RFCOMM 连接失败。
        // 因此必须串行：前一个传输的 Connect() 返回并彻底释放后，再尝试下一个。
        for (int i = 0; i < factories.Count; i++)
        {
            if (!IsCurrent(generation)) return null;
            if (budget.ElapsedMilliseconds >= _connectBudgetMs)
            {
                LastError = $"Windows 连接总超时（{_connectBudgetMs}ms）";
                return null;
            }

            var transport = factories[i]();
            if (!TryAddConnecting(generation, transport))
            {
                ReleaseLocked(transport);
                continue;
            }

            var remainingMs = (int)Math.Max(1, _connectBudgetMs - budget.ElapsedMilliseconds);
            Log.D("WINCONNECT", $"顺序尝试 {transport.GetType().Name} addr={addr:X12}，预算剩余={remainingMs}ms");
            bool ok;
            try
            {
                ok = transport.Connect();
            }
            catch (Exception ex)
            {
                ok = false;
                LastError = Log.DescribeException(ex);
                Log.Ex("WINCONNECT", transport.GetType().Name + ".Connect", ex);
            }
            RemoveConnecting(transport);

            if (ok && IsCurrent(generation))
                return transport;

            if (IsCurrent(generation))
                RecordChannelFailure(addr, i);
            ReleaseLocked(transport);
        }

        LastError ??= "所有 Windows 蓝牙连接方式均失败";
        return null;
    }

    // 注：原有的并行分支逻辑（TryConnectBranches / TrySingle）已移除。
    // 从同一本地蓝牙射频并发发起多个 RFCOMM 连接会导致本地 RFCOMM 通道分配冲突
    // (WSAEADDRINUSE=10048)，使本应成功的 RFCOMM 连接失败。改为 TryConnectTarget 内串行顺序尝试。

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

    // 注：原有的 CancelAndWaitForBranches / StartBranch / AttemptResult 已随并行逻辑一并移除。
    // 串行尝试天然不存在"中途硬 Dispose 输家分支"的问题，传输自身 Connect() 返回后由
    // TryConnectTarget 统一 ReleaseLocked，不再需要并行取消协调。

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
