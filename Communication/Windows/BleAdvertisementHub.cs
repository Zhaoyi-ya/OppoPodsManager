using System.Collections.Concurrent;
using System.Runtime.Versioning;
using OppoPodsManager.Communication.Abstractions;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace OppoPodsManager.Communication.Windows;

// 维护一个跨发现/连接共享的 BLE 广播中枢：持有 BluetoothLEAdvertisementWatcher，
// 把每个 Apple(厂商 0x004C) 广播的最新厂商数据按稳定地址缓存，并在新广播到达时通知订阅者。
// 仅 Windows 编译（csproj 在 Linux 构建时排除 Communication/Windows）。
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class BleAdvertisementHub : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher? _watcher;
    private readonly ConcurrentDictionary<string, BleAdvEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    public event EventHandler<AppleAdvertisementEventArgs>? AdvertisementReceived;

    public BleAdvertisementHub()
    {
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher();
            // 只接收 Apple 厂商数据(company 0x004C)；Data 为空表示匹配任意载荷。
            var manufacturer = new BluetoothLEManufacturerData { CompanyId = 0x004C };
            var advertisement = new BluetoothLEAdvertisement();
            advertisement.ManufacturerData.Add(manufacturer);
            _watcher.AdvertisementFilter = new BluetoothLEAdvertisementFilter { Advertisement = advertisement };
            _watcher.Received += OnReceived;
        }
        catch (Exception exception)
        {
            // BLE 不可用（无蓝牙/权限）时静默降级：AirPods 仅不会被发现，不影响其他品牌。
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Debug(
                "BLE", $"初始化 BLE 广播监听失败，AirPods 发现不可用：{exception.Message}");
            _watcher = null;
        }
    }

    public void Start()
    {
        try { _watcher?.Start(); }
        catch (Exception exception)
        {
            global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Debug(
                "BLE", $"启动 BLE 广播监听失败：{exception.Message}");
        }
    }

    public void Stop()
    {
        try { _watcher?.Stop(); }
        catch { }
    }

    public byte[]? GetLatest(string stableId)
        => _entries.TryGetValue(stableId, out var entry) ? entry.Data : null;

    public IReadOnlyCollection<KeyValuePair<string, BleAdvEntry>> Snapshot()
        => _entries.ToArray();

    public void Forget(string stableId)
        => _entries.TryRemove(stableId, out _);

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.Advertisement is null)
            return;

        BluetoothLEManufacturerData? manufacturer = null;
        foreach (var data in args.Advertisement.ManufacturerData)
        {
            if (data.CompanyId == 0x004C)
            {
                manufacturer = data;
                break;
            }
        }
        if (manufacturer is null)
            return;

        var bytes = ReadBuffer(manufacturer.Data);
        if (bytes.Length == 0)
            return;

        var stableId = "BLE:" + args.BluetoothAddress.ToString("X12");
        var localName = args.Advertisement.LocalName;
        _entries[stableId] = new BleAdvEntry(bytes, localName);
        AdvertisementReceived?.Invoke(this, new AppleAdvertisementEventArgs(stableId, bytes));
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        if (buffer is null || buffer.Length == 0)
            return [];
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        if (_watcher is not null)
        {
            _watcher.Received -= OnReceived;
            try { _watcher.Stop(); } catch { }
        }
        _entries.Clear();
        AdvertisementReceived = null;
    }
}

// 单个 Apple 设备的广播缓存：厂商原始字节 + 广播中的本地名称（用于显示）。
public sealed record BleAdvEntry(byte[] Data, string? LocalName);
