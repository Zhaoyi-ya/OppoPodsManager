using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Transport;
namespace OppoPodsManager.Control.Brands.Edifier;
// 漫步者（Edifier）品牌后端工厂；ControlManager 负责选择它，通信层不引用 EdifierManager。
public sealed class EdifierManagerFactory : IBrandManagerFactory
{
    public string Brand => "Edifier";
    public Guid ServiceId => EdifierConstants.EdifierSppServiceId;
    public bool IsCandidateName(string? deviceName) => EdifierModels.IsFamilyName(deviceName);
    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        var link = new ConnectionLink(connection, new EdifierFrameCodec(), new FrameRouter());
        var manager = new EdifierManager();
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
