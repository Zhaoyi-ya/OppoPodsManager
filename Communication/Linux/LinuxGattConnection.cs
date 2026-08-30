using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Communication.Linux;

// BlueZ GATT 回退链路（RFCOMM 不可用时的兜底，例如部分固件只暴露 GATT、SPP 不可见）。
// 这里用 BlueZ 标准的 org.freedesktop.DBus.ObjectManager.GetManagedObjects 取全部对象及其接口属性
// （含 UUID），比 main 分支手搓 Introspect XML 解析可靠——main 的 GATT 因 Introspect 解析不到
// UUID（uuid 变量恒为 null）而永远连不上。
public sealed class LinuxGattConnectionFactory : IConnectionFactory
{
    public const string TransportName = "gatt";

    public string Transport => TransportName;

    public async Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        OppoPodsManager.Communication.Abstractions.ConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var connection = new LinuxGattConnection(candidate);
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

internal sealed class LinuxGattConnection : IRawConnection
{
    // OPPO / heyTap melody GATT 服务与特征 UUID（取自官方 SDK 协议定义，与 main 分支一致）。
    private static readonly Guid[] ServiceUuids =
    {
        new("0000079A-D102-11E1-9B23-00025B00A5A5"),
        new("0000079C-D102-11E1-9B23-00025B00A5A5"),
        new("DF21FE2C-2515-4FDB-8886-F12C4D67927C"), // Enco Air4 Pro 等
    };

    private static readonly Guid[] TxCharUuids =
    {
        new("0000079B-D102-11E1-9B23-00025B00A5A5"),
        new("0200079C-D102-11E1-9B23-00025B00A5A5"),
        new("DF21FE2D-2515-4FDB-8886-F12C4D67927C"),
    };

    private static readonly Guid[] RxCharUuids =
    {
        new("0000079C-D102-11E1-9B23-00025B00A5A5"),
        new("0100079C-D102-11E1-9B23-00025B00A5A5"),
        new("DF21FE2E-2515-4FDB-8886-F12C4D67927C"),
    };

    private readonly DeviceCandidate _candidate;
    private readonly object _gate = new();
    private Connection? _dbus;
    private string? _txCharPath;
    private string? _rxCharPath;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private int _connected;
    private int _disposed;
    private byte[] _lastRx = Array.Empty<byte>();

    public LinuxGattConnection(DeviceCandidate candidate) => _candidate = candidate;

    public bool IsConnected => Volatile.Read(ref _connected) != 0;
    public event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    public event EventHandler? Disconnected;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!ulong.TryParse(_candidate.BluetoothAddress, System.Globalization.NumberStyles.HexNumber, null, out var address))
            throw new InvalidOperationException("GATT 需要蓝牙地址。");

        var connection = new Connection(Address.System);
        await connection.ConnectAsync();

        var om = connection.CreateProxy<IObjectManager>("org.bluez", "/");
        var objects = await om.GetManagedObjectsAsync();

        // 1) 按蓝牙地址定位已连接设备路径。
        string? devicePath = null;
        foreach (var (path, ifaces) in objects)
        {
            if (!ifaces.TryGetValue("org.bluez.Device1", out var devProps))
                continue;
            if (devProps.TryGetValue("Address", out var addrObj) && addrObj is string addr)
            {
                if (addr.Replace(":", string.Empty).Equals(address.ToString("X12"), StringComparison.OrdinalIgnoreCase))
                {
                    devicePath = path.ToString();
                    break;
                }
            }
        }

        if (devicePath is null)
            throw new InvalidOperationException("BlueZ 中未找到已连接的设备。");

        // 2) 在该设备下找 melody GATT 服务。
        string? servicePath = null;
        foreach (var (path, ifaces) in objects)
        {
            if (!ifaces.TryGetValue("org.bluez.GattService1", out var svcProps))
                continue;
            if (svcProps.TryGetValue("Device", out var devObj) && devObj?.ToString() == devicePath
                && svcProps.TryGetValue("UUID", out var uuidObj) && uuidObj is string uuidStr
                && Guid.TryParse(uuidStr, out var svcUuid) && ServiceUuids.Contains(svcUuid))
            {
                servicePath = path.ToString();
                break;
            }
        }

        if (servicePath is null)
            throw new InvalidOperationException("未找到 melody GATT 服务（设备可能只暴露 RFCOMM）。");

        // 3) 在服务下找 TX(写)/RX(通知) 特征。
        foreach (var (path, ifaces) in objects)
        {
            if (!ifaces.TryGetValue("org.bluez.GattCharacteristic1", out var charProps))
                continue;
            if (charProps.TryGetValue("Service", out var svcObj) && svcObj?.ToString() != servicePath)
                continue;
            if (charProps.TryGetValue("UUID", out var cuuidObj) && cuuidObj is string cuuidStr
                && Guid.TryParse(cuuidStr, out var cUuid))
            {
                if (TxCharUuids.Contains(cUuid))
                    _txCharPath = path.ToString();
                else if (RxCharUuids.Contains(cUuid))
                    _rxCharPath = path.ToString();
            }
        }

        if (_txCharPath is null || _rxCharPath is null)
            throw new InvalidOperationException("未找到 TX/RX GATT 特征。");

        // 4) 订阅 RX 通知，让 BlueZ 在收到数据时用 Value 属性暴露。
        var rxProxy = connection.CreateProxy<IGattCharacteristic1>("org.bluez", _rxCharPath);
        await rxProxy.StartNotifyAsync();

        _dbus = connection;
        Volatile.Write(ref _connected, 1);
        _readCancellation = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
    }

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var dbus = _dbus;
        var tx = _txCharPath;
        if (dbus is null || tx is null)
            throw new IOException("GATT 未连接。");
        var proxy = dbus.CreateProxy<IGattCharacteristic1>("org.bluez", tx);
        await proxy.WriteValueAsync(data.ToArray(), new Dictionary<string, object>());
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                var dbus = _dbus;
                var rx = _rxCharPath;
                if (dbus is null || rx is null)
                    break;

                try
                {
                    var proxy = dbus.CreateProxy<IGattCharacteristic1>("org.bluez", rx);
                    var data = await proxy.ReadValueAsync(new Dictionary<string, object>());
                    if (data.Length > 0 && !data.AsSpan().SequenceEqual(_lastRx))
                    {
                        _lastRx = data;
                        DataReceived?.Invoke(this, new ReadOnlyMemory<byte>(data));
                    }
                }
                catch (Exception exception)
                {
                    global::OppoPodsManager.Control.Subsystems.Logging.ApplicationLog.Current?.Debug(
                        "LinuxGatt", $"读取 RX 特征失败（已忽略）：{exception.Message}");
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (Interlocked.Exchange(ref _connected, 0) != 0 && !cancellationToken.IsCancellationRequested)
                Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var wasConnected = Interlocked.Exchange(ref _connected, 0) != 0;
        _readCancellation?.Cancel();

        if (_dbus is not null && _rxCharPath is not null)
        {
            try
            {
                var proxy = _dbus.CreateProxy<IGattCharacteristic1>("org.bluez", _rxCharPath);
                await proxy.StopNotifyAsync();
            }
            catch
            {
                // 设备可能已断开，忽略。
            }
        }

        if (_readTask is not null)
        {
            try
            {
                await _readTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _readCancellation?.Dispose();
        _readCancellation = null;
        _readTask = null;
        CleanupDbus();

        if (wasConnected)
            Disconnected?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await DisconnectAsync(CancellationToken.None);
    }

    private void CleanupDbus()
    {
        lock (_gate)
        {
            _txCharPath = null;
            _rxCharPath = null;
            try
            {
                _dbus?.Dispose();
            }
            catch
            {
                // 忽略释放异常。
            }

            _dbus = null;
        }
    }
}

[DBusInterface("org.freedesktop.DBus.ObjectManager")]
interface IObjectManager : IDBusObject
{
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
}

[DBusInterface("org.bluez.GattCharacteristic1")]
interface IGattCharacteristic1 : IDBusObject
{
    Task WriteValueAsync(byte[] value, IDictionary<string, object> options);

    Task<byte[]> ReadValueAsync(IDictionary<string, object> options);

    Task StartNotifyAsync();

    Task StopNotifyAsync();
}
