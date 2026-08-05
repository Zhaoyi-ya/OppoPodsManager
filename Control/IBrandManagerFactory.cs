using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control;

// ControlManager 按品牌选择后端工厂；通信层只提供原始字节连接，不依赖任何品牌控制类。
public interface IBrandManagerFactory
{
    string Brand { get; }

    Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken);
}
