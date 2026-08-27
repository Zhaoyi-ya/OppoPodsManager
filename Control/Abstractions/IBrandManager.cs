
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Core.Features;
namespace OppoPodsManager.Control.Abstractions;
public interface IBrandManager : IAsyncDisposable
{
    event EventHandler<BusinessSnapshot>? StateChanged;
    BusinessSnapshot Snapshot { get; }
    DeviceCapability Capability { get; }
    // 向界面公开经过能力交集计算的展示数据，而不是让界面读取协议能力。
    BrandPresentation Presentation { get; }
    bool CanManageMultiDevice { get; }
    void SetInteractivePolling(bool enabled);
    Task DisconnectAsync();
    // 应用层使用统一的设备意图接口，界面无需了解具体品牌协议实现。
    void SetManualModel(string? modelName);
    Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken);
    Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken);
    Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken);
    Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken);
    Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken);
    Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken);
    Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken);
    Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken);
    Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken);
    Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken);
    Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken);
    sbyte CustomEqualizerMinimumGain { get; }
    sbyte CustomEqualizerMaximumGain { get; }
    bool IsValidCustomEqualizerName(string name);
    EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains);
    IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry);
    Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken);
    Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken);
    Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken);
    Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken);
    Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken);
    MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses);
    Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken);
    Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken);
    // 触控手势：品牌无关的展示与下发入口。UI 通过 GestureEntries 动态渲染，不感知品牌差异。
    IReadOnlyList<GestureEntry> GestureEntries { get; }
    Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken);
    /// <summary>设置长按「切换噪声控制」循环的模式集合（MultiCheckbox 勾选结果）。
    /// 默认实现返回 false（协议编码未实现的品牌不下发，仅由覆写的品牌保存状态）。</summary>
    Task<bool> SetLongPressCycleAsync(EarSide ear, GestureSource source, IReadOnlyList<NoiseMode> modes, CancellationToken cancellationToken)
        => Task.FromResult(false);
    /// <summary>设置降噪方向档位（如 FreeBuds 3 的 0-8 级智能降噪方向感）。
    /// 默认实现返回 false（无该能力的品牌不下发）。</summary>
    Task<bool> SetAncDirectionLevelAsync(byte level, CancellationToken cancellationToken)
        => Task.FromResult(false);
    // 均衡器协议档案：解码/编码与预设名解析的跨品牌抽象。UI 通过此接口消费，
    // 不感知具体品牌命令字、负载格式与频段白名单对齐方式。
    IEqualizerProfile EqualizerProfile { get; }

    /// <summary>会话活性探测：返回（最后发送 tick，最后接收 tick）（TickCount64 毫秒）。
    /// 无链路或品牌不适用（如 BLE 广播型 Apple）时返回 null，看门狗将跳过该会话。
    /// 契约：LastSend &gt; LastReceive 且持续无接收超过阈值 ⇒ 视为死会话，由控制层
    /// teardown 并重新探测品牌，避免“会话已建立但设备永不再响应”导致界面卡死。</summary>
    (long LastSendTicks, long LastReceiveTicks)? SessionLiveness => null;
}
