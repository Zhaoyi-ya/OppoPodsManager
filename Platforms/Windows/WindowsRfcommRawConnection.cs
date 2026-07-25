using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Core.Communication;
using OppoPodsManager.Core.Connections;
using OppoPodsManager.Core.Devices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace OppoPodsManager.Platforms.Windows;

/// <summary>
/// Windows RFCOMM raw byte connection. No brand framing/codec — only StreamSocket I/O.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsRfcommRawConnection : IRawConnection
{
    public static readonly Guid MelodySppUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private const int ConnectTimeoutMs = 3000;

    private readonly object _gate = new();
    private RfcommDeviceService? _service;
    private StreamSocket? _socket;
    private DataWriter? _writer;
    private DataReader? _reader;
    private CancellationTokenSource? _readCts;
    private Task? _readLoop;
    private bool _disposed;

    public WindowsRfcommRawConnection(RawDeviceCandidate device, ConnectionProfile profile)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        if (device.BluetoothAddress is null or 0)
            throw new ArgumentException("RFCOMM 连接需要蓝牙地址。", nameof(device));
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
                return ConnectionResult.Failure(LastError ?? "RFCOMM 连接失败");
            }

            lock (_gate)
            {
                IsConnected = true;
                StartReadLoopLocked();
            }

            LastError = null;
            return ConnectionResult.Success();
        }
        catch (OperationCanceledException)
        {
            LastError = "RFCOMM 连接超时或取消";
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
        DataWriter? writer;
        lock (_gate)
        {
            if (!IsConnected || _writer is null)
                throw new InvalidOperationException("RFCOMM 未连接。");
            writer = _writer;
        }

        var bytes = data.ToArray();
        writer.WriteBytes(bytes);
        await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
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
        var service = await FindServiceAsync(addr, cancellationToken).ConfigureAwait(false);
        if (service?.Device is null || service.Device.BluetoothAddress != addr)
        {
            LastError = $"目标 {addr:X12} 未发现 Melody RFCOMM 服务";
            service?.Dispose();
            return false;
        }

        var socket = new StreamSocket();
        await socket.ConnectAsync(service.ConnectionHostName, service.ConnectionServiceName)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        var writer = new DataWriter(socket.OutputStream);
        var reader = new DataReader(socket.InputStream) { InputStreamOptions = InputStreamOptions.Partial };

        lock (_gate)
        {
            _service = service;
            _socket = socket;
            _writer = writer;
            _reader = reader;
        }

        return true;
    }

    private void StartReadLoopLocked()
    {
        _readCts = new CancellationTokenSource();
        var ct = _readCts.Token;
        _readLoop = Task.Run(() => ReadLoopAsync(ct), ct);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        DataReader? reader;
        lock (_gate) reader = _reader;
        if (reader is null)
            return;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                uint got = await reader.LoadAsync(512).AsTask(ct).ConfigureAwait(false);
                if (got == 0)
                    break;

                var chunk = new byte[got];
                reader.ReadBytes(chunk);
                if (IsConnected)
                    DataReceived?.Invoke(chunk);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            if (!ct.IsCancellationRequested && IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke();
            }
        }
    }

    private async Task CleanupAsync()
    {
        CancellationTokenSource? readCts;
        Task? readLoop;
        DataWriter? writer;
        DataReader? reader;
        StreamSocket? socket;
        RfcommDeviceService? service;

        lock (_gate)
        {
            readCts = _readCts;
            readLoop = _readLoop;
            writer = _writer;
            reader = _reader;
            socket = _socket;
            service = _service;
            _readCts = null;
            _readLoop = null;
            _writer = null;
            _reader = null;
            _socket = null;
            _service = null;
        }

        try { readCts?.Cancel(); } catch { }

        // Dispose socket first so LoadAsync unblocks the read loop promptly.
        try { if (writer is not null) { writer.DetachStream(); writer.Dispose(); } } catch { }
        try { if (reader is not null) { reader.DetachStream(); reader.Dispose(); } } catch { }
        try { socket?.Dispose(); } catch { }
        try { service?.Dispose(); } catch { }

        if (readLoop is not null)
        {
            try { await readLoop.WaitAsync(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false); }
            catch { }
        }

        try { readCts?.Dispose(); } catch { }
    }

    private static async Task<RfcommDeviceService?> FindServiceAsync(
        ulong targetAddr,
        CancellationToken cancellationToken)
    {
        var byUuid = await FindByServiceUuidAsync(targetAddr, cancellationToken).ConfigureAwait(false);
        if (byUuid is not null)
            return byUuid;
        return await FindByPairedDeviceAsync(targetAddr, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<RfcommDeviceService?> FindByServiceUuidAsync(
        ulong targetAddr,
        CancellationToken cancellationToken)
    {
        var serviceId = RfcommServiceId.FromUuid(MelodySppUuid);
        string selector = RfcommDeviceService.GetDeviceSelector(serviceId);
        var devices = await DeviceInformation.FindAllAsync(selector)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        foreach (var di in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RfcommDeviceService? svc = null;
            try
            {
                svc = await RfcommDeviceService.FromIdAsync(di.Id)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (svc?.Device is not null && svc.Device.BluetoothAddress == targetAddr)
                {
                    // Hand ownership to caller; do not dispose on match.
                    var owned = svc;
                    svc = null;
                    return owned;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
            finally
            {
                svc?.Dispose();
            }
        }

        return null;
    }

    private static async Task<RfcommDeviceService?> FindByPairedDeviceAsync(
        ulong targetAddr,
        CancellationToken cancellationToken)
    {
        string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
        var devices = await DeviceInformation.FindAllAsync(selector)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var serviceId = RfcommServiceId.FromUuid(MelodySppUuid);
        foreach (var di in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BluetoothDevice? dev = null;
            var keepDevice = false;
            try
            {
                dev = await BluetoothDevice.FromIdAsync(di.Id)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (dev is null || dev.BluetoothAddress != targetAddr)
                    continue;

                var result = await dev.GetRfcommServicesForIdAsync(serviceId, BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                if (result.Services.Count > 0)
                {
                    // Transfer ownership: service + device stay alive for the connection.
                    // BluetoothDevice must not be disposed while RfcommDeviceService uses it.
                    keepDevice = true;
                    return result.Services[0];
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
            }
            finally
            {
                if (!keepDevice)
                {
                    try { dev?.Dispose(); } catch { }
                }
            }
        }

        return null;
    }
}
