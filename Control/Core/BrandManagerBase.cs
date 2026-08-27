namespace OppoPodsManager.Control.Core;

using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Transport;

// 品牌管理器的公共基类（辅助基类，不直接实现 IBrandManager）：集中维护业务状态聚合
// (BusinessState)、当前活动连接 (ConnectionLink) 与交互轮询开关，并提供统一的
// "取连接 / 断言已连接"辅助。子类只需实现 IBrandManager 的品牌专属意图，并把协议字节的
// 收发委托给 CommandSender。
public abstract class BrandManagerBase
{
    private readonly BusinessState _state = new();

    // 聚合所有业务状态，对外发布不可变快照。
    protected BusinessState State => _state;

    // 当前活动会话的连接；无会话时为 null。
    protected ConnectionLink? Link { get; set; }

    protected bool HasLink => Link is not null;

    // 取活动连接，未连接时抛出，避免每个写方法重复判空。
    protected ConnectionLink RequireLink()
        => Link ?? throw new InvalidOperationException("No device session is active.");

    // 由窗口可见性决定是否执行交互功能的补偿轮询（保留 volatile 语义：UI 线程写、轮询线程读）。
    protected bool InteractivePolling
    {
        get => _interactivePolling;
        set => _interactivePolling = value;
    }

    private volatile bool _interactivePolling;

    // 会话活性：委托 ConnectionLink 的收发打点，供控制层看门狗判定死会话。
    public (long LastSendTicks, long LastReceiveTicks)? SessionLiveness
        => Link is { } link ? (link.LastSendTicks, link.LastReceiveTicks) : null;
}
