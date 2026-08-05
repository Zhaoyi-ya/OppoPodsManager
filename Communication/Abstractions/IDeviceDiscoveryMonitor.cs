namespace OppoPodsManager.Communication.Abstractions;

// 支持实时增加/减少事件的设备发现器；品牌发现器按自己的 UUID 和型号规则实现。
public interface IDeviceDiscoveryMonitor : IDeviceDiscovery, IDisposable
{
    event EventHandler<DeviceCandidatesChangedEventArgs>? DevicesChanged;

    void Start();
}

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
