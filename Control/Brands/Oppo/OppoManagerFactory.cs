using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Commands;
using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
namespace OppoPodsManager.Control.Brands.Oppo;
// OPPO 品牌后端工厂；ControlManager 负责选择它，通信层不引用 OppoManager。
public sealed class OppoManagerFactory : IBrandManagerFactory
{
    // Melody 协议的官方 RFCOMM 服务 UUID，由 OPPO 后端而非平台传输层拥有。
    public static readonly Guid MelodyServiceId = new("0000079A-D102-11E1-9B23-00025B00A5A5");
    private readonly ModelCatalog? _modelCatalog;
    public OppoManagerFactory(ModelCatalog? modelCatalog = null)
    {
        _modelCatalog = modelCatalog;
    }
    public string Brand => "OPPO";
    public Guid ServiceId => MelodyServiceId;
    public bool IsCandidateName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;
        return deviceName.Contains("OPPO", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("OnePlus", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Enco", StringComparison.OrdinalIgnoreCase);
    }
    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        var link = new ConnectionLink(connection, new FrameCodec(), new FrameRouter());
        var manager = new OppoManager(_modelCatalog);
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
