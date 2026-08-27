using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Huawei;

// 华为（Huawei）品牌后端工厂；ControlManager 负责选择它，通信层不引用 HuaweiManager。
public sealed class HuaweiManagerFactory : IBrandManagerFactory
{
    public string Brand => "Huawei";
    public Guid ServiceId => HuaweiConstants.HuaweiSppServiceId;
    // 标准 SPP UUID：任何串口蓝牙设备都可能应答建链（如 vivo），不能作为品牌证据；
    // StartSessionAsync 内置协议握手验证兜底（见 IBrandManagerFactory.ProbeEvidence 注释）。
    public BrandProbeEvidence ProbeEvidence => BrandProbeEvidence.GenericSpp;
    public bool IsCandidateName(string? deviceName) => HuaweiModels.IsFamilyName(deviceName);
    // 识别到具体型号后返回其首选 SPP 通道（6i/Pro/Pro2/Pro3/Pro5/SE2/SE4/Studio/FreeClip2/LacePro2=1）。
    public int GetPreferredChannel(string? deviceName)
        => HuaweiModels.GetCapabilities(HuaweiModels.DetectRoute(deviceName)).PreferredSppChannel;
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
