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

    // 判断规则刻意收窄：原先的 Contains("Air") 会命中 AirPods 及任何含 "Air" 的第三方设备
    // （如 Soundcore Liberty Air），虽然品牌排序里 APPLE 在前而暂时未暴雷，但属依赖排序偶然性。
    // 官方包内的识别依据是 BLE 广播（公司 ID 0x2717 / 过滤串 "XM" "XMSMART"），
    // 名称仅作辅助；此处只保留确属小米系的名称片段。
    private static readonly string[] NameMarkers =
    {
        "Xiaomi", "Redmi", "Mi Buds", "Mi True", "FlipBuds",
        "Mi Air", "Xiaomi Air", "Redmi Air", "Air2 SE", "Air2 Pro",
    };

    public bool IsCandidateName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;

        foreach (var marker in NameMarkers)
        {
            if (deviceName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
