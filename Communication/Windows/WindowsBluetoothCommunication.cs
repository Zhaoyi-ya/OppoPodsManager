using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Windows;

// Windows 多品牌设备发现入口；Win32/WinRT 细节留在本平台文件中，不泄漏到控制层。
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsBluetoothDiscovery : IDeviceDiscoveryMonitor
{
    private readonly object _gate = new();
    private global::Windows.Devices.Enumeration.DeviceWatcher? _watcher;
    private IReadOnlyList<DeviceCandidate> _lastSnapshot = [];
    private int _refreshGeneration;
    private long _snapshotGeneration;
    private bool _disposed;

    public event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    public async Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var devices = await Task.Run(WindowsBluetoothDiscoveryCore.ListConnected, cancellationToken);
        return devices
            .Where(device => device.Address != 0)
            .Select(device => new DeviceCandidate(
                device.Address.ToString("X12"),
                device.Address.ToString("X12"),
                device.Address.ToString("X12"),
                device.Name,
                device.ServiceIds,
                [WindowsRfcommConnectionFactory.TransportName]))
            .ToArray();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
                return;

            var selector = global::Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                global::Windows.Devices.Bluetooth.BluetoothConnectionStatus.Connected);
            _watcher = global::Windows.Devices.Enumeration.DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnDeviceChanged;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Start();
        }
        _ = RefreshSnapshotAsync();
    }

    public void Dispose()
    {
        global::Windows.Devices.Enumeration.DeviceWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _lastSnapshot = [];
            _refreshGeneration++;
        }

        if (watcher is null)
            return;
        watcher.Added -= OnDeviceChanged;
        watcher.Updated -= OnDeviceUpdated;
        watcher.Removed -= OnDeviceRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        try { watcher.Stop(); } catch { }
    }

    private async void OnDeviceChanged(global::Windows.Devices.Enumeration.DeviceWatcher sender, global::Windows.Devices.Enumeration.DeviceInformation args)
        => await RefreshSnapshotAsync();

    private async void OnDeviceUpdated(global::Windows.Devices.Enumeration.DeviceWatcher sender, global::Windows.Devices.Enumeration.DeviceInformationUpdate args)
        => await RefreshSnapshotAsync();

    private async void OnDeviceRemoved(global::Windows.Devices.Enumeration.DeviceWatcher sender, global::Windows.Devices.Enumeration.DeviceInformationUpdate args)
        => await RefreshSnapshotAsync();

    private async void OnEnumerationCompleted(global::Windows.Devices.Enumeration.DeviceWatcher sender, object args)
        => await RefreshSnapshotAsync();

    private async Task RefreshSnapshotAsync()
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        try
        {
            var snapshot = await DiscoverAsync(CancellationToken.None);
            if (generation != Volatile.Read(ref _refreshGeneration))
                return;

            IReadOnlyList<DeviceCandidate> previous;
            lock (_gate)
                previous = _lastSnapshot;
            if (previous.Count > 0 && snapshot.Count == 0)
            {
                // Win32/WinRT 枚举在蓝牙栈切换期间可能短暂返回空集；
                // 先复核一次，避免瞬时枚举失败误清理当前会话。
                await Task.Delay(500);
                if (generation != Volatile.Read(ref _refreshGeneration))
                    return;
                snapshot = await DiscoverAsync(CancellationToken.None);
            }

            if (generation != Volatile.Read(ref _refreshGeneration))
                return;

            lock (_gate)
            {
                if (_disposed || _lastSnapshot.Select(device => device.StableId).SequenceEqual(snapshot.Select(device => device.StableId)))
                    return;
                _lastSnapshot = snapshot;
            }
            var eventGeneration = Interlocked.Increment(ref _snapshotGeneration);
            DevicesChanged?.Invoke(this, new DeviceCandidatesChangedEventArgs(snapshot, eventGeneration));
        }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Error("Bluetooth", "Windows 蓝牙设备变化刷新失败。", exception);
        }
    }
}

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsRfcommConnectionFactory : IConnectionFactory
{
    public const string TransportName = "rfcomm";

    public string Transport => TransportName;

    public async Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        if (options.ServiceId is not { } serviceId)
            throw new InvalidOperationException("RFCOMM connection requires a service UUID selected by ControlManager.");

        var connection = new WindowsRfcommConnection(candidate, serviceId, options.AllowBareChannels);
        try
        {
            await connection.ConnectAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
