using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Brands.Apple;

// AirPods（Apple）会话管理。AirPods 走 BLE 广播而非 RFCOMM，只读状态（电量/充电/佩戴/型号/盒盖）
// 来自广播明文厂商数据；控制通道(L2CAP/ATT，降噪/手势)在 Windows 上暂未实现，故相关写操作一律返回不支持。
// 状态更新为事件驱动：订阅 IAppleAdvertisementProvider.AdvertisementUpdated，每次新广播到达即重解。
internal sealed class AppleManager : IBrandManager
{
    private readonly BusinessState _state = new();
    private IAppleAdvertisementProvider? _provider;
    private string? _deviceName;
    private EventHandler<byte[]>? _onUpdated;
    private bool _disposed;

    public AppleManager()
    {
        _state.Changed += OnStateChanged;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;

    public BusinessSnapshot Snapshot => _state.Snapshot();

    // AirPods 不使用型号能力表；UI 依据 Presentation 而非此字段决定可见性。
    public DeviceCapability Capability => DeviceCapability.Unknown;

    public BrandPresentation Presentation => BuildPresentation();

    public bool CanManageMultiDevice => false;

    public void SetInteractivePolling(bool enabled)
    {
        // 状态来自广播事件，不依赖轮询。交互态变化无需调整。
    }

    public Task DisconnectAsync()
    {
        if (_provider is not null && _onUpdated is not null)
            _provider.AdvertisementUpdated -= _onUpdated;
        _provider = null;
        _onUpdated = null;
        _state.Reset();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return new ValueTask(Task.CompletedTask);
        _disposed = true;
        _state.Changed -= OnStateChanged;
        return new ValueTask(DisconnectAsync());
    }

    public void SetManualModel(string? modelName)
    {
        // AirPods 型号由广播识别，无手动覆盖需求。
    }

    // ---- OPPO/其他品牌专属功能：统一返回不支持（控制通道未实现）----
    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken) => Task.FromResult(false);

    // ---- 降噪：AirPods 支持但仅经 L2CAP 控制通道（Windows 未实现），写操作标记不支持 ----
    public Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken) => Task.FromResult(false);

    // ---- 触控手势：AirPods 手势经 AACP（L2CAP）配置，Windows 未实现 ----
    public IReadOnlyList<GestureEntry> GestureEntries => [];
    public Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken)
        => Task.FromResult(false);

    // ---- 均衡器：AirPods 无 EQ 写通道 ----
    public IEqualizerProfile EqualizerProfile => NullEqualizerProfile.Instance;
    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public bool IsValidCustomEqualizerName(string name) => false;
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, -6, 6, [], []);
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry) => [];
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => new([], []);

    // ---- 会话建立 ----
    public async Task StartSessionAsync(string deviceName, IAppleAdvertisementProvider provider, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _deviceName = deviceName;
        _provider = provider;
        _onUpdated = OnAdvertisementUpdated;
        provider.AdvertisementUpdated += _onUpdated;

        _state.SetConnected(deviceName);
        // 若发现时已有广播数据，立即解析首帧，避免界面短暂空白。
        var initial = provider.LatestData;
        if (initial is not null)
            ApplyAdvertisement(initial);
    }

    private void OnAdvertisementUpdated(object? sender, byte[] data)
        => ApplyAdvertisement(data);

    private void ApplyAdvertisement(byte[] data)
    {
        if (!AppleAdvertisementParser.TryParse(data, out var status))
            return;

        BatteryLevel? left = status.LeftBattery is { } l ? new BatteryLevel((byte)l, status.LeftCharging) : null;
        BatteryLevel? right = status.RightBattery is { } r ? new BatteryLevel((byte)r, status.RightCharging) : null;
        BatteryLevel? chargingCase = status.CaseBattery is { } c ? new BatteryLevel((byte)c, status.CaseCharging) : null;
        _state.SetBattery(left, right, chargingCase);

        var leftWear = status.LeftInEar ? EarWearState.Worn : EarWearState.Removed;
        var rightWear = status.RightInEar ? EarWearState.Worn : EarWearState.Removed;
        _state.SetWear(new WearSnapshot(leftWear, rightWear));

        if (status.ModelName is { } model)
        {
            // 用解析到的型号名作为展示名与型号，同时保留厂商为 Apple。
            _state.SetIdentity(new DeviceIdentity("Apple", model, model, null, null));
        }
    }

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
        => StateChanged?.Invoke(this, snapshot);

    private BrandPresentation BuildPresentation()
    {
        var snapshot = _state.Snapshot();
        var modelName = snapshot.Identity?.ModelName ?? _deviceName ?? "AirPods";
        var isKnown = snapshot.Identity?.ModelName is not null;
        return new BrandPresentation(
            modelName,
            isKnown,
            false,  // SupportsSpatialAudio
            false,  // SupportsCustomEqualizer
            false,  // SupportsNoiseCancellation（控制通道 Windows 未实现）
            false,  // CanManageMultiDevice
            [],
            BrandPresentation.DefaultCustomEqMinimumGain,
            BrandPresentation.DefaultCustomEqMaximumGain,
            [],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            Array.Empty<NoiseOptionModel>(),
            "off");
    }
}
