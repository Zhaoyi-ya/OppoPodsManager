using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace OppoPodsManager;

/// <summary>
/// BLE GATT 传输（WinRT Windows.Devices.Bluetooth）。
///   Service 0000079A-…、TX(Write) 0000079B-…、RX(Notify) 0000079C-…、CCCD 2902。
/// 帧格式用 GattFrameCodec（melody 5 字节头，无 SPP 0xAA 外壳）。
///
/// 设备发现采用 WinRT 枚举（DeviceInformation），按优先级：
///   1) 按 melody GATT 服务 UUID 枚举（GetDeviceSelectorFromUuid）——最精确；
///   2) 按已配对 BLE 设备的品牌名匹配（BluetoothLEDevice 选择器）；
///   3) 最后回退到注册表定位的经典蓝牙地址（IDeviceLocator）——多为 BR/EDR 地址，成功率低。
/// 作为经典 SPP 失败后的回退连接方式（多数 OPPO 耳机在 Windows 下只暴露经典口，
/// 仅暴露 BLE 的设备/固件才会走到这里）。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsGattTransport : IPodTransport
{
    // melody 私有服务/特征 UUID（后缀 -D102-11E1-9B23-00025B00A5A5）
    private static readonly Guid ServiceUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid TxCharUuid  = new("0000079B-D102-11E1-9B23-00025B00A5A5"); // Write
    private static readonly Guid RxCharUuid  = new("0000079C-D102-11E1-9B23-00025B00A5A5"); // Notify

    private const int ConnectTimeoutMs = 8000;

    private readonly IDeviceLocator _locator;
    private readonly IFrameCodec _codec = new GattFrameCodec();
    private readonly List<byte> _framer = new();
    private readonly ConcurrentQueue<PodFrame> _rxQueue = new();
    private readonly object _lock = new();

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _txChar;
    private GattCharacteristic? _rxChar;
    private bool _disposed;

    /// <summary>默认用注册表回溯发现器；可注入其它 IDeviceLocator。</summary>
    public WindowsGattTransport() : this(new WindowsBluetoothLocator()) { }
    public WindowsGattTransport(IDeviceLocator locator) { _locator = locator; }

    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    /// <summary>WinRT 枚举发现 BLE 设备 → 取 GATT 服务/特征 → 订阅通知 → 完成连接。</summary>
    public bool Connect()
    {
        try
        {
            Log.D("GATT", "Connect: 开始");
            if (!RunSync(ConnectAsyncCore, ConnectTimeoutMs))
            {
                Cleanup();
                if (string.IsNullOrEmpty(LastError)) LastError = "GATT 连接超时";
                Log.Result("GATT", "Connect", false, LastError);
                return false;
            }

            IsConnected = true;
            LastError = null;
            Log.Result("GATT", "Connect", true, $"name=\"{DeviceName}\"");
            return true;
        }
        catch (Exception e)
        {
            LastError = Log.DescribeException(e);
            Log.Ex("GATT", "Connect", e);
            Cleanup();
            return false;
        }
    }

    /// <summary>实际的异步连接流程：WinRT 枚举发现设备 → 找服务/特征 → 订阅通知。</summary>
    private async Task<bool> ConnectAsyncCore()
    {
        _device = await DiscoverDeviceAsync();
        if (_device == null)
        {
            if (string.IsNullOrEmpty(LastError)) LastError = "未发现可用的 BLE 设备（WinRT 枚举+注册表回退均失败）";
            return false;
        }

        if (!string.IsNullOrEmpty(_device.Name)) DeviceName = _device.Name;
        Log.D("GATT", $"Connect: 已打开 BLE 设备 name=\"{DeviceName}\" addr={_device.BluetoothAddress:X12} 连接态={_device.ConnectionStatus}");

        // 取私有服务（uncached 强制走链路，避免系统缓存过期句柄）
        var svcResult = await _device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached);
        Log.D("GATT", $"Connect: 取 melody 服务 status={svcResult.Status} 服务数={svcResult.Services.Count}");
        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
        {
            // status=Unreachable 常见于设备只经典配对无 BLE 链路；ProtocolError=GATT 层拒绝
            LastError = $"未发现 melody GATT 服务 (status={svcResult.Status}; Unreachable 多为设备无 BLE 链路/未在广播)";
            return false;
        }
        var service = svcResult.Services[0];

        var txResult = await service.GetCharacteristicsForUuidAsync(TxCharUuid, BluetoothCacheMode.Uncached);
        var rxResult = await service.GetCharacteristicsForUuidAsync(RxCharUuid, BluetoothCacheMode.Uncached);
        if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0 ||
            rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
        {
            LastError = "未发现 TX/RX 特征";
            return false;
        }
        _txChar = txResult.Characteristics[0];
        _rxChar = rxResult.Characteristics[0];

        // 订阅 RX 通知：先挂事件，再写 CCCD(Notify)
        _rxChar.ValueChanged += OnRxValueChanged;
        var cccd = await _rxChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (cccd != GattCommunicationStatus.Success)
        {
            LastError = $"写 CCCD 失败 (status={cccd})，通常是未配对/未加密";
            return false;
        }

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
        _framer.Clear();
        while (_rxQueue.TryDequeue(out _)) { }
        Log.D("GATT", "Connect: 服务/特征就绪，通知已开启");
        return true;
    }

    /// <summary>
    /// 设备发现：优先 WinRT 枚举（按服务 UUID → 按配对 BLE 品牌名），
    /// 最后才回退到注册表定位的经典地址。返回可用的 BluetoothLEDevice，找不到返回 null。
    /// </summary>
    private async Task<BluetoothLEDevice?> DiscoverDeviceAsync()
    {
        // 1) 按 melody GATT 服务 UUID 枚举（最精确，直接命中暴露该服务的设备）
        var dev = await FindByServiceUuidAsync();
        if (dev != null) { Log.D("GATT", "Discover: 命中(服务 UUID 枚举)"); return dev; }

        // 2) 按已配对 BLE 设备的品牌名匹配
        dev = await FindByPairedNameAsync();
        if (dev != null) { Log.D("GATT", "Discover: 命中(配对 BLE 名称)"); return dev; }

        // 3) 回退：注册表定位的经典蓝牙地址（多为 BR/EDR，成功率低）
        var (addr, name) = _locator.Locate();
        if (addr != 0)
        {
            Log.D("GATT", $"Discover: 回退注册表地址 addr={addr:X12} name=\"{name}\"");
            var byAddr = await BluetoothLEDevice.FromBluetoothAddressAsync(addr);
            if (byAddr != null) { Log.D("GATT", "Discover: 命中(注册表地址回退)"); return byAddr; }
            LastError = "注册表地址无法打开为 BLE 设备（可能仅经典配对，非 BLE）";
        }
        return null;
    }

    /// <summary>按 melody 服务 UUID 用 DeviceInformation 枚举，取品牌名优先、否则取第一个。</summary>
    private async Task<BluetoothLEDevice?> FindByServiceUuidAsync()
    {
        try
        {
            string selector = GattDeviceService.GetDeviceSelectorFromUuid(ServiceUuid);
            var devices = await DeviceInformation.FindAllAsync(selector);
            Log.D("GATT", $"Discover: 服务 UUID 枚举到 {devices.Count} 个候选");
            if (devices.Count == 0) return null;

            // 优先品牌名匹配
            foreach (var di in devices)
            {
                if (IsSupportedBrand(di.Name))
                {
                    var svc = await GattDeviceService.FromIdAsync(di.Id);
                    if (svc?.Device != null) return svc.Device;
                }
            }
            // 回退取第一个可用
            foreach (var di in devices)
            {
                var svc = await GattDeviceService.FromIdAsync(di.Id);
                if (svc?.Device != null) return svc.Device;
            }
        }
        catch (Exception ex) { Log.Ex("GATT", "FindByServiceUuidAsync", ex); }
        return null;
    }

    /// <summary>枚举已配对 BLE 设备，按品牌名匹配。</summary>
    private async Task<BluetoothLEDevice?> FindByPairedNameAsync()
    {
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            var devices = await DeviceInformation.FindAllAsync(selector);
            Log.D("GATT", $"Discover: 已配对 BLE 枚举到 {devices.Count} 个");
            foreach (var di in devices)
            {
                if (!IsSupportedBrand(di.Name)) continue;
                var dev = await BluetoothLEDevice.FromIdAsync(di.Id);
                if (dev != null) return dev;
            }
        }
        catch (Exception ex) { Log.Ex("GATT", "FindByPairedNameAsync", ex); }
        return null;
    }

    /// <summary>设备名是否匹配受支持品牌（OPPO/OnePlus/realme）。</summary>
    private static bool IsSupportedBrand(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var brand in OppoProtocol.SupportedBrands)
            if (name.Contains(brand, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            Log.D("GATT", "ConnectionStatusChanged: 设备断开");
            OnDisconnected();
        }
    }

    private void OnRxValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var data = args.CharacteristicValue.ToArray();
            if (data.Length == 0) return;
            lock (_lock)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    _framer.Add(data[i]);
                    while (_codec.TryDecode(_framer, out var frame))
                        _rxQueue.Enqueue(frame);
                }
            }
        }
        catch (Exception ex) { Log.Ex("GATT", "OnRxValueChanged", ex); }
    }

    /// <summary>编码帧并通过 TX 特征写入（WriteWithoutResponse，3s 超时）。</summary>
    public void Send(ushort cmd, byte[] payload)
    {
        var tx = _txChar;
        if (tx == null) { Log.D("GATT", $"Send cmd=0x{cmd:X4} 失败: TX 特征未就绪"); return; }

        byte[] bytes;
        lock (_lock) { bytes = _codec.Encode(cmd, payload); }
        try
        {
            var writer = new DataWriter();
            writer.WriteBytes(bytes);
            var buffer = writer.DetachBuffer();
            // 用 WRITE_TYPE_NO_RESPONSE
            RunSync(async () =>
            {
                await tx.WriteValueAsync(buffer, GattWriteOption.WriteWithoutResponse);
                return true;
            }, 3000);
            Log.D("GATT", $"Send cmd=0x{cmd:X4} payload={payload?.Length ?? 0}B -> {bytes.Length}B");
        }
        catch (Exception ex) { Log.Ex("GATT", $"Send cmd=0x{cmd:X4}", ex); }
    }

    /// <summary>取出已入队帧交付上层（通知异步入队，这里同步取出交付）。</summary>
    public void Poll(int timeoutMs)
    {
        // 通知是异步回调，这里在时间预算内取出已入队的帧交付上层
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            while (_rxQueue.TryDequeue(out var frame))
            {
                Log.D("GATT", $"Poll: 交付帧 cmd=0x{frame.Cmd:X4} payload={frame.Payload.Length}B");
                FrameReceived?.Invoke(frame);
            }
            if (!IsConnected) return;
            if (DateTime.UtcNow >= end) break;
            Thread.Sleep(20);
        }
        // 收尾：交付循环末尾可能刚入队的帧
        while (_rxQueue.TryDequeue(out var frame))
        {
            Log.D("GATT", $"Poll: 交付帧 cmd=0x{frame.Cmd:X4} payload={frame.Payload.Length}B");
            FrameReceived?.Invoke(frame);
        }
    }

    private void OnDisconnected()
    {
        if (!IsConnected) return;
        IsConnected = false;
        Disconnected?.Invoke();
    }

    /// <summary>断开连接并释放 BLE 资源。</summary>
    public void Close()
    {
        IsConnected = false;
        Cleanup();
    }

    /// <summary>逐一解绑事件 + 释放特征/服务/设备对象。</summary>
    private void Cleanup()
    {
        lock (_lock)
        {
            try { if (_rxChar != null) _rxChar.ValueChanged -= OnRxValueChanged; } catch { }
            try { if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            _rxChar = null;
            _txChar = null;
            try { _device?.Dispose(); } catch { }
            _device = null;
        }
    }

    /// <summary>在给定超时内同步等待一个异步操作，超时返回 false。超时与异常分别记日志以便定位。</summary>
    private static bool RunSync(Func<System.Threading.Tasks.Task<bool>> op, int timeoutMs)
    {
        Task<bool>? task = null;
        try
        {
            task = op();
            if (!task.Wait(timeoutMs))
            {
                task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                Log.D("GATT", $"RunSync: 超时 (>{timeoutMs}ms)");
                return false;
            }
            return task.Result;
        }
        catch (Exception ex) { Log.Ex("GATT", "RunSync", ex); return false; }
    }

    /// <summary>释放 BLE 传输资源（幂等）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
    }
}
