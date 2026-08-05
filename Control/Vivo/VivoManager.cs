using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control.Vivo;

// vivo / iQOO TWS 会话管理（HyperEars GAIA 协议移植，GPL-3.0-only）。
//
// vivo 仅支持电量与降噪两类功能，因此本管理器对 IBrandManager 中 OPPO 专属的
// 功能（均衡器、空间音频、游戏模式、双设备、查找设备等）一律返回“不支持”，
// 仅电量与降噪提供端到端读写。UI 通过 Presentation.SupportsNoiseCancellation
// 与 NoiseOptions 展示降噪卡片，不会因缺失 OPPO 能力而报错。
internal sealed class VivoManager : IBrandManager
{
    private readonly BusinessState _state = new();
    private ConnectionLink? _link;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private string? _deviceName;
    private VivoProfile _profile = VivoProfile.FamilyDefaultV4;

    public VivoManager()
    {
        _state.Changed += OnStateChanged;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;

    public BusinessSnapshot Snapshot => _state.Snapshot();

    // vivo 不使用 OPPO 型号能力表；界面依据 Presentation 而非此字段决定可见性。
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
        // vivo 无型号覆盖需求。
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
        var vivoMode = MapToVivoMode(mode);
        if (vivoMode is null || _link is null)
            return Task.FromResult(false);

        return SetNoiseModeCoreAsync(vivoMode.Value, cancellationToken);
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
            0 => NoiseMode.NoiseCancellation,
            1 => NoiseMode.Off,
            2 => NoiseMode.Transparency,
            _ => NoiseMode.Unknown
        };
        if (mode == NoiseMode.Unknown)
            return Task.FromResult(false);

        return SetNoiseCancellationAsync(mode, cancellationToken);
    }

    // ---- 会话建立 ----
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _deviceName = deviceName;
        _profile = VivoModels.SelectProfile(deviceName);
        ApplicationLog.Current?.Debug("Vivo", $"选择协议画像：device={deviceName}，gaiaVersion={_profile.GaiaVersion}，queryPayload={_profile.NoiseQueryPayload.Length} 字节，setSuffix={string.Join(",", _profile.NoiseSetSuffix)}。");
        _link = link;

        // 握手可选，失败不阻断后续流程。
        try
        {
            await link.RequestAsync(VivoConstants.Handshake, VivoConstants.HandshakeResponse, Array.Empty<byte>(), cancellationToken);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"握手未响应（可忽略）：{exception.Message}");
        }

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
                VivoConstants.QueryBattery, VivoConstants.ReportBattery, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                ApplyBattery(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"电量查询失败：{exception.Message}");
        }
    }

    private async Task RefreshNoiseAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryNoiseMode, VivoConstants.AckNoiseMode, _profile.NoiseQueryPayload, cancellationToken);
            if (response is not null)
                ApplyNoise(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"降噪查询失败：{exception.Message}");
        }
    }

    private async Task<bool> SetNoiseModeCoreAsync(byte vivoMode, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        var payload = new byte[1 + _profile.NoiseSetSuffix.Length];
        payload[0] = vivoMode;
        _profile.NoiseSetSuffix.CopyTo(payload, 1);

        try
        {
            await _link.RequestAsync(VivoConstants.SetNoiseMode, VivoConstants.AckNoiseMode, payload, cancellationToken);
            await RefreshNoiseAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置降噪失败：{exception.Message}", exception);
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

    // ---- GAIA 解析 ----
    private void ApplyBattery(ReadOnlySpan<byte> payload)
    {
        // payload: [0]=0, [1]=left%, [2]=right%, [3]=case%, [4]=charging bits
        if (payload.Length < 5 || payload[0] != 0)
            return;

        var charging = payload[4];
        var left = payload[1] <= 100 ? (byte?)payload[1] : null;
        var right = payload[2] <= 100 ? (byte?)payload[2] : null;
        var caseP = payload[3] <= 100 ? (byte?)payload[3] : null;

        _state.SetBattery(
            left.HasValue ? new BatteryLevel(left.Value, (charging & 1) != 0) : null,
            right.HasValue ? new BatteryLevel(right.Value, (charging & 2) != 0) : null,
            caseP.HasValue ? new BatteryLevel(caseP.Value, (charging & 4) != 0) : null);
    }

    private void ApplyNoise(ReadOnlySpan<byte> payload)
    {
        // payload: [0]=0, [1]=mode (0=ANC,1=OFF,2=TRANSPARENCY)
        if (payload.Length < 2 || payload[0] != 0)
            return;

        var mode = MapFromVivoMode(payload[1]);
        _state.SetNoise(new NoiseSnapshot(mode, null));
    }

    private static byte? MapToVivoMode(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => VivoConstants.NoiseOff,
        NoiseMode.NoiseCancellation => VivoConstants.NoiseAnc,
        NoiseMode.Transparency => VivoConstants.NoiseTransparency,
        _ => null
    };

    private static NoiseMode MapFromVivoMode(byte value) => value switch
    {
        VivoConstants.NoiseAnc => NoiseMode.NoiseCancellation,
        VivoConstants.NoiseOff => NoiseMode.Off,
        VivoConstants.NoiseTransparency => NoiseMode.Transparency,
        _ => NoiseMode.Unknown
    };

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
        => StateChanged?.Invoke(this, snapshot);

    private BrandPresentation BuildPresentation()
    {
        IReadOnlyList<NoiseOptionModel> noiseOptions =
        [
            new("off", NoiseMode.Off, VivoConstants.NoiseOff, []),
            new("anc", NoiseMode.NoiseCancellation, VivoConstants.NoiseAnc, []),
            new("transparency", NoiseMode.Transparency, VivoConstants.NoiseTransparency, []),
        ];

        return new BrandPresentation(
            _deviceName ?? "vivo / iQOO TWS",
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
