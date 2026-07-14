using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace OppoPodsManager;

/// <summary>命令发送结果状态码（0=成功，0x6=超时，其余为设备返回的错误码）。</summary>
public enum CmdStatus
{
    Success = 0,
    Timeout = 0x6,   // PacketTimeoutProcessor 超时回送的状态字节
    Failed = 0xFF,   // 链路断开/未连接等本地失败
}

/// <summary>
/// 命令分发器：在 IPodTransport 之上实现带重试/超时的命令发送（类似 PacketTimeoutProcessor）：
///   · 每条"需要响应"的命令注册 10s 超时；
///   · 响应按 (TRANS_ID, 基础 cmd) 配对（响应 cmd = 请求 cmd | 0x8000）；
///   · 超时按重试次数重发，用尽则以 0x6 状态回调（超时标志字节）；
///   · 链路断开时所有挂起命令以 Failed 回调。
/// 本类是 IPodTransport 装饰器：普通 Send/Poll/Connect 原样转发；SendTracked 才走队列。
/// 采用"基础 cmd"配对（本机 SPP seq 固定，取 cmd 低 15 位作 key），
/// 我方串行发送不会同 cmd 并发，配对稳定。
/// </summary>
public sealed class PacketDispatcher : IPodTransport
{
    private const int DefaultTimeoutMs = 10000;   // 每命令超时 10s
    private const int SweepIntervalMs = 500;      // 超时扫描周期

    /// <summary>一条待完成的命令（请求-响应配对）。</summary>
    private sealed class Pending
    {
        public ushort Cmd;                 // 基础 cmd（不含 0x8000）
        public byte[] Payload = Array.Empty<byte>();
        public Action<CmdStatus, PodFrame?>? OnResult;  // 完成回调
        public long DeadlineTicks;         // Environment.TickCount64 到期时刻
        public int RetriesLeft;            // 剩余重试次数
        public int TimeoutMs;              // 超时毫秒
    }

    private readonly IPodTransport _inner;              // 被装饰的底层传输
    private readonly object _lock = new();               // _pending 保护锁
    private readonly Dictionary<ushort, Queue<Pending>> _pending = new(); // 基础 cmd → FIFO（队首为在途项）
    private Timer? _sweeper;                             // 超时扫描定时器
    private bool _disposed;

    /// <summary>装饰指定传输层：绑定其 FrameReceived/Disconnected 事件。</summary>
    public PacketDispatcher(IPodTransport inner)
    {
        _inner = inner;
        _inner.FrameReceived += OnInnerFrame;
        _inner.Disconnected += OnInnerDisconnected;
    }

    // ===== IPodTransport 透传（以下方法原样转发给 _inner）=====
    public string? DeviceName => _inner.DeviceName;
    public bool IsConnected => _inner.IsConnected;
    public string? LastError => _inner.LastError;
    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    /// <summary>连接底层传输，成功后启动超时扫描定时器。</summary>
    public bool Connect()
    {
        var ok = _inner.Connect();
        if (ok) EnsureSweeper();
        return ok;
    }

    /// <summary>透传发送（不经过超时跟踪；快速轮询类命令走此路径）。</summary>
    public void Send(ushort cmd, byte[] payload) => _inner.Send(cmd, payload);

    /// <summary>透传轮询。</summary>
    public void Poll(int timeoutMs) => _inner.Poll(timeoutMs);

    /// <summary>关闭连接并失败所有挂起命令。</summary>
    public void Close()
    {
        FailAllPending(CmdStatus.Failed);
        _inner.Close();
    }

    /// <summary>
    /// 发送一条"期望响应"的命令并注册超时/重试。收到匹配响应或超时后回调。
    /// onResult(status, respFrame)：status=Success 时 respFrame 为响应帧；超时/失败时为 null。
    /// </summary>
    public void SendTracked(ushort cmd, byte[] payload, Action<CmdStatus, PodFrame?>? onResult = null,
                            int retries = 1, int timeoutMs = DefaultTimeoutMs)
    {
        var baseCmd = (ushort)(cmd & 0x7FFF);
        bool sendNow;
        var pending = new Pending
        {
            Cmd = baseCmd,
            Payload = payload,
            OnResult = onResult,
            RetriesLeft = retries,
            TimeoutMs = timeoutMs,
        };
        lock (_lock)
        {
            if (!_pending.TryGetValue(baseCmd, out var queue))
            {
                queue = new Queue<Pending>();
                _pending[baseCmd] = queue;
            }
            sendNow = queue.Count == 0;
            queue.Enqueue(pending);
            if (sendNow)
                pending.DeadlineTicks = Environment.TickCount64 + timeoutMs;
        }
        EnsureSweeper();
        if (sendNow) _inner.Send(cmd, payload);
    }

    private void OnInnerFrame(PodFrame frame)
    {
        // 响应帧（第 15 位置位）：按基础 cmd 配对并完成挂起项
        if ((frame.Cmd & 0x8000) != 0)
        {
            var baseCmd = (ushort)(frame.Cmd & 0x7FFF);
            Pending? p = null;
            Pending? next = null;
            lock (_lock)
            {
                if (_pending.TryGetValue(baseCmd, out var queue) && queue.Count > 0)
                {
                    p = queue.Dequeue();
                    if (queue.Count == 0)
                        _pending.Remove(baseCmd);
                    else
                    {
                        next = queue.Peek();
                        next.DeadlineTicks = Environment.TickCount64 + next.TimeoutMs;
                    }
                }
            }
            if (p != null)
            {
                // 响应载荷首字节为状态码（CommandUtil.j）；空载荷视为成功
                var status = (frame.Payload != null && frame.Payload.Length > 0)
                    ? (CmdStatus)frame.Payload[0] : CmdStatus.Success;
                try { p.OnResult?.Invoke(status == 0 ? CmdStatus.Success : status, frame); }
                catch (Exception ex) { Log.Ex("DISPATCH", "OnResult", ex); }
            }
            if (next != null)
            {
                Log.D("DISPATCH", $"发送队列中的下一条命令 cmd=0x{next.Cmd:X4}");
                try { _inner.Send(next.Cmd, next.Payload); }
                catch (Exception ex) { Log.Ex("DISPATCH", "发送队列下一条", ex); }
            }
        }
        // 无论是否配对，都把帧继续交给上层解析（DispatchFrame）
        FrameReceived?.Invoke(frame);
    }

    private void OnInnerDisconnected()
    {
        FailAllPending(CmdStatus.Failed);
        Disconnected?.Invoke();
    }

    private void EnsureSweeper()
    {
        if (_sweeper != null || _disposed) return;
        _sweeper = new Timer(_ => Sweep(), null, SweepIntervalMs, SweepIntervalMs);
    }

    /// <summary>超时扫描：到期项按剩余次数重发，用尽则以 0x6 回调（超时标志）。</summary>
    private void Sweep()
    {
        var now = Environment.TickCount64;
        List<Pending>? retries = null;
        List<Pending>? timeouts = null;

        lock (_lock)
        {
            if (_pending.Count == 0) return;
            foreach (var kv in new List<KeyValuePair<ushort, Queue<Pending>>>(_pending))
            {
                var queue = kv.Value;
                if (queue.Count == 0) continue;
                var p = queue.Peek();
                if (now < p.DeadlineTicks) continue;
                if (p.RetriesLeft > 0)
                {
                    p.RetriesLeft--;
                    p.DeadlineTicks = now + p.TimeoutMs;
                    (retries ??= new()).Add(p);
                }
                else
                {
                    queue.Dequeue();
                    (timeouts ??= new()).Add(p);
                    if (queue.Count == 0)
                        _pending.Remove(kv.Key);
                    else
                    {
                        var next = queue.Peek();
                        next.DeadlineTicks = now + next.TimeoutMs;
                        (retries ??= new()).Add(next);
                    }
                }
            }
        }

        if (retries != null)
            foreach (var p in retries)
            {
                Log.D("DISPATCH", $"发送/重发命令 cmd=0x{p.Cmd:X4}（剩余重试 {p.RetriesLeft}）");
                try { _inner.Send(p.Cmd, p.Payload); } catch (Exception ex) { Log.Ex("DISPATCH", "重发", ex); }
            }

        if (timeouts != null)
            foreach (var p in timeouts)
            {
                Log.D("DISPATCH", $"命令超时 cmd=0x{p.Cmd:X4} status=0x6");
                try { p.OnResult?.Invoke(CmdStatus.Timeout, null); }
                catch (Exception ex) { Log.Ex("DISPATCH", "超时回调", ex); }
            }
    }

    private void FailAllPending(CmdStatus status)
    {
        List<Pending> all;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            all = _pending.Values.SelectMany(queue => queue).ToList();
            _pending.Clear();
        }
        foreach (var p in all)
        {
            try { p.OnResult?.Invoke(status, null); }
            catch (Exception ex) { Log.Ex("DISPATCH", "失败回调", ex); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _sweeper?.Dispose(); } catch { }
        _sweeper = null;
        FailAllPending(CmdStatus.Failed);
        _inner.FrameReceived -= OnInnerFrame;
        _inner.Disconnected -= OnInnerDisconnected;
        _inner.Dispose();
    }
}
