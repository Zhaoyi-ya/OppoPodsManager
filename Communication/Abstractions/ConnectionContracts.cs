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
    int Channel,
    // 是否允许在“服务 UUID 端口 0”失败后退化到裸通道 1/15。
    // 裸通道能建链但不回任何 GAIA 帧时是死通道，握手阶段识别到死通道后可关掉它强制只用端口 0 重试。
    bool AllowBareChannels = true);

// 品牌管理器在握手/探测阶段判定当前 RFCOMM 通道不可用（典型为落到非 GAIA 裸通道，
// 能建链但不回任何 GAIA 帧）。连接层应据此放弃该通道，按既定策略重试其它候选端口。
public sealed class ChannelUnusableException : Exception
{
    public ChannelUnusableException(string message) : base(message) { }
    public ChannelUnusableException(string message, Exception inner) : base(message, inner) { }
}

// RFCOMM 连接尝试全部失败。WsaError 携带底层 Winsock 错误码（如 10064=主机不可达），
// 用于区分“设备暂不可达（环境/瞬态，可自愈）”与真正的配置/程序错误，
// 让控制层给出针对性提示并依赖设备重新连接后的自动重连，而不是抛出一个吓人的泛型异常。
public sealed class BluetoothConnectException : Exception
{
    public int? WsaError { get; }

    public BluetoothConnectException(string message, int? wsaError = null) : base(message)
        => WsaError = wsaError;

    public BluetoothConnectException(string message, int? wsaError, Exception inner) : base(message, inner)
        => WsaError = wsaError;

    // 把常见的 Winsock 连接错误码翻译成可读文字，便于日志与用户提示判断“设备不可达”还是“被占用/超时”。
    public static string DescribeWsa(int wsa)
    {
        return wsa switch
        {
            10060 => "连接超时",
            10061 => "连接被拒绝",
            10064 => "主机不可达（设备未连接/不在范围内）",
            10065 => "无路由到主机",
            _ => "未知",
        };
    }
}

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
