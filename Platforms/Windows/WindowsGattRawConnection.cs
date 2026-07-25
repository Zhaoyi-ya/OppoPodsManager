using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace OppoPodsManager.Platforms.Windows;

/// <summary>
/// Windows BLE GATT raw byte connection. No brand framing — only characteristic I/O.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsGattRawConnection : IRawConnection
{
    private static readonly Guid ServiceUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid TxCharUuid = new("0000079B-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid RxCharUuid = new("0000079C-D102-11E1-9B23-00025B00A5A5");
    private const int ConnectTimeoutMs = 8000;

    private readonly object _gate = new();
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _txChar;
    private GattCharacteristic? _rxChar;
    private bool _disposed;

    public WindowsGattRawConnection(RawDeviceCandidate device, ConnectionProfile profile)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (device.BluetoothAddress is null or 0)
            throw new ArgumentException("GATT 连接需要蓝牙地址。", nameof(device));
    }

    public RawDeviceCandidate Device { get; }
    public ConnectionProfile Profile { get; }
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }
    public event Action<ReadOnlyMemory<byte>>? DataReceived;
    public event Action? Disconnected;

    public async ValueTask<ConnectionResult> ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeoutMs);
        try
        {
            var ok = await ConnectCoreAsync(timeout.Token).ConfigureAwait(false);
            if (!ok)
            {
                await CleanupAsync().ConfigureAwait(false);
                return ConnectionResult.Failure(LastError ?? "GATT 连接失败");
            }

            IsConnected = true;
            LastError = null;
            return ConnectionResult.Success();
        }
        catch (OperationCanceledException)
        {
            LastError = "GATT 连接超时或取消";
            await CleanupAsync().ConfigureAwait(false);
            return ConnectionResult.Failure(LastError);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await CleanupAsync().ConfigureAwait(false);
            return ConnectionResult.Failure(LastError);
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GattCharacteristic? tx;
        lock (_gate)
        {
            if (!IsConnected || _txChar is null)
                throw new InvalidOperationException("GATT 未连接。");
            tx = _txChar;
        }

        var writer = new DataWriter();
        writer.WriteBytes(data.ToArray());
        var buffer = writer.DetachBuffer();
        var status = await tx.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"GATT 写入失败: {status}");
    }

    public async ValueTask DisconnectAsync()
    {
        var wasConnected = IsConnected;
        IsConnected = false;
        await CleanupAsync().ConfigureAwait(false);
        if (wasConnected)
            Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
    }

    private async Task<bool> ConnectCoreAsync(CancellationToken cancellationToken)
    {
        var addr = Device.BluetoothAddress!.Value;
        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(addr)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (device is null)
        {
            LastError = $"无法打开 BLE 设备 {addr:X12}";
            return false;
        }

        var svcResult = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
        {
            LastError = $"未发现 melody GATT 服务 ({svcResult.Status})";
            device.Dispose();
            return false;
        }

        var service = svcResult.Services[0];
        var txResult = await service.GetCharacteristicsForUuidAsync(TxCharUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var rxResult = await service.GetCharacteristicsForUuidAsync(RxCharUuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0
            || rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
        {
            LastError = "未发现 TX/RX 特征";
            service.Dispose();
            device.Dispose();
            return false;
        }

        var tx = txResult.Characteristics[0];
        var rx = rxResult.Characteristics[0];
        var cccd = await rx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (cccd != GattCommunicationStatus.Success)
        {
            LastError = $"写 CCCD 失败 ({cccd})";
            service.Dispose();
            device.Dispose();
            return false;
        }

        lock (_gate)
        {
            _device = device;
            _service = service;
            _txChar = tx;
            _rxChar = rx;
            _rxChar.ValueChanged += OnRxValueChanged;
            _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        }

        return true;
    }

    private void OnRxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var data = args.CharacteristicValue.ToArray();
            if (data.Length == 0 || !IsConnected)
                return;
            DataReceived?.Invoke(data);
        }
        catch
        {
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected && IsConnected)
        {
            IsConnected = false;
            Disconnected?.Invoke();
        }
    }

    private Task CleanupAsync()
    {
        BluetoothLEDevice? device;
        GattDeviceService? service;
        GattCharacteristic? rx;
        lock (_gate)
        {
            device = _device;
            service = _service;
            rx = _rxChar;
            _device = null;
            _service = null;
            _txChar = null;
            _rxChar = null;
        }

        try { if (rx is not null) rx.ValueChanged -= OnRxValueChanged; } catch { }
        try { if (device is not null) device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
        try { service?.Dispose(); } catch { }
        try { device?.Dispose(); } catch { }
        return Task.CompletedTask;
    }
}
