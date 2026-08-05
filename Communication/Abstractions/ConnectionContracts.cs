namespace OppoPodsManager.Communication.Abstractions;

// 跨平台通信层向控制层公开的设备发现、传输选择和原始字节连接契约。
public sealed record DeviceCandidate(
    string StableId,
    string PlatformId,
    string? BluetoothAddress,
    string DisplayName,
    IReadOnlyCollection<Guid> ServiceIds,
    IReadOnlyCollection<string> Transports);

// 描述一次连接尝试所需的传输实现、服务标识和可选通道。
public sealed record ConnectionOptions(
    string Transport,
    Guid? ServiceId,
    int Channel);

// 平台原始连接仅负责字节收发和连接生命周期，不包含品牌协议。
public interface IRawConnection : IAsyncDisposable
{
    bool IsConnected { get; }

    event EventHandler<ReadOnlyMemory<byte>>? DataReceived;
    event EventHandler? Disconnected;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
}

// 按传输名称创建对应平台原始连接。
public interface IConnectionFactory
{
    string Transport { get; }

    Task<IRawConnection> OpenAsync(
        DeviceCandidate candidate,
        ConnectionOptions options,
        CancellationToken cancellationToken);
}

// 枚举当前可连接的设备候选项。
public interface IDeviceDiscovery
{
    Task<IReadOnlyList<DeviceCandidate>> DiscoverAsync(CancellationToken cancellationToken);
}

// 支持实时变更通知的发现器；品牌筛选由各平台实现负责。
public interface IDeviceDiscoveryMonitor : IDeviceDiscovery, IDisposable
{
    event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    void Start();
}

// 提供最新设备候选快照及其递增版本，供控制层忽略过期事件。
public sealed class DeviceCandidatesChangedEventArgs : EventArgs
{
    public DeviceCandidatesChangedEventArgs(IReadOnlyList<DeviceCandidate> devices, long generation = 0)
    {
        Devices = devices;
        Generation = generation;
    }

    public IReadOnlyList<DeviceCandidate> Devices { get; }
    public long Generation { get; }
}
