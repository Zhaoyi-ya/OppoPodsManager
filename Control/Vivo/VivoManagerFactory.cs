using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Oppo.Managers;

namespace OppoPodsManager.Control.Vivo;

// vivo / iQOO 品牌后端工厂；ControlManager 负责选择它，通信层不引用 VivoManager。
public sealed class VivoManagerFactory : IBrandManagerFactory
{
    public string Brand => "Vivo";

    public async Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken)
    {
        // 按设备名选择 GAIA 协议画像，决定降噪命令使用的 GAIA 版本。
        var profile = VivoModels.SelectProfile(plan.Candidate.DisplayName);
        var link = new ConnectionLink(connection, new VivoFrameCodec(profile.GaiaVersion), new FrameRouter());
        var manager = new VivoManager();
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
