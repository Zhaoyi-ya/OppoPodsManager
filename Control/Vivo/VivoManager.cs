using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control.Vivo;

// vivo / iQOO TWS 会话管理（HyperEars GAIA 协议移植，GPL-3.0-only）。
//
// 已实现并暴露 UI：电量、降噪（3 档：关闭/降噪/通透，key 对齐 OPPO 的 "Off"/"NC"/"Transparency"）。
// 查找耳机/游戏模式(低延迟)/空间音效开关的命令实现已就绪（见下方 *CoreAsync）。
// 空间音频(0x0218)与游戏模式(0x0220)命令字已反编译/抓包验证；游戏模式仍为占位，待真机确认。
// 佩戴检测(0x0103/0x820D)与音效场景(0x0118/0x8118)经反编译实证并已接入；连接建立后发送
// 0x0205 注册通知握手，使电量/降噪/佩戴/空间/音效场景改为耳机主动上报（与 OPPO 同源）。
// 控件可见性由 VivoFeatureMatrix（vivo 能力白名单，对应 EarbudFeatures.FeatureID）按连接型号决定，
// 与 OPPO 侧 CapabilityLoader 同一思路：已知型号仅显示其能力集合内的功能，未知型号默认乐观显示，
// 便于真机逐项验证命令实现，确认不支持的功能由白名单精确隐藏。
// 其余 OPPO 专属能力（均衡器、3 模式空间音频等）仍返回“不支持”。
internal sealed class VivoManager : IBrandManager
{
    private readonly BusinessState _state = new();
    private ConnectionLink? _link;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private string? _deviceName;
    private VivoProfile _profile = VivoProfile.FamilyDefaultV4;
    private bool? _gameModeEnabled;
    private bool? _spatialSoundEnabled;
    // 运行期探测到的“不支持”功能（查询超时），本会话内停止轮询并隐藏对应控件。
    private readonly HashSet<int> _runtimeUnsupported = new();
    // 多连接（双设备）：订阅耳机主动上报 / 时间请求，并缓存最近一次设备列表（MAC+state，7 字节外形式）供全量下发。
    private readonly List<IDisposable> _subscriptions = new();
    private readonly List<(byte[] Address, byte State)> _multiConnectCache = new();
    private readonly object _multiConnectGate = new();
    // 各资源查询的序号守卫：只接受“最新发出”的查询响应，丢弃过期的旧查询响应，
    // 避免轮询中已在途的旧查询在设值之后回灌，造成 UI 闪回切换前状态（OPPO 用设备推送无此竞态）。
    private int _noiseSeq, _noiseHighest;
    private int _gameSeq, _gameHighest;
    private int _spatialSeq, _spatialHighest;
    // 设值后设备约 1~2s 才真正落定：此窗口内丢弃降噪回读，避免轮询读到切换前的旧模式覆盖乐观值（UI 回闪）。
    private DateTime _noiseApplyDeadline = DateTime.MinValue;
    // 0x0300 握手是否成功：仅成功时才说明当前通道是活的 GAIA 通道，可安全启用主动上报。
    private bool _handshakeOk;

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

    public bool CanManageMultiDevice => IsFeatureLive(VivoFeatureMatrix.DualConnection);

    public void SetInteractivePolling(bool enabled)
    {
        // 电量/降噪轮询始终运行，不依赖交互状态。
    }

    public async Task DisconnectAsync()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
        // 解除对耳机主动帧（0x8509 时间请求 / 0x8249 双连上报）的订阅，避免泄漏到下一次会话。
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        lock (_multiConnectGate)
            _multiConnectCache.Clear();
        if (_link is not null)
        {
            var link = _link;
            _link = null;
            // 必须 await：否则底层 RFCOMM socket 在后台关闭，探测会话会被泄漏，
            // 导致后续真正连接时该通道仍被占用、port 0 被拒并退化到不认 GAIA 的裸通道。
            await link.DisposeAsync();
        }

        _state.Reset();
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
    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken)
        => _link is null ? Task.FromResult(false) : SetWearDetectionCoreAsync(enabled, cancellationToken);
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken)
        => SetDualDeviceCoreAsync(enabled, cancellationToken);
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => SetSpatialSoundCoreAsync(enabled, cancellationToken);
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken) => SetGameModeCoreAsync(enabled, cancellationToken);
    public Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken) => SetFindDeviceCoreAsync(enabled, cancellationToken);
    public Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken)
        => _link is null ? Task.FromResult(false) : RefreshMultiDeviceCoreAsync(_link, cancellationToken);
    public Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken)
        => _link is null ? Task.FromResult(false) : RefreshMultiDeviceCoreAsync(_link, cancellationToken);
    public Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken)
        => _link is null ? Task.FromResult(false) : OperateMultiDeviceCoreAsync(automatic ? MultiDeviceOperation.AutomaticPriority : MultiDeviceOperation.SetPriority, address, cancellationToken);
    public Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken)
        => _link is null ? Task.FromResult(false) : OperateMultiDeviceCoreAsync(operation, address, cancellationToken);

    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public bool IsValidCustomEqualizerName(string name) => false;

    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, -6, 6, [], []);

    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry) => [];

    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => MultiDevicePolicy.BuildDisplayState(_state.Snapshot().MultiDevice, hiddenAddresses);

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
            "Off" => NoiseMode.Off,
            "NC" => NoiseMode.NoiseCancellation,
            "Transparency" => NoiseMode.Transparency,
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
        // 订阅耳机主动下发的帧：
        //   * 时间请求 0x8509（应答 0x0509）、双连列表上报 0x8249（也由轮询 0x0249 触发）。
        //   * 状态 report 帧（电量/降噪/佩戴/空间/音效场景/媒体播放）：注册通知后耳机会主动推送，
        //     即使未注册，0x8249/0x8509 等仍自发；订阅让这些状态变为实时上报而非仅轮询。
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.PeerTimeRequest, OnPeerTimeRequest));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportMultiConnect, OnMultiConnectReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportBattery, OnBatteryReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportNoiseMode, OnNoiseReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportWearStatus, OnWearReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportSpatialSound, OnSpatialReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportSoundEffectScene, OnSoundSceneReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportAudioPlayState, OnPlayStateReport));
        // 每次开新会话重置运行期探测到的“不支持”功能（避免首次连接因通道协商失败而误判永久隐藏）。
        _runtimeUnsupported.Clear();

        // 握手用于确认当前 RFCOMM 通道是活的 GAIA 通道。历史 bug：自动连接阶段若退化到裸通道
        // Channel-1/15，能建链但不回任何 GAIA 帧，握手会超时；原逻辑“可忽略”后继续，导致耳机看似已
        // 连接却不推送任何状态。现改为：握手失败即视为通道不可用，抛 ChannelUnusableException 让连接层
        // 放弃该通道、强制只用服务 UUID 端口 0 重试（多半能自愈），而不是在死通道上半死不活。
        try
        {
            await link.RequestAsync(VivoConstants.Handshake, VivoConstants.HandshakeResponse, Array.Empty<byte>(), cancellationToken);
            _handshakeOk = true;
        }
        catch (Exception exception)
        {
            _handshakeOk = false;
            ApplicationLog.Current?.Info("Vivo", $"握手未响应，通道不可用：{exception.Message}");
            throw new ChannelUnusableException($"vivo 0x0300 握手未响应，当前 RFCOMM 通道不可用：{exception.Message}", exception);
        }

        // 注册通知握手：让耳机开始主动推送各状态 report 帧（与 OPPO 同源，空载荷一发即开推）。
        // 即便机型不支持，也是单向发送、不阻塞后续流程；推送由上方订阅捕获。
        await RegisterNotificationsAsync(link, cancellationToken);

        await RefreshBatteryAsync(link, cancellationToken);
        await RefreshNoiseAsync(link, cancellationToken);
        // 先上报“已连接”，让主窗口尽快显示电量与降噪等已验证状态。
        _state.SetConnected(deviceName);

        _pollCancellation = new CancellationTokenSource();
        _pollTask = RunPollingAsync(link, _pollCancellation.Token);

        // 游戏模式/空间音效命令字尚未真机验证（见 VivoConstants 占位说明），且未知型号按白名单
        // 乐观显示；若在此同步等待其初始查询，最坏情况下两个命令各超时约 4s，会使“已连接”状态
        // 延迟近 8s 才上报。改为连接建立后在后台补偿查询：命中则回填开关状态并通知 UI，
        // 超时则由运行期探测标记“不支持”并隐藏对应控件（轮询中也不再重复查询）。
        if (IsFeatureLive(VivoFeatureMatrix.LowLatencyGaming))
            _ = RefreshGameModeAsync(link, cancellationToken);
        if (IsFeatureLive(VivoFeatureMatrix.SpatialAudio))
            _ = RefreshSpatialSoundAsync(link, cancellationToken);
        // 双连列表：同样在后台补偿拉取，避免阻塞“已连接”上报；不支持的机型由运行期超时探测隐藏面板。
        if (IsFeatureLive(VivoFeatureMatrix.DualConnection))
            _ = RefreshMultiDeviceCoreAsync(link, cancellationToken);
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
        var seq = ++_noiseSeq;
        _noiseHighest = Math.Max(_noiseHighest, seq);
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryNoiseMode, VivoConstants.ReportNoiseMode, _profile.NoiseQueryPayload, cancellationToken);
            // 仅接受最新发出的查询响应；过期的在途旧查询（设值前发出）直接丢弃，防止 UI 闪回旧状态。
            if (seq < _noiseHighest)
            {
                ApplicationLog.Current?.Debug("Vivo", $"降噪查询响应已过期（seq={seq} < 最新={_noiseHighest}），跳过以避免 UI 回闪。");
                return;
            }

            // 设值沉降窗口：设备尚未真正切到新模式，此刻回读到的是切换前状态，丢弃以免覆盖乐观值（UI 回闪）。
            if (DateTime.UtcNow < _noiseApplyDeadline)
            {
                ApplicationLog.Current?.Debug("Vivo", $"降噪查询落在设值沉降窗口内（seq={seq}），丢弃旧模式回读以避免 UI 回闪。");
                return;
            }

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
            // 乐观更新：立即把 UI 切到目标模式，避免“设值后回读旧值→UI 闪回切换前状态”。
            // 设备约 1~2s 才真正落定，立即回读必然拿到切换前的值。抬高查询序号纪元，
            // 使轮询中可能已在途的旧查询响应在回程时被丢弃，不会用旧模式覆盖本次乐观值；
            // 同时开启沉降窗口，丢弃窗口内（设值后设备未落定）的回读，避免旧模式覆盖乐观值；
            // 窗口过后轮询自然读回并校正真实状态（若 SET 实际生效则读回新模式，UI 无变化）。
            ++_noiseSeq;
            _noiseHighest = _noiseSeq;
            _noiseApplyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
            // 我们发出的 vivoMode 必为已知值（NoiseAnc/NoiseOff/NoiseTransparency），映射结果恒有效。
            _state.SetNoise(new NoiseSnapshot(MapFromVivoMode(vivoMode), null));
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置降噪失败：{exception.Message}", exception);
            return false;
        }
    }

    // ---- T1：查找耳机（发声），游戏模式，空间音效开关 ----
    private async Task<bool> SetFindDeviceCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        try
        {
            await _link.RequestAsync(VivoConstants.SetFindDevice, VivoConstants.AckFindDevice,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            ApplicationLog.Current?.Info("Vivo", $"查找耳机命令已发送：enabled={enabled}。");
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"查找耳机命令失败：{exception.Message}", exception);
            return false;
        }
    }

    private async Task<bool> SetGameModeCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        try
        {
            await _link.RequestAsync(VivoConstants.SetGameMode, VivoConstants.AckGameMode,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            await RefreshGameModeAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置游戏模式失败：{exception.Message}", exception);
            return false;
        }
    }

    private async Task<bool> SetSpatialSoundCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        try
        {
            await _link.RequestAsync(VivoConstants.SetSpatialSound, VivoConstants.ReportSpatialSound,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            await RefreshSpatialSoundAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置空间音效失败：{exception.Message}", exception);
            return false;
        }
    }

    private async Task RefreshGameModeAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        var seq = ++_gameSeq;
        _gameHighest = Math.Max(_gameHighest, seq);
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryGameMode, VivoConstants.ReportGameMode, Array.Empty<byte>(), cancellationToken);
            if (seq < _gameHighest)
            {
                ApplicationLog.Current?.Debug("Vivo", $"游戏模式查询响应已过期（seq={seq} < 最新={_gameHighest}），跳过。");
                return;
            }

            if (response is not null && response.Payload.Length >= 1)
            {
                var p = response.Payload.Span;
                // 响应格式 00 <value>（如 00 01）；值字节在第 1 位（长度 1 时退回第 0 位）。
                _gameModeEnabled = (p.Length >= 2 ? p[1] : p[0]) != 0;
            }
        }
        catch (TimeoutException)
        {
            if (_runtimeUnsupported.Add(VivoFeatureMatrix.LowLatencyGaming))
            {
                ApplicationLog.Current?.Info("Vivo", "游戏模式查询超时，本会话停止轮询并隐藏控件。");
                _state.NotifyChanged();
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"游戏模式查询失败：{exception.Message}");
        }
    }

    private async Task RefreshSpatialSoundAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        var seq = ++_spatialSeq;
        _spatialHighest = Math.Max(_spatialHighest, seq);
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QuerySpatialSound, VivoConstants.ReportSpatialSound, Array.Empty<byte>(), cancellationToken);
            if (seq < _spatialHighest)
            {
                ApplicationLog.Current?.Debug("Vivo", $"空间音效查询响应已过期（seq={seq} < 最新={_spatialHighest}），跳过。");
                return;
            }

            if (response is not null && response.Payload.Length >= 1)
            {
                var p = response.Payload.Span;
                // 响应格式 00 <value>（如 00 01）；值字节在第 1 位（长度 1 时退回第 0 位）。
                _spatialSoundEnabled = (p.Length >= 2 ? p[1] : p[0]) != 0;
            }
        }
        catch (TimeoutException)
        {
            if (_runtimeUnsupported.Add(VivoFeatureMatrix.SpatialAudio))
            {
                ApplicationLog.Current?.Info("Vivo", "空间音效查询超时，本会话停止轮询并隐藏控件。");
                _state.NotifyChanged();
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"空间音效查询失败：{exception.Message}");
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
            if (IsFeatureLive(VivoFeatureMatrix.LowLatencyGaming))
                await RefreshGameModeAsync(link, cancellationToken);
            if (IsFeatureLive(VivoFeatureMatrix.SpatialAudio))
                await RefreshSpatialSoundAsync(link, cancellationToken);
            if (IsFeatureLive(VivoFeatureMatrix.DualConnection))
                await RefreshMultiDeviceCoreAsync(link, cancellationToken);
        }
    }

    // 综合“能力白名单(VivoFeatureMatrix.KnownSupported，已知型号仅显示其声明支持的功能) + 运行期超时探测”判断某功能当前是否应查询/展示。
    private bool IsFeatureLive(int featureId)
        => VivoFeatureMatrix.IsFeatureSupported(_deviceName, featureId)
           && !_runtimeUnsupported.Contains(featureId);

    // ---- 多连接（双设备）----
    // 命令真值来源：开源 Vivopods 项目（HyperEars 抓包 + 官方 APK jadx 反编译双重确认），见 VivoConstants 注释。
    // 双连列表上报 0x8249 由两路触发：轮询 0x0249 的响应、耳机主动推送（本订阅处理）。
    private async Task<bool> RefreshMultiDeviceCoreAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryMultiConnect, VivoConstants.ReportMultiConnect, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                OnMultiConnectReport(response);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"多设备列表查询失败：{exception.Message}");
            // 不支持双连的机型（如 TWS 3e）会超时：运行期标记本会话不支持，隐藏面板并停止轮询。
            if (exception is TimeoutException && _runtimeUnsupported.Add(VivoFeatureMatrix.DualConnection))
                _state.NotifyChanged();
            return false;
        }
    }

    private void OnMultiConnectReport(ProtocolFrame frame)
    {
        if (TryParseMultiConnect(frame.Payload.Span, out var devices))
            ApplyMultiConnect(devices);
    }

    private void ApplyMultiConnect(IReadOnlyList<ConnectedDeviceSnapshot> devices)
    {
        lock (_multiConnectGate)
        {
            _multiConnectCache.Clear();
            foreach (var device in devices)
            {
                if (TryParseAddress(device.Address, out var mac))
                    _multiConnectCache.Add((mac, (byte)device.ConnectionState));
            }
        }

        string? priority = null;
        foreach (var device in devices)
            if (device.IsCurrent)
                priority = device.Address;

        _state.SetMultiDevice(new MultiDeviceSnapshot(devices, false, priority));
    }

    // 解析 0x8249 上报：[status:1=0][count:1] 后每台 [MAC:6][未知:3][state:1][nameLen:1][UTF8名]。
    // state 字节：0=断，1=保持，2=连为当前 → 映射为 ConnectedDeviceSnapshot 的 ConnectionState 与 IsCurrent。
    private static bool TryParseMultiConnect(ReadOnlySpan<byte> payload, out IReadOnlyList<ConnectedDeviceSnapshot> devices)
    {
        devices = [];
        if (payload.Length < 2 || payload[0] != 0)
            return false;

        var count = payload[1];
        var parsed = new List<ConnectedDeviceSnapshot>(count);
        var offset = 2;
        for (var index = 0; index < count; index++)
        {
            if (payload.Length - offset < 11)
                return false;

            var address = FormatAddress(payload.Slice(offset, 6));
            offset += 6;
            offset += 3; // 未知 3 字节（metadata），展示/下发均不需要，跳过。
            var state = payload[offset++];
            var nameLength = payload[offset++];
            if (payload.Length - offset < nameLength)
                return false;

            var name = nameLength == 0
                ? string.Empty
                : Encoding.UTF8.GetString(payload.Slice(offset, nameLength)).TrimEnd('\0');
            offset += nameLength;
            parsed.Add(new ConnectedDeviceSnapshot(address, name, 0, state, state == 2, false, state == 2));
        }

        devices = parsed;
        return true;
    }

    // 全量下发 0x014A：每条仅 [MAC:6][state:1]（7 字节，不含未知/名称），将目标设备状态改为 newState。
    private byte[]? BuildMultiConnectUpdate(byte[] targetMac, byte newState)
    {
        lock (_multiConnectGate)
        {
            if (_multiConnectCache.Count == 0)
                return null;

            var payload = new byte[_multiConnectCache.Count * 7];
            var index = 0;
            foreach (var (mac, state) in _multiConnectCache)
            {
                var isTarget = mac.AsSpan().SequenceEqual(targetMac);
                mac.CopyTo(payload, index);
                payload[index + 6] = isTarget ? newState : state;
                index += 7;
            }

            return payload;
        }
    }

    private async Task<bool> OperateMultiDeviceCoreAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        try
        {
            // 操作前确保手头有最新设备列表，才能构造合法的全量下发帧。
            if (_multiConnectCache.Count == 0)
                await RefreshMultiDeviceCoreAsync(_link, cancellationToken);

            switch (operation)
            {
                case MultiDeviceOperation.Unpair:
                    if (!TryParseAddress(address, out var removeMac))
                        return false;
                    await _link.RequestAsync(VivoConstants.RemoveMultiConnect, VivoConstants.AckRemoveMultiConnect, removeMac, cancellationToken);
                    break;

                case MultiDeviceOperation.AutomaticPriority:
                    // vivo 无“自动优先级”概念，忽略以免误发未知命令。
                    ApplicationLog.Current?.Info("Vivo", "vivo 无自动优先级概念，忽略该操作。");
                    return true;

                case MultiDeviceOperation.Connect:
                case MultiDeviceOperation.Disconnect:
                case MultiDeviceOperation.SetPriority:
                {
                    if (!TryParseAddress(address, out var targetMac))
                        return false;
                    // Connect→2(连为当前)，Disconnect→0(断)，SetPriority→2(置为当前设备)。
                    var newState = operation == MultiDeviceOperation.Disconnect ? (byte)0 : (byte)2;
                    var payload = BuildMultiConnectUpdate(targetMac, newState);
                    if (payload is null)
                        return false;
                    await _link.RequestAsync(VivoConstants.SetMultiConnect, VivoConstants.AckMultiConnect, payload, cancellationToken);
                    break;
                }

                default:
                    return false;
            }

            // 操作后回读最新列表，刷新 UI。
            await RefreshMultiDeviceCoreAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"多设备操作失败：{exception.Message}", exception);
            return false;
        }
    }

    private async Task<bool> SetDualDeviceCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        try
        {
            await _link.RequestAsync(VivoConstants.EnableMultiConnect, VivoConstants.AckEnableMultiConnect,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"双连开关失败：{exception.Message}", exception);
            return false;
        }
    }

    // ---- 手机时间同步 ----
    // 耳机经 0x8509 主动索要时间，主机单向应答 0x0509（7 字节本地时间）。
    private void OnPeerTimeRequest(ProtocolFrame frame)
    {
        ApplicationLog.Current?.Debug("Vivo", $"收到耳机时间请求 0x{frame.Command:X4}，准备应答 0x{VivoConstants.HostTimeResponse:X4}。");
        var now = DateTime.Now;
        var payload = new byte[]
        {
            (byte)(now.Year / 100),
            (byte)(now.Year % 100),
            (byte)now.Month,
            (byte)now.Day,
            (byte)now.Hour,
            (byte)now.Minute,
            (byte)now.Second,
        };
        _ = RespondPeerTimeAsync(payload);
    }

    private async Task RespondPeerTimeAsync(byte[] payload)
    {
        try
        {
            if (_link is null)
                return;
            // 单向应答：复用连接层的请求串行门，避免与轮询发送在字节层面交错。
            await _link.SendFireAndForgetAsync(VivoConstants.HostTimeResponse, payload, CancellationToken.None);
            ApplicationLog.Current?.Debug("Vivo", "已应答耳机时间请求 0x0509。");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"应答时间请求失败：{exception.Message}");
        }
    }

    // ---- 佩戴检测（反编译实证 259 / 525）----
    // 0x820D 上报负载 [status:0][flags]，flags 位域：0x01=右耳佩戴、0x02=左耳佩戴、0x0C=在充电盒。
    private void OnWearReport(ProtocolFrame frame)
        => ApplyWear(frame.Payload.Span);

    private void ApplyWear(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return;

        var flags = payload.Length >= 2 ? payload[1] : payload[0];
        var inCase = (flags & 0x0C) != 0;
        var leftWorn = (flags & 0x02) != 0;
        var rightWorn = (flags & 0x01) != 0;

        var left = inCase ? EarWearState.InCase : (leftWorn ? EarWearState.Worn : EarWearState.Removed);
        var right = inCase ? EarWearState.InCase : (rightWorn ? EarWearState.Worn : EarWearState.Removed);
        ApplicationLog.Current?.Debug("Vivo", $"佩戴状态更新：左={left}，右={right}（flags=0x{flags:X2}）。");
        _state.SetWear(new WearSnapshot(left, right));
    }

    private async Task<bool> SetWearDetectionCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null)
            return false;

        try
        {
            await _link.RequestAsync(VivoConstants.SetWearDetection, VivoConstants.AckWearDetection,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            ApplicationLog.Current?.Info("Vivo", $"佩戴检测开关已发送：enabled={enabled}。");
            await RefreshWearStatusAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置佩戴检测失败：{exception.Message}", exception);
            return false;
        }
    }

    private async Task RefreshWearStatusAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryWearStatus, VivoConstants.ReportWearStatus, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                ApplyWear(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"佩戴状态查询失败：{exception.Message}");
        }
    }

    // ---- 电量/降噪/空间/音效/播放：订阅耳机主动上报 ----
    private void OnBatteryReport(ProtocolFrame frame)
        => ApplyBattery(frame.Payload.Span);

    private void OnNoiseReport(ProtocolFrame frame)
        => ApplyNoise(frame.Payload.Span);

    private void OnSpatialReport(ProtocolFrame frame)
    {
        var p = frame.Payload.Span;
        if (p.Length < 1)
            return;
        _spatialSoundEnabled = (p.Length >= 2 ? p[1] : p[0]) != 0;
        ApplicationLog.Current?.Debug("Vivo", $"空间音频主动上报：{(_spatialSoundEnabled == true ? "开" : "关")}。");
        _state.NotifyChanged();
    }

    private void OnSoundSceneReport(ProtocolFrame frame)
        => ApplySoundEffectScene(frame.Payload.Span);

    private void ApplySoundEffectScene(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return;

        var scene = payload.Length >= 2 ? payload[1] : payload[0];
        var name = SoundEffectSceneSnapshot.ResolveName(scene) ?? $"场景{scene}";
        ApplicationLog.Current?.Debug("Vivo", $"音效场景更新：{name}（字节 0x{scene:X2}）。");
        _state.SetSoundEffectScene(new SoundEffectSceneSnapshot(scene, name));
    }

    private void OnPlayStateReport(ProtocolFrame frame)
    {
        var p = frame.Payload.Span;
        if (p.Length < 1)
            return;
        var v = p.Length >= 2 ? p[1] : p[0];
        ApplicationLog.Current?.Debug("Vivo", $"媒体播放状态：{(v != 0 ? "播放中" : "暂停/停止")}（0x{v:X2}）。");
    }

    // 注册通知握手：与 OPPO 同源。依次发空载荷帧，耳机应答后即主动推送各 report 帧。
    // 用单向发送（不等应答）避免个别机型不支持时阻塞；推送由上方订阅捕获。
    private async Task RegisterNotificationsAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        ushort[] chain =
        [
            VivoConstants.RegisterNotificationsStart,
            VivoConstants.RegisterNotificationsQuery,
            VivoConstants.RegisterNotificationsEnable,
            VivoConstants.RegisterNotification,
            VivoConstants.RegisterNotificationsEnd,
        ];

        foreach (var command in chain)
        {
            try
            {
                await link.SendFireAndForgetAsync(command, Array.Empty<byte>(), cancellationToken);
            }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Debug("Vivo", $"注册通知帧 0x{command:X4} 发送失败（可忽略）：{exception.Message}");
            }
        }

        // 仅当 0x0300 握手成功（通道确认为活的 GAIA 通道）时才打印“已启用主动上报”，
        // 避免死通道上半死不活却误报成功、误导排查。
        if (_handshakeOk)
            ApplicationLog.Current?.Info("Vivo", "已发送 vivo 注册通知握手，启用耳机主动上报。");
        else
            ApplicationLog.Current?.Info("Vivo", "vivo 注册通知握手未确认（0x0300 握手未成功），主动上报可能不生效。");
    }

    private static string FormatAddress(ReadOnlySpan<byte> wireAddress)
    {
        var parts = new string[6];
        for (var index = 0; index < wireAddress.Length; index++)
            parts[5 - index] = wireAddress[index].ToString("X2");

        return string.Join(':', parts);
    }

    private static bool TryParseAddress(string? address, out byte[] bytes)
    {
        bytes = [];
        var parts = address?.Split(':');
        if (parts is null || parts.Length != 6)
            return false;

        bytes = new byte[6];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (!byte.TryParse(parts[index], NumberStyles.AllowHexSpecifier, null, out bytes[index]))
                return false;
        }

        return true;
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

        ApplicationLog.Current?.Debug("Vivo",
            $"电量更新：左={left?.ToString() ?? "-"}%，右={right?.ToString() ?? "-"}%，仓={caseP?.ToString() ?? "-"}%，充电位=0x{charging:X2}。");

        _state.SetBattery(
            left.HasValue ? new BatteryLevel(left.Value, (charging & 1) != 0) : null,
            right.HasValue ? new BatteryLevel(right.Value, (charging & 2) != 0) : null,
            caseP.HasValue ? new BatteryLevel(caseP.Value, (charging & 4) != 0) : null);
    }

    private void ApplyNoise(ReadOnlySpan<byte> payload)
    {
        // vivo 0x0230 响应固定 3 字节：[status=0][mode][constant=0x04]（已用 TWS 3e 真机验证）。
        // mode 字节取值与 VivoConstants 对齐：0=降噪(ANC)，1=关闭(Off)，2=通透(Transparency)。
        // 为兼容其它型号，若首字节非空则退化为“首字节即 mode”。
        if (payload.Length < 1)
            return;

        var modeByte = payload.Length >= 2 && payload[0] == 0 ? payload[1] : payload[0];
        var mode = MapFromVivoMode(modeByte);
        if (mode == NoiseMode.Unknown)
        {
            ApplicationLog.Current?.Debug("Vivo", $"降噪模式字节无法识别：0x{modeByte:X2}，忽略。");
            return;
        }

        ApplicationLog.Current?.Debug("Vivo", $"降噪模式更新：{mode}（字节 0x{modeByte:X2}，原始 {FormatBytes(payload)}）。");
        _state.SetNoise(new NoiseSnapshot(mode, null));
    }

    private static string FormatBytes(ReadOnlySpan<byte> bytes)
        => bytes.Length == 0 ? "(空)" : BitConverter.ToString(bytes.ToArray());

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
        // 降噪三档：key 必须与 DeviceProfileLoader.AncLabel 的识别集一致
        // （"Off"/"NC"/"Transparency"），否则会被兜底成“降噪”，导致三个按钮同名。
        IReadOnlyList<NoiseOptionModel> noiseOptions =
        [
            new("Off", NoiseMode.Off, VivoConstants.NoiseOff, []),
            new("NC", NoiseMode.NoiseCancellation, VivoConstants.NoiseAnc, []),
            new("Transparency", NoiseMode.Transparency, VivoConstants.NoiseTransparency, []),
        ];

        // 可见控件由 vivo 能力白名单（EarbudFeatures.FeatureID）按型号决定，而非硬编码；
        // 开发期未知型号乐观显示，便于真机测试命令实现。
        var visibleControls = VivoFeatureMatrix.ResolveVisibleControls(_deviceName);

        // 当前开关状态（设备轮询/回读得到）；查找耳机为瞬时动作，无持久状态。
        var controlStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (_gameModeEnabled is { } gameOn)
            controlStates["game-mode"] = gameOn;
        if (_spatialSoundEnabled is { } spatialOn)
            controlStates["spatial-sound"] = spatialOn;

        // 测试期默认可操作；确认不支持的型号由白名单隐藏控件后此字典自然不含该键。
        var controlEnabledStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in visibleControls)
            controlEnabledStates[key] = true;

        var currentNoiseKey = _state.Snapshot().Noise.Mode switch
        {
            NoiseMode.Off => "Off",
            NoiseMode.NoiseCancellation => "NC",
            NoiseMode.Transparency => "Transparency",
            _ => "Off"
        };

        return new BrandPresentation(
            _deviceName ?? "vivo / iQOO TWS",
            false,
            false,
            false,
            true,
            IsFeatureLive(VivoFeatureMatrix.DualConnection),
            [],
            BrandPresentation.DefaultCustomEqMinimumGain,
            BrandPresentation.DefaultCustomEqMaximumGain,
            [],
            visibleControls,
            controlStates,
            controlEnabledStates,
            noiseOptions,
            currentNoiseKey);
    }
}
