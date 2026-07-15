using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace OppoPodsManager;

/// <summary>
/// BLE GATT 传输（WinRT Windows.Devices.Bluetooth）。
///   Service 0000079A-…、TX(Write) 0000079B-…、RX(Notify) 0000079C-…、CCCD 2902。
/// 帧格式用 GattFrameCodec（melody 5 字节头，无 SPP 0xAA 外壳）。
///
/// 仅按 WindowsConnectionTransport 已选定的目标地址打开设备，不执行全局名称或服务枚举，
/// 防止多副同品牌耳机时回退到错误设备。仅作为目标 RFCOMM 路径失败后的最后回退。
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsGattTransport : IPodTransport
{
    // melody 私有服务/特征 UUID（后缀 -D102-11E1-9B23-00025B00A5A5）
    private static readonly Guid ServiceUuid = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private static readonly Guid TxCharUuid  = new("0000079B-D102-11E1-9B23-00025B00A5A5"); // Write
    private static readonly Guid RxCharUuid  = new("0000079C-D102-11E1-9B23-00025B00A5A5"); // Notify

    private const int ConnectTimeoutMs = 8000;

    private readonly ulong _targetAddress;
    private readonly string _targetName;
    private readonly IFrameCodec _codec = new GattFrameCodec();
    private readonly List<byte> _framer = new();
    private readonly ConcurrentQueue<PodFrame> _rxQueue = new();
    private readonly object _lock = new();

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _txChar;
    private GattCharacteristic? _rxChar;
    private CancellationTokenSource? _connectCts;
    private bool _disposed;

    public WindowsGattTransport(ulong targetAddress, string targetName)
    {
        if (targetAddress == 0) throw new ArgumentOutOfRangeException(nameof(targetAddress));
        _targetAddress = targetAddress;
        _targetName = targetName;
    }

    public string? DeviceName { get; private set; }
    public string? LastError { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action<PodFrame>? FrameReceived;
    public event Action? Disconnected;

    /// <summary>按固定目标地址打开 BLE 设备 → 取 GATT 服务/特征 → 订阅通知。</summary>
    public bool Connect()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowsGattTransport));
            _connectCts?.Cancel();
            _connectCts?.Dispose();
            _connectCts = cts = new CancellationTokenSource(ConnectTimeoutMs);
        }
        try
        {
            Log.D("GATT", $"Connect: 开始 target={_targetAddress:X12}");
            if (!ConnectAsyncCore(cts.Token).GetAwaiter().GetResult())
            {
                Cleanup();
                if (string.IsNullOrEmpty(LastError)) LastError = "目标 GATT 连接失败";
                Log.Result("GATT", "Connect", false, LastError);
                return false;
            }

            lock (_lock)
            {
                if (!ReferenceEquals(_connectCts, cts) || cts.IsCancellationRequested)
                {
                    LastError = "GATT 连接已取消";
                    CleanupLocked();
                    return false;
                }
                IsConnected = true;
            }
            LastError = null;
            Log.Result("GATT", "Connect", true, $"name=\"{DeviceName}\"");
            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = cts.IsCancellationRequested ? "GATT 连接已取消或超时" : "GATT 连接已取消";
            Cleanup();
            Log.Result("GATT", "Connect", false, LastError);
            return false;
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
    private async Task<bool> ConnectAsyncCore(CancellationToken cancellationToken)
    {
        BluetoothLEDevice? device = null;
        GattDeviceService? service = null;
        try
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(_targetAddress).AsTask(cancellationToken);
            if (device == null)
            {
                LastError = $"目标地址 {_targetAddress:X12} 无法打开为 BLE 设备";
                return false;
            }

            if (device.BluetoothAddress != _targetAddress)
            {
                LastError = $"GATT 地址不匹配：目标={_targetAddress:X12} 实际={device.BluetoothAddress:X12}";
                return false;
            }

            var deviceName = !string.IsNullOrEmpty(device.Name) ? device.Name : _targetName;
            Log.D("GATT", $"Connect: 已打开目标 BLE 设备 name=\"{deviceName}\" addr={device.BluetoothAddress:X12} 连接态={device.ConnectionStatus}");

            var svcResult = await device.GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            Log.D("GATT", $"Connect: 取 melody 服务 status={svcResult.Status} 服务数={svcResult.Services.Count}");
            if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
            {
                LastError = $"目标设备未发现 melody GATT 服务 (status={svcResult.Status})";
                return false;
            }
            service = svcResult.Services[0];

            var txResult = await service.GetCharacteristicsForUuidAsync(TxCharUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            var rxResult = await service.GetCharacteristicsForUuidAsync(RxCharUuid, BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0 ||
                rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0)
            {
                LastError = "目标设备未发现 TX/RX 特征";
                return false;
            }
            var tx = txResult.Characteristics[0];
            var rx = rxResult.Characteristics[0];

            var cccd = await rx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken);
            if (cccd != GattCommunicationStatus.Success)
            {
                LastError = $"写 CCCD 失败 (status={cccd})，通常是未配对/未加密";
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _device = device;
                _service = service;
                _txChar = tx;
                _rxChar = rx;
                DeviceName = deviceName;
                device = null;
                service = null;
                _rxChar.ValueChanged += OnRxValueChanged;
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;
                _framer.Clear();
                while (_rxQueue.TryDequeue(out _)) { }
            }
            Log.D("GATT", "Connect: 目标服务/特征就绪，通知已开启");
            return true;
        }
        finally
        {
            try { service?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
        }
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
        try { _connectCts?.Cancel(); } catch { }
        Cleanup();
    }

    /// <summary>逐一解绑事件 + 释放特征/服务/设备对象。</summary>
    private void Cleanup()
    {
        lock (_lock)
            CleanupLocked();
    }

    private void CleanupLocked()
    {
        try { if (_rxChar != null) _rxChar.ValueChanged -= OnRxValueChanged; } catch { }
        try { if (_device != null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
        _rxChar = null;
        _txChar = null;
        try { _service?.Dispose(); } catch { }
        _service = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
        try { _connectCts?.Dispose(); } catch { }
        _connectCts = null;
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
