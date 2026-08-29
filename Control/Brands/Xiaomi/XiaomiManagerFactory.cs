using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Xiaomi;

// 小米品牌后端工厂。ControlManager 通过名称匹配选中本工厂后，通信层用 ServiceId 做 SDP
// 解析出小米 RFCOMM 通道（参考 EarbudsConstants.kt：XIAOAI SPP UUID = 00001101-…-008584D01810），
// 无需新增 BLE 传输层。
public sealed class XiaomiManagerFactory : IBrandManagerFactory
{
    // 小米 XIAOAI SPP 服务的真实 SDP UUID（非标准 SPP，底座为 008584D01810）。
    public static readonly Guid XiaomiServiceId = new("00001101-0000-1000-8000-008584D01810");

    public XiaomiManagerFactory()
    {
    }

    public string Brand => "XIAOMI";

    public Guid ServiceId => XiaomiServiceId;

    public bool IsCandidateName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;

        return deviceName.Contains("Xiaomi", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Redmi", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Mi Buds", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Mi True", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("FlipBuds", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Air", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        var link = new ConnectionLink(connection, new XiaomiFrameCodec(), new FrameRouter());
        var manager = new XiaomiManager();
        try
        {
            await manager.StartSessionAsync(plan.Candidate.DisplayName, link, cancellationToken);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync();
            await link.DisposeAsync();
            throw;
        }
    }
}
