using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Abstractions;

namespace OppoPodsManager.Control.Brands.Apple;

// AirPods 品牌后端工厂。AirPods 仅靠 BLE 广播被发现，因此本工厂仅接受 ble-adv 传输；
// 落到 RFCOMM 等其它传输的候选（如系统也将 AirPods 列为已连接经典设备）直接判通道不可用，
// 让控制层快速回退，不浪费连接尝试。
public sealed class AppleManagerFactory : IBrandManagerFactory
{
    public string Brand => "Apple";

    // AirPods 无 RFCOMM 服务 UUID；用 Guid.Empty 占位，匹配由 IsCandidateName/传输决定。
    public Guid ServiceId => Guid.Empty;

    public bool IsCandidateName(string? deviceName)
        => !string.IsNullOrWhiteSpace(deviceName)
            && deviceName.Contains("airpods", StringComparison.OrdinalIgnoreCase);

    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(plan.Options.Transport, TransportNames.BleAdvertisement, StringComparison.OrdinalIgnoreCase))
            throw new ChannelUnusableException(
                $"Apple 品牌仅支持 BLE 广播传输({TransportNames.BleAdvertisement})，当前传输为 {plan.Options.Transport}。");

        if (connection is not IAppleAdvertisementProvider provider)
            throw new ChannelUnusableException("Apple 连接未提供 BLE 广播数据源。");

        var manager = new AppleManager();
        try
        {
            await manager.StartSessionAsync(plan.Candidate.DisplayName, provider, cancellationToken);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync();
            throw;
        }
    }
}
