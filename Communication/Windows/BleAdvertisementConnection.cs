using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Windows;

// AirPods 的“连接”并不打开 RFCOMM 串口：只读状态来自 BLE 广播，因此本连接是连接概念的轻量适配——
// 构造即视为已连接，最新厂商数据从 BleAdvertisementHub 按稳定地址取用，新广播到达时通过
// AdvertisementUpdated 通知品牌层。SendAsync 抛 NotImplemented：控制通道(L2CAP)在 Windows 尚未实现。
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class BleAdvertisementConnection : IRawConnection, IAppleAdvertisementProvider
{
    private readonly BleAdvertisementHub _hub;
    private readonly string _stableId;
    private readonly EventHandler<AppleAdvertisementEventArgs> _onAdvertisement;
    private EventHandler<byte[]>? _updated;
    private bool _disposed;

    public BleAdvertisementConnection(BleAdvertisementHub hub, string stableId)
    {
        _hub = hub;
        _stableId = stableId;
        _onAdvertisement = OnHubAdvertisement;
        _hub.AdvertisementReceived += _onAdvertisement;
    }

    public bool IsConnected => true;

    public byte[]? LatestData => _hub.GetLatest(_stableId);

    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;

    public event EventHandler? Disconnected;

    event EventHandler<byte[]>? IAppleAdvertisementProvider.AdvertisementUpdated
    {
        add => _updated += value;
        remove => _updated -= value;
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        Disconnected?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        => throw new NotSupportedException("AirPods 控制通道(L2CAP/ATT)尚未在 Windows 实现，仅支持 BLE 广播只读状态。");

    private void OnHubAdvertisement(object? sender, AppleAdvertisementEventArgs e)
    {
        if (!string.Equals(e.StableId, _stableId, StringComparison.Ordinal))
            return;

        DataReceived?.Invoke(this, e.Data.AsMemory());
        _updated?.Invoke(this, e.Data);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hub.AdvertisementReceived -= _onAdvertisement;
        _updated = null;
        await Task.CompletedTask;
    }
}
