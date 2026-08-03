using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Oppo.Features;

namespace OppoPodsManager.Control;

public interface IBrandManager : IAsyncDisposable
{
    event EventHandler<BusinessSnapshot>? StateChanged;

    BusinessSnapshot Snapshot { get; }

    DeviceCapability Capability { get; }

    // 提供该品牌管理器支持的型号清单，供型号选择和能力解析共用。
    IReadOnlyList<string> ModelNames { get; }

    // 提供官方型号白名单中的品牌、系列和型号层级。
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree { get; }

    // 按官方型号目录定位型号所属的品牌和系列。
    ModelCatalogLocation? FindModelLocation(string? modelName);

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
}
