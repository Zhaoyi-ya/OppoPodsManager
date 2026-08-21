using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Huawei;

// 华为（Huawei）品牌后端工厂；ControlManager 负责选择它，通信层不引用 HuaweiManager。
public sealed class HuaweiManagerFactory : IBrandManagerFactory
{
    public string Brand => "Huawei";
    public Guid ServiceId => HuaweiConstants.HuaweiSppServiceId;
    public bool IsCandidateName(string? deviceName) => HuaweiModels.IsFamilyName(deviceName);
    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        var link = new ConnectionLink(connection, new HuaweiFrameCodec(), new FrameRouter());
        var manager = new HuaweiManager();
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
