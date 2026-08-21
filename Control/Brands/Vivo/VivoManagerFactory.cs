using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Assets.Vivo;
using OppoPodsManager.Control.Brands.Vivo.Models;
namespace OppoPodsManager.Control.Brands.Vivo;
// vivo / iQOO 品牌后端工厂；ControlManager 负责选择它，通信层不引用 VivoManager。
public sealed class VivoManagerFactory : IBrandManagerFactory
{
    private readonly VivoModelCatalog _modelCatalog;
    public VivoManagerFactory(VivoModelCatalog? modelCatalog = null)
    {
        _modelCatalog = modelCatalog ?? VivoDeviceModelData.LoadCatalog();
    }
    public string Brand => "Vivo";
    public Guid ServiceId => VivoConstants.VivoServiceId;
    public bool IsCandidateName(string? deviceName)
        => _modelCatalog.Find(deviceName) is not null || VivoModels.IsFamilyName(deviceName);
    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        // 已识别项目代号时，按官方显示名选择协议画像，避免 DPD 名称遗漏型号特例。
        var recognizedModel = _modelCatalog.Find(plan.Candidate.DisplayName);
        var profile = VivoModels.SelectProfile(recognizedModel?.DisplayName ?? plan.Candidate.DisplayName);
        var link = new ConnectionLink(connection, new VivoFrameCodec(profile.GaiaVersion), new FrameRouter());
        var manager = new VivoManager(_modelCatalog);
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
