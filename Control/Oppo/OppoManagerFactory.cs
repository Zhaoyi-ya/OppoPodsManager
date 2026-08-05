using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Oppo;

// OPPO 品牌后端工厂；ControlManager 负责选择它，通信层不引用 OppoManager。
public sealed class OppoManagerFactory : IBrandManagerFactory
{
    private readonly ModelCatalog? _modelCatalog;

    public OppoManagerFactory(ModelCatalog? modelCatalog = null)
    {
        _modelCatalog = modelCatalog;
    }

    public string Brand => "OPPO";

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
