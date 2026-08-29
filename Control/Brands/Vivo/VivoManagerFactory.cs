using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Assets.Vivo;
using OppoPodsManager.Control.Brands.Vivo.Models;
namespace OppoPodsManager.Control.Brands.Vivo;
// vivo / iQOO 品牌后端工厂；ControlManager 负责选择它，通信层不引用 VivoManager。
public sealed class VivoManagerFactory : IBrandManagerFactory
{
    private readonly Lazy<VivoModelCatalog> _modelCatalog;
    public VivoManagerFactory(VivoModelCatalog? modelCatalog = null)
    {
        // 惰性加载：仅在首次需要（设备名称匹配或会话创建）时才解析 vivo 设备目录，
        // 避免 App 启动时一次性构造全部品牌工厂导致 vivo 目录被提前载入内存。
        _modelCatalog = new Lazy<VivoModelCatalog>(() => modelCatalog ?? VivoDeviceModelData.LoadCatalog());
    }
    public string Brand => "Vivo";
    public Guid ServiceId => VivoConstants.VivoServiceId;
    public bool IsCandidateName(string? deviceName)
        => _modelCatalog.Value.Find(deviceName) is not null || VivoModels.IsFamilyName(deviceName);
    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        // 已识别项目代号时，按官方显示名选择协议画像，避免 DPD 名称遗漏型号特例。
        var recognizedModel = _modelCatalog.Value.Find(plan.Candidate.DisplayName);
        var profile = VivoModels.SelectProfile(recognizedModel?.DisplayName ?? plan.Candidate.DisplayName);
        var link = new ConnectionLink(connection, new VivoFrameCodec(profile.GaiaVersion), new FrameRouter());
        var manager = new VivoManager(_modelCatalog.Value);
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
