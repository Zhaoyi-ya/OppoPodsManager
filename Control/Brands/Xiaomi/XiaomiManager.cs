using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Communication.Abstractions;

namespace OppoPodsManager.Control.Brands.Xiaomi;

// MVP：仅验证「发现 → RFCOMM 连接 → 电量读取」通路。
// 能力集全部置空（DeviceCapability.Unknown），UI 据此不显示任何高级功能控件（降噪/EQ/手势等）。
// 认证握手、降噪、EQ、手势等协议在真机验证后逐步补全。
public sealed class XiaomiManager : BrandManagerBase, IBrandManager
{
    private readonly ModelCatalog? _modelCatalog;
    private DeviceCapability _capability = DeviceCapability.Unknown;
    private ConnectionLink? _link;

    public XiaomiManager(ModelCatalog? modelCatalog = null)
    {
        _modelCatalog = modelCatalog;
        State.Changed += PublishState;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;
    public BusinessSnapshot Snapshot => State.Snapshot();
    public DeviceCapability Capability { get => _capability; private set => _capability = value; }
    public bool CanManageMultiDevice => false;
    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public IEqualizerProfile EqualizerProfile => NullEqualizerProfile.Instance;
    public IReadOnlyList<GestureEntry> GestureEntries => Array.Empty<GestureEntry>();

    // 能力全空：UI 不渲染任何高级功能控件；电量由 Snapshot 直接驱动，连接后即可见。
    public BrandPresentation Presentation
    {
        get
        {
            var emptyControls = new HashSet<string>();
            var emptyStates = new Dictionary<string, bool>();
            return new BrandPresentation(
                Capability.ModelName,
                Capability.IsKnownModel,
                false,
                false,
                false,
                false,
                Array.Empty<ushort>(),
                CustomEqualizerMinimumGain,
                CustomEqualizerMaximumGain,
                Array.Empty<string>(),
                emptyControls,
                emptyStates,
                emptyStates,
                Array.Empty<NoiseOptionModel>(),
                string.Empty);
        }
    }

    public void SetInteractivePolling(bool enabled) => InteractivePolling = enabled;

    public void SetManualModel(string? modelName)
    {
        // MVP：无型号库识别，忽略手动型号覆盖。
    }

    // ---- MVP 会话：连接即标记已连接，并尝试读取电量（电池命令 0x0b，响应命令 0x07）----
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _link = link;
        link.Disconnected += OnLinkDisconnected;
        State.SetConnected(deviceName);
        ApplicationLog.Current?.Info("Xiaomi", $"已建立 RFCOMM 会话：{deviceName}，开始尝试读取电量。");
        try
        {
            var response = await link.RequestAsync(0x0B, 0x07, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, cancellationToken);
            if (response is not null && response.Payload.Length >= 7)
            {
                var p = response.Payload.Span;
                // 小米电量响应帧：magic FEDCBAC4 + flag + len + cmd(0x07) + 负载。
                // 负载 = 02 02 04 07 L R C（对齐 MiBudsClient 的 BATTERY_PATTERN=02020407，L/R/C 在模式之后）。
                State.SetBattery(
                    new BatteryLevel(Clamp(p[4]), false),
                    new BatteryLevel(Clamp(p[5]), false),
                    new BatteryLevel(Clamp(p[6]), false));
                ApplicationLog.Current?.Info("Xiaomi", $"电量读取成功：L={p[4]} R={p[5]} C={p[6]}。");
            }
            else
            {
                ApplicationLog.Current?.Info("Xiaomi",
                    "电量请求未收到有效响应（命令通道可能无需认证，或帧/响应格式待真机校准）。");
            }
        }
        catch (Exception ex)
        {
            ApplicationLog.Current?.Error("Xiaomi", $"电量读取失败：{ex.Message}", ex);
        }

        // 会话握手验证：电量查询没有收到过“匹配命令字的应答” = 死通道（其它品牌设备的裸通道会发
        // 非协议噪声字节，仅凭 LastReceiveTicks==0 拦不住），改用 LastResponseTicks==0 判死通道，
        // 抛 ChannelUnusableException 让 Discovery 切换下一品牌，而不是建一个空会话假在线。
        // 注：真小米若要求认证而认证未实装，同样无协议应答——此时会话本就无任何可用数据，
        // 如实失败优于假连接；认证实装后本检查自然放行。
        if (link.LastResponseTicks == 0)
            throw new ChannelUnusableException(
                $"小米协议握手未收到任何应答（电量查询无应答），疑似落到非小米通道：{deviceName}。");
    }

    private static byte Clamp(byte value) => value > 100 ? (byte)100 : value;

    public async Task DisconnectAsync()
    {
        if (_link is not null)
        {
            _link.Disconnected -= OnLinkDisconnected;
            await _link.DisposeAsync();
        }

        _link = null;
        _capability = DeviceCapability.Unknown;
        State.Reset();
    }

    public ValueTask DisposeAsync()
    {
        State.Changed -= PublishState;
        return new ValueTask(DisconnectAsync());
    }

    private void PublishState(object? sender, BusinessSnapshot snapshot) => StateChanged?.Invoke(this, snapshot);

    private void OnLinkDisconnected(object? sender, EventArgs args)
    {
        _link = null;
        _capability = DeviceCapability.Unknown;
        State.Reset();
    }

    // ---- 能力未实现的安全兜底：UI 不会触发（Presentation 不显示对应控件），调用亦返回 false ----
    private Task<bool> Unsupported() => Task.FromResult(false);

    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken) => Unsupported();
    public Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken) => Unsupported();

    public bool IsValidCustomEqualizerName(string name) => false;
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => NullEqualizerProfile.Instance.CreateCustomEqualizerEntry(id, name, gains);
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
        => NullEqualizerProfile.Instance.AlignCustomEqualizerGains(entry);
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => MultiDevicePolicy.BuildDisplayState(State.Snapshot().MultiDevice, hiddenAddresses);
}
