using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus;

namespace OppoPodsManager;

public sealed class LinuxGattTransport : IPodTransport
{
    private static readonly Guid[] ServiceUuids = {
        new("0000079A-D102-11E1-9B23-00025B00A5A5"),
        new("0000079C-D102-11E1-9B23-00025B00A5A5"),
        new("DF21FE2C-2515-4FDB-8886-F12C4D67927C"),  // Enco Air4 Pro 等
    };
    private static readonly Guid[] TxCharUuids = {
        new("0000079B-D102-11E1-9B23-00025B00A5A5"),
        new("0200079C-D102-11E1-9B23-00025B00A5A5"),
        new("DF21FE2D-2515-4FDB-8886-F12C4D67927C"),
    };
    private static readonly Guid[] RxCharUuids = {
        new("0000079C-D102-11E1-9B23-00025B00A5A5"),
        new("0100079C-D102-11E1-9B23-00025B00A5A5"),
        new("DF21FE2E-2515-4FDB-8886-F12C4D67927C"),
    };
    private const int ConnectTimeoutMs = 8000;
    private readonly IDeviceLocator _locator;
    private readonly IFrameCodec _codec = new GattFrameCodec();
    private readonly List<byte> _framer = new();
    private readonly ConcurrentQueue<PodFrame> _rxQueue = new();
    private readonly object _lock = new();
    private Connection? _dbus;
    private string? _rxCharPath;
    private string? _txCharPath;
    private string? _devPath;
    private byte[] _lastRxData = Array.Empty<byte>();
    private bool _disposed;

    public LinuxGattTransport() : this(new LinuxBluetoothLocator()) { }
    public LinuxGattTransport(IDeviceLocator locator) { _locator = locator; }
    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected { get; private set; }
    public event Action<PodFrame>? FrameReceived;
#pragma warning disable CS0067
    public event Action? Disconnected;
#pragma warning restore CS0067

    public bool Connect()
    {
        try
        {
            if (!RunSync(ConnectAsyncCore, ConnectTimeoutMs)) { CleanupDbus(); if (string.IsNullOrEmpty(LastError)) LastError = "BLE GATT 连接超时"; return false; }
            IsConnected = true; LastError = null;
            return true;
        }
        catch (Exception e) { LastError = e.Message; Log.Ex("LXGATT", "Connect", e); CleanupDbus(); return false; }
    }

    private async Task<bool> ConnectAsyncCore()
    {
        var (addr, name) = _locator.Locate();
        if (addr == 0) { LastError = "未发现已配对的 OPPO 蓝牙设备"; return false; }
        DeviceName = name;
        var addrHex = addr.ToString("X12");
        _devPath = $"/org/bluez/hci0/dev_{addrHex.Substring(10,2)}_{addrHex.Substring(8,2)}_{addrHex.Substring(6,2)}_{addrHex.Substring(4,2)}_{addrHex.Substring(2,2)}_{addrHex.Substring(0,2)}";
        Log.D("LXGATT", $"Connect: device path={_devPath}");

        _dbus = new Connection(Address.System);
        await _dbus.ConnectAsync();
        Log.D("LXGATT", "Connect: D-Bus 已连接");

        // 使用 Introspect 获取 GATT 服务树
        var introProxy = _dbus.CreateProxy<IGattProxy>("org.bluez", _devPath);
        var xml = await introProxy.CallMethodWithReturnStringAsync("Introspect", null, "org.freedesktop.DBus.Introspectable");
        Log.D("LXGATT", $"Connect: Introspect OK, {xml?.Length ?? 0} chars");

        // 从 XML 提取 service/char 节点并查询 UUID
        var svcCharMap = new Dictionary<string, (string svc, string? uuid)>();
        if (xml != null) ExtractGattNodes(xml, _devPath, svcCharMap);
        Log.D("LXGATT", $"Connect: 枚举到 {svcCharMap.Count} 个 GATT 节点");

        // 查找 melody 服务
        string? sp = null;
        foreach (var (nodePath, (svc, uuid)) in svcCharMap)
        {
            if (uuid != null && Guid.TryParse(uuid, out var g) && ServiceUuids.Contains(g))
            { sp = svc; break; }
        }
        if (sp == null) { LastError = "未发现 melody GATT 服务"; return false; }
        Log.D("LXGATT", $"Connect: melody 服务={sp}");

        // 查找 TX/RX 特征
        foreach (var (nodePath, (svc, uuid)) in svcCharMap)
        {
            if (!nodePath.StartsWith(sp)) continue;
            if (uuid != null && Guid.TryParse(uuid, out var g))
            {
                if (TxCharUuids.Contains(g)) _txCharPath = nodePath;
                else if (RxCharUuids.Contains(g)) _rxCharPath = nodePath;
            }
        }
        if (_txCharPath == null) { LastError = "未发现 TX 特征"; return false; }
        if (_rxCharPath == null) { LastError = "未发现 RX 特征"; return false; }
        Log.D("LXGATT", $"Connect: TX={_txCharPath} RX={_rxCharPath}");

        _framer.Clear(); _lastRxData = Array.Empty<byte>();
        while (_rxQueue.TryDequeue(out _)) { }
        return true;
    }

    private static void ExtractGattNodes(string xml, string parentPath, Dictionary<string, (string svc, string? uuid)> map)
    {
        string? currentSvc = null;
        int pos = 0;
        while (pos < xml.Length)
        {
            var tagStart = xml.IndexOf("<node", pos, StringComparison.Ordinal);
            if (tagStart < 0) break;
            var tagEnd = xml.IndexOf('>', tagStart);
            if (tagEnd < 0) break;
            var tag = xml.Substring(tagStart, tagEnd - tagStart + 1);
            pos = tagEnd + 1;

            // 提取 name 属性
            var nameIdx = tag.IndexOf("name=\"", StringComparison.Ordinal);
            if (nameIdx < 0) continue;
            nameIdx += 6;
            var nameEnd = tag.IndexOf('"', nameIdx);
            if (nameEnd < 0) continue;
            var name = tag.Substring(nameIdx, nameEnd - nameIdx);

            var nodePath = parentPath + "/" + name;

            if (name.StartsWith("service"))
                currentSvc = nodePath;
            else if (name.StartsWith("char") && currentSvc != null)
            {
                string? uuid = null;
                // 尝试提取 UUID
                // XXX simple parsing: assume UUID is embedded in node
                map[nodePath] = (currentSvc, uuid);
            }
        }
    }

    public void Send(ushort cmd, byte[] payload)
    {
        var d = _dbus; var tp = _txCharPath;
        if (d == null || tp == null) return;
        byte[] bytes; lock (_lock) { bytes = _codec.Encode(cmd, payload); }
        try { RunSync(async () => { var px = d.CreateProxy<IGattProxy>("org.bluez", tp); await px.CallMethodAsync("WriteValue", bytes, "org.bluez.GattCharacteristic1"); }, 3000); }
        catch (Exception ex) { Log.Ex("LXGATT", $"Send cmd=0x{cmd:X4}", ex); }
    }

    public void Poll(int timeoutMs)
    {
        var d = _dbus; var rp = _rxCharPath;
        if (d == null || rp == null || !IsConnected) return;
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            try
            {
                RunSync(async () =>
                {
                    var px = d.CreateProxy<IGattProxy>("org.bluez", rp);
                    var result = await px.CallMethodWithReturnAsync("ReadValue", null, "org.bluez.GattCharacteristic1");
                    if (result is byte[] data && data.Length > 0 && !data.AsSpan().SequenceEqual(_lastRxData))
                    {
                        _lastRxData = data;
                        lock (_framer) { for (int i = 0; i < data.Length; i++) { _framer.Add(data[i]); while (_codec.TryDecode(_framer, out var f)) _rxQueue.Enqueue(f); } }
                    }
                }, 200);
            }
            catch { }
            while (_rxQueue.TryDequeue(out var frame)) FrameReceived?.Invoke(frame);
            if (!IsConnected || DateTime.UtcNow >= end) break;
            Thread.Sleep(50);
        }
    }

    public void Close() { IsConnected = false; CleanupDbus(); }
    private void CleanupDbus() { lock (_lock) { _txCharPath = null; _rxCharPath = null; _devPath = null; try { _dbus?.Dispose(); } catch { } _dbus = null; } }
    public void Dispose() { if (_disposed) return; Close(); _disposed = true; }
    private static bool RunSync(Func<Task<bool>> op, int ms) { try { var t = op(); return t.Wait(ms) && t.Result; } catch { return false; } }
    private static void RunSync(Func<Task> op, int ms) { try { op().Wait(ms); } catch { } }
}

[DBusInterface("org.freedesktop.DBus.Introspectable")]
interface IGattProxy : Tmds.DBus.IDBusObject
{
    Task CallMethodAsync(string method, object? args, string iface);
    Task<object?> CallMethodWithReturnAsync(string method, object? args, string iface);
    Task<string?> CallMethodWithReturnStringAsync(string method, object? args, string iface);
}
