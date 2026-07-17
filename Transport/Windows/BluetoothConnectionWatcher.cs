using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace OppoPodsManager;

/// <summary>
/// Event-driven watcher for currently connected Bluetooth devices.
/// It reports only devices that pass the same earbud identity gate as discovery.
/// Communication verification remains the caller's responsibility.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class BluetoothConnectionWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ulong addr, string name)> _devices = new();
    private DeviceWatcher? _watcher;
    private IReadOnlyList<(ulong addr, string name)>? _lastPublishedSnapshot;
    private bool _disposed;

    public event Action<IReadOnlyList<(ulong addr, string name)>>? DevicesChanged;

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
        => await RefreshDeviceAsync(args.Id, args.Name);

    private async void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
        => await RefreshDeviceAsync(args.Id, null);

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        bool changed;
        lock (_gate) changed = _devices.Remove(args.Id);
        if (changed) PublishSnapshot();
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
        => PublishSnapshot();

    private async Task RefreshDeviceAsync(string id, string? fallbackName)
    {
        try
        {
            var info = await DeviceInformation.CreateFromIdAsync(id);
            var device = await BluetoothDevice.FromIdAsync(id);
            if (device == null || device.ConnectionStatus != BluetoothConnectionStatus.Connected)
            {
                Remove(id);
                return;
            }

            var addr = device.BluetoothAddress;
            var name = string.IsNullOrWhiteSpace(info?.Name) ? fallbackName : info?.Name;
            var hasMelodyService = WindowsDeviceDiscovery.HasOppoSppService(addr);
            if (addr == 0 || !SupportedEarbudIdentity.IsCandidate(name, hasMelodyService))
            {
                Remove(id);
                return;
            }

            lock (_gate)
            {
                if (_disposed) return;
                _devices[id] = (addr, string.IsNullOrWhiteSpace(name) ? $"耳机 {addr:X12}" : name);
            }
            PublishSnapshot();
        }
        catch (Exception ex)
        {
            Log.Ex("BTWATCH", $"RefreshDevice id={id}", ex);
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
        IReadOnlyList<(ulong addr, string name)> snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            snapshot = _devices.Values
                .GroupBy(device => device.addr)
                .Select(group => group.First())
                .OrderBy(device => device.name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // DeviceWatcher can emit several Updated/EnumerationCompleted callbacks
            // for the same state. Do not wake the reconnect loop for an unchanged
            // snapshot, especially for the common "no connected device" state.
            if (_lastPublishedSnapshot != null && _lastPublishedSnapshot.SequenceEqual(snapshot))
                return;

            _lastPublishedSnapshot = snapshot;
        }
        DevicesChanged?.Invoke(snapshot);
    }

    public void Dispose()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _devices.Clear();
            _lastPublishedSnapshot = null;
        }

        if (watcher == null) return;
        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        try { watcher.Stop(); } catch { }
    }
}
