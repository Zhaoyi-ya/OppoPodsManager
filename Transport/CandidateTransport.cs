using System;
using System.Collections.Generic;

namespace OppoPodsManager;

/// <summary>
/// 按设备候选逐台尝试完整传输栈。候选之间隔离，某台设备的发现或连接失败
/// 不会阻止后续设备继续尝试。
/// </summary>
public sealed class CandidateTransport : IPodTransport
{
    private readonly Func<IReadOnlyList<(ulong addr, string name)>> _discover;
    private readonly Func<ulong, string, IPodTransport> _createTransport;
    private IPodTransport? _active;
    private bool _disposed;

    public CandidateTransport(
        Func<IReadOnlyList<(ulong addr, string name)>> discover,
        Func<ulong, string, IPodTransport> createTransport)
    {
        _discover = discover ?? throw new ArgumentNullException(nameof(discover));
        _createTransport = createTransport ?? throw new ArgumentNullException(nameof(createTransport));
    }

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    public string? DeviceName => _active?.DeviceName;
    public bool IsConnected => _active?.IsConnected ?? false;
    public string? LastError { get; private set; }

    public bool Connect()
    {
        ReleaseActive();
        var candidates = _discover();
        Log.D("CANDIDATE", $"发现 {candidates.Count} 个连接候选");

        for (int i = 0; i < candidates.Count; i++)
        {
            var (addr, name) = candidates[i];
            if (addr == 0) continue;

            var transport = _createTransport(addr, name);
            transport.FrameReceived += ForwardFrame;
            transport.Disconnected += ForwardDisconnected;
            Log.D("CANDIDATE", $"尝试设备 [{i + 1}/{candidates.Count}] addr={addr:X12} name=\"{name}\"");

            bool connected;
            try { connected = transport.Connect(); }
            catch (Exception ex)
            {
                Log.Ex("CANDIDATE", $"连接候选 {addr:X12}", ex);
                connected = false;
            }

            if (connected)
            {
                _active = transport;
                LastError = null;
                Log.Result("CANDIDATE", "Connect", true, $"选中 addr={addr:X12} name=\"{name}\"");
                return true;
            }

            LastError = transport.LastError;
            transport.FrameReceived -= ForwardFrame;
            transport.Disconnected -= ForwardDisconnected;
            try { transport.Dispose(); } catch { }
            Log.D("CANDIDATE", $"候选 {addr:X12} 失败，继续下一台: {LastError ?? "unknown"}");
        }

        LastError ??= candidates.Count == 0 ? "未发现已配对的受支持耳机" : "所有耳机候选均连接失败";
        Log.Result("CANDIDATE", "Connect", false, LastError);
        return false;
    }

    private void ForwardFrame(PodFrame frame) => FrameReceived?.Invoke(frame);
    private void ForwardDisconnected() => Disconnected?.Invoke();

    public void Send(ushort cmd, byte[] payload) => _active?.Send(cmd, payload);
    public void Poll(int timeoutMs) => _active?.Poll(timeoutMs);
    public void Close()
    {
        // 断开后释放活动传输，避免后续 Send/Poll 触及已关闭的链路。
        ReleaseActive();
    }

    private void ReleaseActive()
    {
        if (_active == null) return;
        _active.FrameReceived -= ForwardFrame;
        _active.Disconnected -= ForwardDisconnected;
        try { _active.Close(); } catch { }
        try { _active.Dispose(); } catch { }
        _active = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ReleaseActive();
        _disposed = true;
    }
}
