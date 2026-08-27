using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control.Abstractions;

// ControlManager 通过名称优先级选择后端工厂；通信层只提供原始字节连接，不依赖任何品牌控制类。
public interface IBrandManagerFactory
{
    string Brand { get; }

    // 每个品牌后端声明自己的经典蓝牙服务 UUID，供控制层逐个验证连接。
    Guid ServiceId { get; }

    // 服务 UUID 的判别强度：DedicatedService = 品牌专属 UUID（命中即可强烈指向该品牌）；
    // GenericSpp = 标准 SPP 00001101 之类的通用 UUID（几乎所有串口蓝牙设备都会应答 RFCOMM
    // 建链，命中不能作为品牌证据）。通用 UUID 的品牌在候选排序中降级，且其 CreateAsync
    // 必须完成协议级握手验证（收到品牌专属协议响应）才允许建立会话，否则抛
    // ChannelUnusableException 让控制层切换下一品牌——这是防止“华为探测连上 vivo 的
    // SPP 通道后误锁会话”的契约。
    BrandProbeEvidence ProbeEvidence => BrandProbeEvidence.DedicatedService;

    // 判断蓝牙名称是否属于当前品牌，用于确定首个待验证的服务 UUID。
    bool IsCandidateName(string? deviceName);

    // 按设备名解析该品牌的“首选 RFCOMM 通道”（0=走 SDP 端口 0 解析）。
    // 部分型号的控制服务固定在指定通道（如华为 6i/Pro/Pro2/Pro3/Pro5/SE2/SE4/Studio/FreeClip2/LacePro2=1），
    // SDP 缓存缺失时直连该通道可自愈。默认 0，仅识别到具体型号且需要固定通道的品牌覆写。
    int GetPreferredChannel(string? deviceName) => 0;

    Task<IBrandManager> CreateAsync(
        DeviceConnectionPlan plan,
        IRawConnection connection,
        CancellationToken cancellationToken);
}

// 探测证据强度：专属服务 UUID 优先于通用 SPP 兜底。
public enum BrandProbeEvidence
{
    DedicatedService,
    GenericSpp,
}
