using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Gestures;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Control.Equalizers;

namespace OppoPodsManager.Control.Edifier;

// 漫步者（Edifier）TWS / 头戴耳机会话管理。
//
// 漫步者仅支持电量与降噪两类功能（来自 mEDIFIER 参考实现），因此本管理器对 IBrandManager
// 中 OPPO 专属功能（均衡器、空间音频、游戏模式、双设备、查找设备等）一律返回“不支持”，
// 仅电量与降噪提供端到端读写。UI 通过 Presentation.SupportsNoiseCancellation 与 NoiseOptions
// 展示降噪卡片，不会因缺失 OPPO 能力而报错。
//
// 协议层次：复用项目现有的 WindowsRfcommConnection（RFCOMM），因此仅支持提供 SPP 通道的设备
// （W820NB 系列、W200BT 系列等，参考 Klinkore 分支的 SPP UUID）。纯 BLE 设备需额外 BLE 传输层。
internal sealed class EdifierManager : IBrandManager
{
    private readonly BusinessState _state = new();
    private ConnectionLink? _link;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private string? _deviceName;

    public EdifierManager()
    {
        _state.Changed += OnStateChanged;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;

    public BusinessSnapshot Snapshot => _state.Snapshot();

    // 漫步者不使用 OPPO 型号能力表；界面依据 Presentation 而非此字段决定可见性。
    public DeviceCapability Capability => DeviceCapability.Unknown;

    public IReadOnlyList<string> ModelNames => [];

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree
        => new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>>();

    public ModelCatalogLocation? FindModelLocation(string? modelName) => null;

    public BrandPresentation Presentation => BuildPresentation();

    public bool CanManageMultiDevice => false;

    public void SetInteractivePolling(bool enabled)
    {
        // 电量/降噪轮询始终运行，不依赖交互状态。
    }

    public Task DisconnectAsync()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
        if (_link is not null)
        {
            var link = _link;
            _link = null;
            _ = link.DisposeAsync();
        }

        _state.Reset();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _state.Changed -= OnStateChanged;
        return new ValueTask(DisconnectAsync());
    }

    public void SetManualModel(string? modelName)
    {
        // 漫步者无型号覆盖需求。
    }

    // ---- OPPO 专属功能：统一返回不支持 ----
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

    // ---- 触控手势：漫步者协议不支持手势配置，统一返回空/不支持 ----
    public IReadOnlyList<GestureEntry> GestureEntries => [];
    public Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken) => Task.FromResult(false);

    // 漫步者协议不支持均衡器，统一返回空档案；UI 通过 Presentation.SupportsCustomEqualizer 判定不可见。
    public IEqualizerProfile EqualizerProfile => NullEqualizerProfile.Instance;

    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public bool IsValidCustomEqualizerName(string name) => false;

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, -6, 6, [], []);

    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry) => [];

    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => new([], []);

    // ---- 降噪（实装）----
    public Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken)
    {
        var edifierMode = MapToEdifierMode(mode);
        if (edifierMode is null || _link is null)
            return Task.FromResult(false);

        return SetNoiseModeCoreAsync(edifierMode.Value, cancellationToken);
    }

    public Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken)
    {
        var mode = modeKey switch
        {
            "off" => NoiseMode.Off,
            "anc" => NoiseMode.NoiseCancellation,
            "transparency" => NoiseMode.Transparency,
            _ => NoiseMode.Unknown
        };
        if (mode == NoiseMode.Unknown)
            return Task.FromResult(false);

        return SetNoiseCancellationAsync(mode, cancellationToken);
    }

    public Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken)
    {
        var mode = protocolIndex switch
        {
            1 => NoiseMode.Off,
            2 => NoiseMode.NoiseCancellation,
            3 => NoiseMode.Transparency,
            _ => NoiseMode.Unknown
        };
        if (mode == NoiseMode.Unknown)
            return Task.FromResult(false);

        return SetNoiseCancellationAsync(mode, cancellationToken);
    }

    // ---- 会话建立 ----
    // 漫步者 SPP 通道无需握手，直接读取电量与降噪并启动轮询。
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _deviceName = deviceName;
        _link = link;

        await RefreshBatteryAsync(link, cancellationToken);
        await RefreshNoiseAsync(link, cancellationToken);

        _state.SetConnected(deviceName);

        _pollCancellation = new CancellationTokenSource();
        _pollTask = RunPollingAsync(link, _pollCancellation.Token);
    }

    // ---- 内部读取/轮询 ----
    private async Task RefreshBatteryAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                EdifierConstants.QueryBattery, EdifierConstants.ReportBattery, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                ApplyBattery(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Edifier", $"电量查询失败：{exception.Message}");
        }
    }

    private async Task RefreshNoiseAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                EdifierConstants.QueryNoiseMode, EdifierConstants.ReportNoiseMode, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                ApplyNoise(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Edifier", $"降噪查询失败：{exception.Message}");
        }
    }

    private async Task<bool> SetNoiseModeCoreAsync(byte edifierMode, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        var payload = new byte[] { edifierMode };

        try
        {
            await _link.RequestAsync(EdifierConstants.SetNoiseMode, EdifierConstants.AckNoiseMode, payload, cancellationToken);
            await RefreshNoiseAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Edifier", $"设置降噪失败：{exception.Message}", exception);
            return false;
        }
    }

    private async Task RunPollingAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_link is null)
                break;

            await RefreshBatteryAsync(link, cancellationToken);
            await RefreshNoiseAsync(link, cancellationToken);
        }
    }

    // ---- 漫步者解析 ----
    private void ApplyBattery(ReadOnlySpan<byte> payload)
    {
        // 漫步者电量查询（0xD0）响应为单字节百分比（头戴/双耳共用一个值）。
        if (payload.Length < 1)
            return;

        var percent = payload[0];
        if (percent > 100)
            return;

        var level = new BatteryLevel(percent, false);
        // 单电池设备：左/右/充电盒统一显示该值，确保界面有可见电量。
        _state.SetBattery(level, level, level);
    }

    private void ApplyNoise(ReadOnlySpan<byte> payload)
    {
        // 降噪查询（0xCC）响应 payload[0]=模式(1=关,2=降噪,3=通透)，payload[1]=环境音量(可选)。
        if (payload.Length < 1)
            return;

        var mode = MapFromEdifierMode(payload[0]);
        _state.SetNoise(new NoiseSnapshot(mode, null));
    }

    private static byte? MapToEdifierMode(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => EdifierConstants.NoiseOff,
        NoiseMode.NoiseCancellation => EdifierConstants.NoiseAnc,
        NoiseMode.Transparency => EdifierConstants.NoiseTransparency,
        _ => null
    };

    private static NoiseMode MapFromEdifierMode(byte value) => value switch
    {
        EdifierConstants.NoiseOff => NoiseMode.Off,
        EdifierConstants.NoiseAnc => NoiseMode.NoiseCancellation,
        EdifierConstants.NoiseTransparency => NoiseMode.Transparency,
        _ => NoiseMode.Unknown
    };

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
        => StateChanged?.Invoke(this, snapshot);

    private BrandPresentation BuildPresentation()
    {
        IReadOnlyList<NoiseOptionModel> noiseOptions =
        [
            new("off", NoiseMode.Off, EdifierConstants.NoiseOff, []),
            new("anc", NoiseMode.NoiseCancellation, EdifierConstants.NoiseAnc, []),
            new("transparency", NoiseMode.Transparency, EdifierConstants.NoiseTransparency, []),
        ];

        return new BrandPresentation(
            _deviceName ?? "Edifier TWS / Headphone",
            false,
            false,
            false,
            true,
            false,
            [],
            BrandPresentation.DefaultCustomEqMinimumGain,
            BrandPresentation.DefaultCustomEqMaximumGain,
            [],
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal),
            noiseOptions,
            "off");
    }
}
