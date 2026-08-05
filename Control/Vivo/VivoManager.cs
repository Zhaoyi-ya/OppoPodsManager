using System.Threading;
using System.Threading.Tasks;
using System.Text;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Control.Vivo.Models;

namespace OppoPodsManager.Control.Vivo;

// vivo / iQOO TWS 会话管理。
//
// vivo 当前已验证电量与降噪的 GAIA 命令；其它白名单能力须完成协议验证后才会向界面公开。
internal sealed class VivoManager : IBrandManager
{
    private readonly BusinessState _state = new();
    private readonly VivoModelCatalog _modelCatalog;
    private ConnectionLink? _link;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private readonly List<IDisposable> _notificationSubscriptions = [];
    private volatile bool _interactivePolling;
    private string? _deviceName;
    private string? _manualModel;
    private byte? _spatialScene;
    private bool _audioEffectVerified;
    private VivoProfile _profile = VivoProfile.FamilyDefaultV4;
    private VivoDeviceCapability _vivoCapability = new(null);

    public VivoManager(VivoModelCatalog? modelCatalog = null)
    {
        _modelCatalog = modelCatalog ?? new VivoModelCatalog([]);
        _state.Changed += OnStateChanged;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;

    public BusinessSnapshot Snapshot => _state.Snapshot();

    public DeviceCapability Capability => _vivoCapability.ToDeviceCapability();

    public IReadOnlyList<string> ModelNames => _modelCatalog.ModelNames;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree
        => _modelCatalog.ModelTree;

    public ModelCatalogLocation? FindModelLocation(string? modelName) => _modelCatalog.FindLocation(modelName);

    public BrandPresentation Presentation => BuildPresentation();

    public bool CanManageMultiDevice => false;

    public void SetInteractivePolling(bool enabled)
    {
        // 交互界面打开时缩短电量保底读取间隔，功能状态优先由设备通知更新。
        _interactivePolling = enabled;
        ApplicationLog.Current?.Debug("Vivo", $"交互轮询状态已更新：enabled={enabled}。");
    }

    public Task DisconnectAsync()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
        _spatialScene = null;
        _audioEffectVerified = false;
        DisposeNotificationSubscriptions();
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
        _manualModel = string.IsNullOrWhiteSpace(modelName) ? null : modelName;
        ResolveCapability();
        _audioEffectVerified = false;
        if (_state.Snapshot().IsConnected)
        {
            _state.SetConnected(_deviceName ?? string.Empty);
            if (_link is not null && _vivoCapability.SupportsAudioEffect)
                _ = RefreshAudioEffectAsync(_link, CancellationToken.None);
        }
    }

    // ---- 尚未完成 vivo 协议验证的功能：统一返回不支持 ----
    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    // 使用 vivo 官方低延迟游戏模式命令更新耳机状态。
    public async Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null || !_vivoCapability.SupportsLowLatencyGaming)
            return false;

        try
        {
            var response = await _link.RequestAsync(
                VivoConstants.SetLowLatencyGaming,
                VivoConstants.AckLowLatencyGaming,
                new byte[] { enabled ? (byte)1 : (byte)0 },
                cancellationToken);
            return response is not null && ApplyGameMode(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置低延迟游戏模式失败：{exception.Message}", exception);
            return false;
        }
    }
    // 写入 vivo 官方音效预设，仅接受当前型号版本表允许的协议 ID。
    public async Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken)
    {
        if (_link is null || !_vivoCapability.SupportsAudioEffect || !_audioEffectVerified
            || !_vivoCapability.AudioEffectPresetKeys.Contains(VivoAudioEffectCatalog.GetPresetKey(presetId)))
        {
            return false;
        }

        try
        {
            var response = await _link.RequestAsync(
                VivoConstants.SetAudioEffect,
                VivoConstants.AckAudioEffect,
                new byte[] { presetId },
                cancellationToken);
            return response is not null && ApplyAudioEffect(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置内置音效失败：id={presetId}，{exception.Message}", exception);
            return false;
        }
    }

    // 将界面使用的稳定预设键转换为 vivo 协议 ID。
    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken)
        => VivoAudioEffectCatalog.TryGetPresetId(presetName, out var presetId)
            ? SetEqualizerAsync(presetId, cancellationToken)
            : Task.FromResult(false);
    // 使用官方空间音频模式枚举写入耳机，并保留读取到的场景索引。
    public async Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken)
    {
        if (_link is null || !_vivoCapability.SupportsSpatialAudio)
            return false;

        var vivoMode = mode switch
        {
            SpatialAudioMode.Off => (byte)0,
            SpatialAudioMode.Fixed => (byte)1,
            SpatialAudioMode.HeadTracking => (byte)2,
            _ => (byte?)null
        };
        if (!vivoMode.HasValue)
            return false;

        var payload = _spatialScene.HasValue
            ? new byte[] { vivoMode.Value, _spatialScene.Value }
            : new byte[] { vivoMode.Value };
        try
        {
            var response = await _link.RequestAsync(
                VivoConstants.SetSpatialAudio,
                VivoConstants.AckSpatialAudio,
                payload,
                cancellationToken);
            return response is not null && ApplySpatialAudio(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置空间音频失败：{exception.Message}", exception);
            return false;
        }
    }

    // 将界面稳定键转换为统一业务层的空间音频模式。
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken)
        => SetSpatialAudioAsync(SpatialAudio.ParseMode(modeKey), cancellationToken);
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
        if (vivoMode is null || _link is null || !_vivoCapability.SupportsNoiseCancellation)
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
        ResolveCapability();
        _profile = VivoModels.SelectProfile(deviceName);
        ApplicationLog.Current?.Debug("Vivo", $"选择协议画像：device={deviceName}，model={_vivoCapability.ModelName}，known={_vivoCapability.IsKnownModel}，gaiaVersion={_profile.GaiaVersion}，queryPayload={_profile.NoiseQueryPayload.Length} 字节，setSuffix={string.Join(",", _profile.NoiseSetSuffix)}。");
        _link = link;
        InstallNotificationHandlers(link);

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
        if (_vivoCapability.SupportsNoiseCancellation)
            await RefreshNoiseAsync(link, cancellationToken);
        if (_vivoCapability.SupportsLowLatencyGaming)
            await RefreshGameModeAsync(link, cancellationToken);
        if (_vivoCapability.SupportsSpatialAudio)
            await RefreshSpatialAudioAsync(link, cancellationToken);
        if (_vivoCapability.SupportsAudioEffect)
            await RefreshAudioEffectAsync(link, cancellationToken);

        _state.SetConnected(deviceName);

        _pollCancellation = new CancellationTokenSource();
        _pollTask = RunPollingAsync(link, _pollCancellation.Token);
    }

    // ---- 内部读取/轻量轮询 ----
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

    // 读取一次低延迟游戏模式；官方通知接入前不参与周期轮询。
    private async Task RefreshGameModeAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryLowLatencyGaming,
                VivoConstants.ReportLowLatencyGaming,
                Array.Empty<byte>(),
                cancellationToken);
            if (response is not null)
                ApplyGameMode(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"低延迟游戏模式查询失败：{exception.Message}");
        }
    }

    // 读取空间音频当前模式及设备可选的场景索引。
    private async Task RefreshSpatialAudioAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QuerySpatialAudio,
                VivoConstants.ReportSpatialAudio,
                Array.Empty<byte>(),
                cancellationToken);
            if (response is not null)
                ApplySpatialAudio(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"空间音频查询失败：{exception.Message}");
        }
    }

    // 读取当前内置音效；自定义 DeepX 回包会保留为未选择状态而不伪装成预设。
    private async Task RefreshAudioEffectAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryAudioEffect,
                VivoConstants.ReportAudioEffect,
                Array.Empty<byte>(),
                cancellationToken);
            if (response is not null)
                ApplyAudioEffect(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"内置音效查询失败：{exception.Message}");
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
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = _interactivePolling ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(1);
            await Task.Delay(interval, cancellationToken);
            if (_link != link)
                break;

            await RefreshBatteryAsync(link, cancellationToken);
        }
    }

    // 注册持久协议订阅，使设备主动状态帧无需等待轮询即可更新业务快照。
    private void InstallNotificationHandlers(ConnectionLink link)
    {
        DisposeNotificationSubscriptions();
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportBattery,
            frame => ApplyBattery(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.AckNoiseMode,
            frame => ApplyNoise(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportNoiseMode,
            frame => ApplyNoise(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.AckLowLatencyGaming,
            frame => ApplyGameMode(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportLowLatencyGaming,
            frame => ApplyGameMode(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.AckSpatialAudio,
            frame => ApplySpatialAudio(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportSpatialAudio,
            frame => ApplySpatialAudio(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.AckAudioEffect,
            frame => ApplyAudioEffect(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportAudioEffect,
            frame => ApplyAudioEffect(frame.Payload.Span)));
    }

    // 连接释放前解除所有常驻协议订阅，避免旧连接继续修改当前设备状态。
    private void DisposeNotificationSubscriptions()
    {
        foreach (var subscription in _notificationSubscriptions)
            subscription.Dispose();
        _notificationSubscriptions.Clear();
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
        ApplicationLog.Current?.Debug(
            "Vivo",
            $"电量状态已更新：left={left?.ToString() ?? "-"}，right={right?.ToString() ?? "-"}，case={caseP?.ToString() ?? "-"}，charging=0x{charging:X2}。");
    }

    private void ApplyNoise(ReadOnlySpan<byte> payload)
    {
        // payload: [0]=0, [1]=mode (0=ANC,1=OFF,2=TRANSPARENCY)
        if (payload.Length < 2 || payload[0] != 0)
            return;

        var mode = MapFromVivoMode(payload[1]);
        _state.SetNoise(new NoiseSnapshot(mode, null));
        ApplicationLog.Current?.Debug("Vivo", $"降噪状态已更新：protocol={payload[1]}，mode={mode}。");
    }

    // 官方回包第二个字节为低延迟游戏模式开关。
    private bool ApplyGameMode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[1] > 1)
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略无效低延迟游戏模式回包：{Convert.ToHexString(payload)}。");
            return false;
        }

        _state.SetGame(new GameSnapshot(payload[1] == 1, null));
        ApplicationLog.Current?.Debug("Vivo", $"低延迟游戏模式已更新：enabled={payload[1] == 1}。");
        return true;
    }

    // 官方回包第二个字节为模式，第三个字节存在时为场景索引。
    private bool ApplySpatialAudio(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略无效空间音频回包：{Convert.ToHexString(payload)}。");
            return false;
        }

        var mode = payload[1] switch
        {
            0 => SpatialAudioMode.Off,
            1 => SpatialAudioMode.Fixed,
            2 => SpatialAudioMode.HeadTracking,
            _ => SpatialAudioMode.Unknown
        };
        if (mode == SpatialAudioMode.Unknown)
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略未知空间音频模式：{Convert.ToHexString(payload)}。");
            return false;
        }

        _spatialScene = payload.Length >= 3 ? payload[2] : null;
        _state.SetSpatialAudio(new SpatialAudioSnapshot(mode));
        ApplicationLog.Current?.Debug(
            "Vivo",
            $"空间音频状态已更新：mode={mode}，scene={_spatialScene?.ToString() ?? "-"}。");
        return true;
    }

    // 官方回包第二个字节为音效 ID；游戏场景和 DeepX 载荷需要按官方服务规则单独处理。
    private bool ApplyAudioEffect(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略无效内置音效回包：{Convert.ToHexString(payload)}。");
            return false;
        }

        var reportedPresetId = payload[1];
        if (reportedPresetId == VivoAudioEffectCatalog.DeepXCustomEffect && payload.Length >= 20)
        {
            var customNameLength = payload[18];
            if (customNameLength <= payload.Length - 19)
            {
                var customName = Encoding.UTF8.GetString(payload.Slice(19, customNameLength));
                _audioEffectVerified = true;
                _state.SetEqualizer(new EqualizerSnapshot(reportedPresetId, null));
                ApplicationLog.Current?.Info(
                    "Vivo",
                    $"设备返回 DeepX 自定义音效：name={customName}。当前桌面端仅保留其状态，不将其伪装为内置预设。");
                return true;
            }

            ApplicationLog.Current?.Debug("Vivo", $"忽略名称长度无效的 DeepX 音效回包：{Convert.ToHexString(payload)}。");
            return false;
        }

        if (!VivoAudioEffectCatalog.TryNormalizeReportedPreset(reportedPresetId, out var presetId))
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略未知内置音效回包：{Convert.ToHexString(payload)}。");
            return false;
        }

        var presetKey = VivoAudioEffectCatalog.GetPresetKey(presetId);
        var knownPreset = _vivoCapability.AudioEffectPresetKeys.Contains(presetKey);
        if (knownPreset)
            _audioEffectVerified = true;
        _state.SetEqualizer(new EqualizerSnapshot(presetId, knownPreset ? presetKey : null));
        ApplicationLog.Current?.Debug(
            "Vivo",
            knownPreset
                ? $"内置音效状态已更新：reportedId={reportedPresetId}，id={presetId}，preset={presetKey}。"
                : $"设备返回当前型号不支持的音效：reportedId={reportedPresetId}，id={presetId}，不在内置列表显示。");
        return true;
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
        IReadOnlyList<NoiseOptionModel> noiseOptions = _vivoCapability.SupportsNoiseCancellation
            ?
            [
                new("off", NoiseMode.Off, VivoConstants.NoiseOff, []),
                new("anc", NoiseMode.NoiseCancellation, VivoConstants.NoiseAnc, []),
                new("transparency", NoiseMode.Transparency, VivoConstants.NoiseTransparency, []),
            ]
            : [];

        var visibleControls = new HashSet<string>(StringComparer.Ordinal);
        var controlStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        var controlEnabledStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (_vivoCapability.SupportsLowLatencyGaming)
        {
            visibleControls.Add("game-mode");
            controlEnabledStates["game-mode"] = true;
            var gameEnabled = _state.Snapshot().Game.IsEnabled;
            if (gameEnabled.HasValue)
                controlStates["game-mode"] = gameEnabled.Value;
        }
        if (_vivoCapability.SupportsAudioEffect && _audioEffectVerified)
        {
            visibleControls.Add("equalizer");
            controlEnabledStates["equalizer"] = true;
        }

        return new BrandPresentation(
            _vivoCapability.IsKnownModel ? _vivoCapability.ModelName : _deviceName ?? "vivo / iQOO TWS",
            _vivoCapability.IsKnownModel,
            _vivoCapability.SupportsSpatialAudio,
            false,
            _vivoCapability.SupportsNoiseCancellation,
            false,
            [],
            BrandPresentation.DefaultCustomEqMinimumGain,
            BrandPresentation.DefaultCustomEqMaximumGain,
            _audioEffectVerified ? _vivoCapability.AudioEffectPresetKeys : [],
            visibleControls,
            controlStates,
            controlEnabledStates,
            noiseOptions,
            "off");
    }

    // 优先使用用户明确指定的官方型号，否则按当前蓝牙名称解析白名单条目。
    private void ResolveCapability()
    {
        var identificationName = _manualModel ?? _deviceName;
        var match = _modelCatalog.Match(identificationName);
        _vivoCapability = new VivoDeviceCapability(match?.Model);

        if (match is null)
        {
            ApplicationLog.Current?.Debug("Vivo", $"型号未命中官方目录：device={identificationName ?? ""}。");
            return;
        }

        ApplicationLog.Current?.Info(
            "Vivo",
            $"型号识别完成：device={identificationName}，model={match.Model.DisplayName}，project={match.Model.ProjectName}，source={match.Kind}，matched={match.MatchedValue}。");
    }
}
