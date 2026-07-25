using System.Runtime.Versioning;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OppoPodsManager.Platforms.Windows;

/// <summary>
/// Event-driven watcher for currently connected Bluetooth devices.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsBluetoothConnectionWatcher : IAsyncDisposable
{
    private static readonly Guid MelodySppUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");

private readonly object _gate = new();
    private readonly Dictionary<string, RawDeviceCandidate> _devices = new();
    private DeviceWatcher? _watcher;
    private IReadOnlyList<RawDeviceCandidate>? _lastPublishedSnapshot;
    private bool _disposed;
    private int _disposedFlag;

    public event Action<IReadOnlyList<RawDeviceCandidate>>? DevicesChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher != null) return;
            var selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(
                BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(selector);
            _watcher.Added += OnAdded;
            _watcher.Updated += OnUpdated;
            _watcher.Removed += OnRemoved;
            _watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher.Start();
        }
    }

private async void OnAdded(DeviceWatcher sender, DeviceInformation args)
    {
        try { await RefreshDeviceAsync(args.Id, args.Name).ConfigureAwait(false); }
        catch { /* async void boundary: never throw out of watcher callbacks */ }
    }

    private async void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        try { await RefreshDeviceAsync(args.Id, null).ConfigureAwait(false); }
        catch { /* async void boundary */ }
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;
        bool changed;
        lock (_gate) changed = _devices.Remove(args.Id);
        if (changed) PublishSnapshot();
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;
        PublishSnapshot();
    }

    private async Task RefreshDeviceAsync(string id, string? fallbackName)
    {
        if (Volatile.Read(ref _disposedFlag) != 0) return;

        BluetoothDevice? device = null;
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(id).AsTask().ConfigureAwait(false);
            if (Volatile.Read(ref _disposedFlag) != 0) return;

            device = await BluetoothDevice.FromIdAsync(id).AsTask().ConfigureAwait(false);
            if (Volatile.Read(ref _disposedFlag) != 0) return;

            if (device is null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                Remove(id);
                return;
            }

            var addr = device.BluetoothAddress;
            var name = string.IsNullOrWhiteSpace(info?.Name) ? fallbackName : info?.Name;
            var hasMelody = WindowsConnectedDeviceDiscovery.HasOppoSppService(addr);
            if (addr == 0 || !SupportedEarbudIdentity.IsCandidate(name, hasMelody))
            {
                Remove(id);
                return;
            }

            var candidate = new RawDeviceCandidate(
                StableId: addr.ToString("X12"),
                PlatformDeviceId: id,
                BluetoothAddress: addr,
                AdvertisedName: string.IsNullOrWhiteSpace(name) ? $"耳机 {addr:X12}" : name,
                ServiceUuids: hasMelody ? new HashSet<Guid> { MelodySppUuid } : new HashSet<Guid>(),
                AvailableTransports: new HashSet<DeviceTransport>
                {
                    DeviceTransport.Rfcomm,
                    DeviceTransport.BluetoothClassic,
                    DeviceTransport.Gatt,
                });

            lock (_gate)
            {
                if (_disposed) return;
                _devices[id] = candidate;
            }
            PublishSnapshot();
        }
        catch (ObjectDisposedException)
        {
            // Watcher/device torn down mid-refresh.
        }
        catch
        {
            // Ignore transient WinRT failures; next event will refresh.
        }
        finally
        {
            device?.Dispose();
        }
    }

    private void Remove(string id)
    {
        bool changed;
        lock (_gate) changed = _devices.Remove(id);
        if (changed) PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        IReadOnlyList<RawDeviceCandidate> snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = _devices.Values
                .GroupBy(device => device.BluetoothAddress)
                .Select(group => group.First())
                .OrderBy(device => device.AdvertisedName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (_lastPublishedSnapshot is not null
                && _lastPublishedSnapshot.Select(d => d.StableId).SequenceEqual(snapshot.Select(d => d.StableId)))
                return;

            _lastPublishedSnapshot = snapshot;
        }
        DevicesChanged?.Invoke(snapshot);
    }

public ValueTask DisposeAsync()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            Volatile.Write(ref _disposedFlag, 1);
            watcher = _watcher;
            _watcher = null;
            _devices.Clear();
            _lastPublishedSnapshot = null;
        }

        if (watcher is null)
            return ValueTask.CompletedTask;

        try
        {
            watcher.Added -= OnAdded;
            watcher.Updated -= OnUpdated;
            watcher.Removed -= OnRemoved;
            watcher.EnumerationCompleted -= OnEnumerationCompleted;
            if (watcher.Status is DeviceWatcherStatus.Started
                or DeviceWatcherStatus.EnumerationCompleted
                or DeviceWatcherStatus.Stopping)
            {
                watcher.Stop();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch
        {
        }

        return ValueTask.CompletedTask;
    }
}
