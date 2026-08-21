using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control.Abstractions;

// ControlManager 通过名称优先级选择后端工厂；通信层只提供原始字节连接，不依赖任何品牌控制类。
public interface IBrandManagerFactory
{
    string Brand { get; }

    // 每个品牌后端声明自己的经典蓝牙服务 UUID，供控制层逐个验证连接。
    Guid ServiceId { get; }

    // 判断蓝牙名称是否属于当前品牌，用于确定首个待验证的服务 UUID。
    bool IsCandidateName(string? deviceName);

    Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken);
}
