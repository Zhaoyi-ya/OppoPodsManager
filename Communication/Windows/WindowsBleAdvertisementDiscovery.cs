using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Windows;

// BLE 广播发现器：把 BleAdvertisementHub 中的 Apple 设备转换成 DeviceCandidate，
// 仅在“设备集合发生变化（新增/移除）”时向上抛出 DevicesChanged，避免每次电量广播都触发重排。
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsBleAdvertisementDiscovery : IDeviceDiscoveryMonitor
{
    private readonly BleAdvertisementHub _hub;
    private readonly EventHandler<AppleAdvertisementEventArgs> _onAdvertisement;
    private HashSet<string> _lastStableIds = new(StringComparer.Ordinal);
    private long _generation;
    private bool _disposed;

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public WindowsBleAdvertisementDiscovery(BleAdvertisementHub hub)
    {
        _hub = hub;
        _onAdvertisement = OnHubAdvertisement;
        _hub.AdvertisementReceived += _onAdvertisement;
    }

    public Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var candidates = _hub.Snapshot()
            .Select(entry => new DeviceCandidate(
                entry.Key,
                entry.Key,
                entry.Key.StartsWith("BLE:", StringComparison.Ordinal) ? entry.Key["BLE:".Length..] : entry.Key,
                entry.Value.LocalName ?? "AirPods",
                [],
                [TransportNames.BleAdvertisement]))
            .ToArray();
        return Task.FromResult<IReadOnlyList<DeviceCandidate>>(candidates);
    }

    public void Start()
    {
        try
        {
            _hub.Start();
            // 先抛出一次当前快照，确保应用启动时已可见的 AirPods 立刻进入候选。
            RaiseIfChanged();
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Debug(
                "BLE", $"启动 BLE 发现失败：{exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _hub.AdvertisementReceived -= _onAdvertisement;
        try { _hub.Stop(); } catch { }
    }

    private void OnHubAdvertisement(object? sender, AppleAdvertisementEventArgs e)
        => RaiseIfChanged();

    private void RaiseIfChanged()
    {
        var ids = _hub.Snapshot().Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);
        if (ids.SetEquals(_lastStableIds))
            return;

        _lastStableIds = ids;
        var candidates = ids.Select(id =>
        {
            var localName = _hub.Snapshot().FirstOrDefault(x => x.Key == id).Value.LocalName;
            return new DeviceCandidate(
                id,
                id,
                id.StartsWith("BLE:", StringComparison.Ordinal) ? id["BLE:".Length..] : id,
                localName ?? "AirPods",
                [],
                [TransportNames.BleAdvertisement]);
        }).ToArray();
        var generation = Interlocked.Increment(ref _generation);
        DevicesChanged?.Invoke(this, new DeviceCandidatesChangedEventArgs(candidates, generation));
    }
}
