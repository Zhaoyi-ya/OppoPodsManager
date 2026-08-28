using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using OppoPodsManager.Communication.Abstractions;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Brands.Vivo.Models;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Core;
using OppoPodsManager.Control.Core.Features;
namespace OppoPodsManager.Control.Brands.Vivo;
// vivo / iQOO TWS 会话管理。
//
// ⚠ 协议命令码以官方 App 反编译注册表（EarbudSettingsFetcher.fetchEarbudsSettingsFromCommand）+ 真机抓包为权威真值源。
//   已验证并接入：
//     电量(0x0207/0x8207，恒 GAIA v4)、噪声控制(0x0130/0x0230/0x8130/0x8230，真正切出声模式，
//       0x010C 仅改 ancModeConfig 不切模式)、佩戴(0x0103/0x0203/0x8203 + 实时佩戴 0x020D/0x820D)、
//     音效/EQ(0x0118/0x0218/0x8118/0x8218)、双击手势(0x0102/0x0202/0x8202)、
//     长按功能(0x0131/0x0231/0x8131/0x8231，APK set_long_press)、查找耳机(0x0120/0x8120，=set_audio_playState)、
//     双连(0x0249/0x8249/0x014A/0x014C/0x014D)、低延迟游戏(0x0151/0x0251/0x8151/0x8251)、
//     空间音频(0x0139/0x0239/0x8139/0x8239)。
//   ⚠ 官方 App 无独立"游戏模式"GAIA 命令：游戏模式即低延迟游戏(0x0151)，旧工程误用的 0x0220(=get_audio_playState)已删除。
//   连接建立后发送 0x0202~0x0206 注册通知握手，使耳机开始主动推送各 report 帧（与 OPPO 同源）。
// 控件可见性由 VivoFeatureMatrix 按连接型号决定：已知型号仅显示其能力集合内功能，未知型号默认乐观显示，
// 便于真机逐项验证命令实现，确认不支持的功能由白名单精确隐藏。
//   连接建立后发送 0x0202~0x0206 注册通知握手，使耳机开始主动推送各 report 帧（与 OPPO 同源）。
// 控件可见性由 VivoFeatureMatrix 按连接型号决定：已知型号仅显示其能力集合内功能，未知型号默认乐观显示，
// 便于真机逐项验证命令实现，确认不支持的功能由白名单精确隐藏。
internal sealed class VivoManager : BrandManagerBase, IBrandManager
{
    private readonly VivoModelCatalog _modelCatalog;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private readonly SubscriptionSet _notificationSubscriptions = new();
    private string? _deviceName;
    private string? _manualModel;
    private byte? _spatialScene;
    private bool _audioEffectVerified;
    private VivoProfile _profile = VivoProfile.FamilyDefaultV4;
    private VivoNoiseModeMap _noiseMap = VivoNoiseModeMap.Canonical; // 按型号解析的噪声模式字节/档位映射
    // ---- dszsu: 结构化能力系统 ----
    private VivoDeviceCapability _vivoCapability = new(null);
    // ---- 本地 bug 修复 + 多设备 + 序号守卫 ----
    private bool? _gameModeEnabled;
    private bool? _spatialSoundEnabled;
    private bool? _wearDetectionEnabled;
    private bool? _hearingProtectionEnabled;
    private bool? _dualDeviceEnabled;
    // 运行期探测到的"不支持"功能（查询超时），本会话内停止轮询并隐藏对应控件。
    private readonly HashSet<int> _runtimeUnsupported = new();
    // 多连接（双设备）：订阅耳机主动上报 / 时间请求，并缓存最近一次设备列表（MAC+state，7 字节外形式）供全量下发。
    private readonly SubscriptionSet _subscriptions = new();
    private readonly List<(byte[] Address, byte State)> _multiConnectCache = new();
    private readonly object _multiConnectGate = new();
    // 各资源查询的序号守卫：只接受"最新发出"的查询响应，丢弃过期的旧查询响应，
    // 避免轮询中已在途的旧查询在设值之后回灌，造成 UI 闪回切换前状态（OPPO 用设备推送无此竞态）。
    private int _noiseSeq, _noiseHighest;
    private int _gameSeq, _gameHighest;
    private int _spatialSeq, _spatialHighest;
    // 设值后设备约 1~2s 才真正落定：此窗口内丢弃降噪回读，避免轮询读到切换前的旧模式覆盖乐观值（UI 回闪）。
    private DateTime _noiseApplyDeadline = DateTime.MinValue;
    // 双击手势 / 长按功能 最近一次从耳机收到的配置（左右耳分别存储：长按功能码左右独立）。
    private byte? _doubleTapLeft;
    private byte? _doubleTapRight;
    private byte? _longPressLeftFunc;
    private byte? _longPressRightFunc;
    private readonly VivoGestureProfile _gestureProfile = new();
    // 噪声控制当前模式与降噪档位（SET/REPORT 后维护，便于 UI 回显）。
    private byte _noiseMode = 0xFF;   // 0xFF = 尚未得知
    private byte _reduceModel;        // 降噪档位（reduceNoiseModelConfig），随回读更新
    private readonly Dictionary<NoiseMode, byte> _reduceModelByMode = new(); // 设备回读的每模式 reduceModel 缓存，SET 时原样回传
    // 0x0300 握手是否成功：仅成功时才说明当前通道是活的 GAIA 通道，可安全启用主动上报。
    private bool _handshakeOk;
    public VivoManager(VivoModelCatalog? modelCatalog = null)
    {
        _modelCatalog = modelCatalog ?? new VivoModelCatalog([]);
        State.Changed += OnStateChanged;
    }
    public event EventHandler<BusinessSnapshot>? StateChanged;
    public BusinessSnapshot Snapshot => State.Snapshot();
    public DeviceCapability Capability => _vivoCapability.ToDeviceCapability();
    public IReadOnlyList<string> ModelNames => _modelCatalog.ModelNames;
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree
        => _modelCatalog.ModelTree;
    public ModelCatalogLocation? FindModelLocation(string? modelName) => _modelCatalog.FindLocation(modelName);
    public BrandPresentation Presentation => BuildPresentation();
    public bool CanManageMultiDevice => IsFeatureLive(VivoFeatureMatrix.DualConnection);
    public void SetInteractivePolling(bool enabled)
    {
        // 交互界面打开时缩短电量保底读取间隔，功能状态优先由设备通知更新。
        InteractivePolling = enabled;
        ApplicationLog.Current?.Debug("Vivo", $"交互轮询状态已更新：enabled={enabled}。");
    }
    public async Task DisconnectAsync()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
        // 解除对耳机主动帧（0x8509 时间请求 / 0x8249 双连上报）的订阅，避免泄漏到下一次会话。
        _subscriptions.DisposeAll();
        lock (_multiConnectGate)
            _multiConnectCache.Clear();
        // dszsu: 重置空间/音效状态
        _spatialScene = null;
        _audioEffectVerified = false;
        DisposeNotificationSubscriptions();
        if (Link is not null)
        {
            var link = Link;
            Link = null;
            // 必须 await：否则底层 RFCOMM socket 在后台关闭，探测会话会被泄漏，
            // 导致后续真正连接时该通道仍被占用、port 0 被拒并退化到不认 GAIA 的裸通道。
            await link.DisposeAsync();
        }
        State.Reset();
    }
    public ValueTask DisposeAsync()
    {
        State.Changed -= OnStateChanged;
        return new ValueTask(DisconnectAsync());
    }
    public void SetManualModel(string? modelName)
    {
        _manualModel = string.IsNullOrWhiteSpace(modelName) ? null : modelName;
        ResolveCapability();
        _audioEffectVerified = false;
        if (State.Snapshot().IsConnected)
        {
            State.SetConnected(_deviceName ?? string.Empty);
            if (Link is not null && _vivoCapability.SupportsAudioEffect)
                _ = RefreshAudioEffectAsync(Link, CancellationToken.None);
        }
    }
    // ---- OPPO 专属功能（vivo 不支持，保持 stub）----
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    // ---- vivo 已接通 ----
    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken)
        => Link is null ? Task.FromResult(false) : SetWearDetectionCoreAsync(enabled, cancellationToken);
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken)
        => Link is null ? Task.FromResult(false) : SetHearingProtectionCoreAsync(enabled, cancellationToken);
    // 通话操作：来电时「双击=接听/挂断、长按=拒接」的独立触控开关（0x0150 set_touch_operation_button，
    // 与双击手势 0x0102 的 0x04/0x14「接听/挂断通话」是两套功能，此处为 ScrewVivoTWS AcceptCallMaker 同款）。
    public async Task<bool> SetCallControlAsync(bool doubleTapAnswer, bool longPressReject, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        try
        {
            var mode = (byte)((doubleTapAnswer ? VivoConstants.CallOpDoubleTapAnswer : 0)
                            | (longPressReject ? VivoConstants.CallOpLongPressReject : 0));
            var response = await Link.RequestAsync(
                VivoConstants.SetTouchOperationButton,
                VivoConstants.AckTouchOperationButton,
                new byte[] { VivoConstants.CallOpPrefix, mode },
                cancellationToken);
            return response is not null;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置通话操作失败：{exception.Message}", exception);
            return false;
        }
    }
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken)
        => SetDualDeviceCoreAsync(enabled, cancellationToken);
    // ---- dszsu: 完整实现（带 capability 检查 + 错误处理）----
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => SetSpatialSoundCoreAsync(enabled, cancellationToken);
    // 使用 vivo 官方低延迟游戏模式命令更新耳机状态。
    public async Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (Link is null || !_vivoCapability.SupportsLowLatencyGaming)
            return false;
        try
        {
            var response = await Link.RequestAsync(
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
        if (Link is null || !_vivoCapability.SupportsAudioEffect || !_audioEffectVerified
            || !_vivoCapability.AudioEffectPresetKeys.Contains(VivoAudioEffectCatalog.GetPresetKey(presetId)))
        {
            return false;
        }
        try
        {
            var response = await Link.RequestAsync(
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
        if (Link is null || !_vivoCapability.SupportsSpatialAudio)
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
            var response = await Link.RequestAsync(
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
    // ---- 本地: 查找耳机 + 多设备（完整实现）----
    public Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken) => SetFindDeviceCoreAsync(enabled, cancellationToken);
    public Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken)
        => Link is null ? Task.FromResult(false) : RefreshMultiDeviceCoreAsync(Link, cancellationToken);
    public Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken)
        => Link is null ? Task.FromResult(false) : RefreshMultiDeviceCoreAsync(Link, cancellationToken);
    // ---- dszsu: 返回 false 的功能 ----
    public Task<bool> SetEqualizerCustomAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    // ---- 触控手势：品牌无关展示与下发 ----
    public IReadOnlyList<GestureEntry> GestureEntries
    {
        get
        {
            var list = new List<GestureEntry>();
            foreach (var source in _gestureProfile.SupportedSources)
            {
                foreach (var kind in _gestureProfile.GetSupportedGestures(source))
                {
                    foreach (var ear in new[] { EarSide.Left, EarSide.Right })
                    {
                        var options = _gestureProfile.GetActionOptions(kind, ear, source);
                        var current = ResolveCurrentGesture(kind, ear);
                        list.Add(new GestureEntry(source, kind, ear, _gestureProfile.IsGestureConfigurable(kind, source),
                            LongPressRenderMode.CycleSet, options, current));
                    }
                }
            }
            return list;
        }
    }
    public Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken)
        => SetTouchGestureCoreAsync(ear, kind, action, source, cancellationToken);
    // vivo 的内置音效走独立音频效果协议（VivoAudioEffectCatalog），自定义 EQ 不通过本接口消费；
    // VivoEqualizerProfile 仅负责把协议键 "Vivo.AudioEffect.x" 解析为本地化显示名，其余委托空实现。
    public IEqualizerProfile EqualizerProfile => VivoEqualizerProfile.Instance;
    private GestureActionKind ResolveCurrentGesture(TapKind kind, EarSide ear)
    {
        if (kind == TapKind.LongPress)
        {
            var func = ear == EarSide.Left ? _longPressLeftFunc : _longPressRightFunc;
            return func.HasValue
                ? (_gestureProfile.DecodeLongPress(func.Value) ?? GestureActionKind.None)
                : GestureActionKind.None;
        }
        var raw = ear == EarSide.Left ? _doubleTapLeft : _doubleTapRight;
        return raw.HasValue
            ? (_gestureProfile.DecodeTap(ear, raw.Value) ?? GestureActionKind.None)
            : GestureActionKind.None;
    }
    private async Task<bool> SetTouchGestureCoreAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        try
        {
            if (kind == TapKind.LongPress)
            {
                // 长按 SET 0x0131 需左右耳功能码一同下发：[type, leftCode, rightCode]。
                var otherRaw = ear == EarSide.Left ? _longPressRightFunc : _longPressLeftFunc;
                var payload = _gestureProfile.EncodeSet(ear, kind, action, source, otherRaw);
                if (payload is null)
                    return false;
                await Link.RequestAsync(VivoConstants.SetLongPressFunc, VivoConstants.AckLongPressFunc, payload, cancellationToken);
                if (payload.Length >= 3)
                {
                    _longPressLeftFunc = payload[1];
                    _longPressRightFunc = payload[2];
                }
            }
            else
            {
                var payload = _gestureProfile.EncodeSet(ear, kind, action, source);
                if (payload is null)
                    return false;
                await Link.RequestAsync(VivoConstants.SetDoubleTap, VivoConstants.AckDoubleTap, payload, cancellationToken);
                if (ear == EarSide.Left) _doubleTapLeft = payload[0]; else _doubleTapRight = payload[0];
            }
            State.NotifyChanged();
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置触控手势失败：{exception.Message}", exception);
            return false;
        }
    }
    // ---- 触控手势配置查询（连接建立后后台补偿，与游戏/空间音效同模式）----
    // 双击 QUERY 0x0202 → Report 0x8202；长按 QUERY 0x0231 → Report 0x8231（均已订阅）。
    // 任一查询超时（可能为不支持的型号）仅记录调试日志，不标记"运行期不支持"、不阻塞流程。
    private async Task RefreshGestureConfigAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            await link.RequestAsync(VivoConstants.QueryDoubleTap, VivoConstants.ReportDoubleTapConfig, Array.Empty<byte>(), cancellationToken);
            await link.RequestAsync(VivoConstants.QueryLongPressFunc, VivoConstants.ReportLongPressFunc, Array.Empty<byte>(), cancellationToken);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"手势配置查询失败（可能为不支持的型号）：{exception.Message}");
        }
    }
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    // ---- 接口：自定义 EQ（vivo 不支持）----
    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public bool IsValidCustomEqualizerName(string name) => false;
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, -6, 6, [], []);
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry) => [];
    // ---- 接口：多设备显示状态 ----
    // 与 OPPO 侧 OppoManager.GetMultiDeviceDisplayState 完全一致：复用 MultiDevicePolicy 过滤本地隐藏设备，
    // 并从可显示设备中筛出 ConnectionState==2 的已连接设备，作为设置页"优先级设备"下拉的可选项。
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
    {
        var snapshot = State.Snapshot().MultiDevice;
        return snapshot is null
            ? new MultiDeviceDisplayState([], [])
            : MultiDevicePolicy.BuildDisplayState(snapshot, hiddenAddresses);
    }
    // ---- 接口：降噪 key/protocol 委托 ----
    public async Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken)
    {
        var mode = modeKey switch
        {
            "Off" or "off" => NoiseMode.Off,
            "NC" or "anc" => NoiseMode.NoiseCancellation,
            "Transparency" or "transparency" => NoiseMode.Transparency,
            _ => NoiseMode.Unknown
        };
        if (mode == NoiseMode.Unknown)
            return false;
        return await SetNoiseCancellationAsync(mode, cancellationToken);
    }
    public async Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken)
    {
        var mode = protocolIndex switch
        {
            _ when protocolIndex == _noiseMap.Off => NoiseMode.Off,
            _ when protocolIndex == _noiseMap.NoiseCancellation => NoiseMode.NoiseCancellation,
            _ when protocolIndex == _noiseMap.Transparency => NoiseMode.Transparency,
            _ => (NoiseMode?)null
        };
        if (mode is null)
            return false;
        return await SetNoiseCancellationAsync(mode.Value, cancellationToken);
    }
    public Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken)
        => Link is null ? Task.FromResult(false) : OperateMultiDeviceCoreAsync(automatic ? MultiDeviceOperation.AutomaticPriority : MultiDeviceOperation.SetPriority, address, cancellationToken);
    public Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken)
        => Link is null ? Task.FromResult(false) : OperateMultiDeviceCoreAsync(operation, address, cancellationToken);
    public async Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken)
    {
        if (MapToVivoMode(mode) is null)
            return false;
        // 官方 App 为全局单模式（左右耳共用同一 mode 字节），直接设当前生效模式。
        return await SetNoiseModeCoreAsync(mode, cancellationToken);
    }
    // ---- 会话建立 ----
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _deviceName = deviceName;
        ResolveCapability();
        _profile = VivoModels.SelectProfile(deviceName);
        _noiseMap = _modelCatalog.Find(deviceName)?.NoiseMap ?? VivoNoiseModeMap.Canonical;
        _reduceModelByMode.Clear();
        var setSuffixText = _noiseMap.NoiseSetSuffix is null
            ? "reduceModel-style(2字节)"
            : "[" + string.Join(",", _noiseMap.NoiseSetSuffix) + "]";
        ApplicationLog.Current?.Debug("Vivo", $"选择协议画像：device={deviceName}，model={_vivoCapability.ModelName}，known={_vivoCapability.IsKnownModel}，gaiaVersion={_profile.GaiaVersion}，queryPayload={_profile.NoiseQueryPayload.Length} 字节，噪声SET载荷={setSuffixText}。");
        Link = link;
        // dszsu: 统一安装通知处理器
        InstallNotificationHandlers(link);
        // 本地: 额外订阅时间请求/双连上报/佩戴/空间/音效场景/播放状态（dszsu 的 InstallNotificationHandlers 只覆盖了基础帧）
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.PeerTimeRequest, OnPeerTimeRequest));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportMultiConnect, OnMultiConnectReport));
        // 实时佩戴/在盒状态（0x820D，随取放主动推送，是 UI 佩戴状态真正的实时来源）
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportWearState, OnWearReport));
        // 佩戴检测开关设置（0x8203，仅连接/改设置时上报一次，state 0=关 1=开；不反映实时佩戴）
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportWearDetection, OnWearDetectionReport));
        // 听力保护开关（0x8252，连接/改设置时上报一次，state 0=关 1=开）
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportHearingProtection, OnHearingProtectionReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportSpatialSound, OnSpatialReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportDoubleTapConfig, OnDoubleTapConfigReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportLongPressFunc, OnLongPressFuncReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.TelemetryReport, OnTelemetryReport));
        // 固件/型号主动上报（兜底；空闲态一般不推，主要靠首次连接主动查询）
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportFirmware, OnFirmwareReport));
        _subscriptions.Add(link.Router.Subscribe(VivoConstants.ReportModel, OnModelReport));
        // 每次开新会话重置运行期探测到的"不支持"功能（避免首次连接因通道协商失败而误判永久隐藏）。
        _runtimeUnsupported.Clear();
        // 握手用于确认当前 RFCOMM 通道是活的 GAIA 通道。历史 bug：自动连接阶段若退化到裸通道
        // Channel-1/15，能建链但不回任何 GAIA 帧，握手会超时；原逻辑"可忽略"后继续，导致耳机看似已
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
        // 降噪始终查询（已验证功能）
        await RefreshNoiseAsync(link, cancellationToken);
        // 设备信息：首次连接主动查固件版本/型号（同 OPPO 首次连接查 0x0105），空闲态不主动推送
        await RefreshDeviceInfoAsync(link, cancellationToken);
        // 实时佩戴/在盒状态（0x820D）随取放主动推送，由订阅处理；此处查询仅用于即时校准。
        await RefreshWearStatusAsync(link, cancellationToken);
        // 佩戴检测开关：查询 0x0203 → 0x8203 上报，回填功能开关勾选态（state 0=关 1=开）
        await RefreshWearDetectionAsync(link, cancellationToken);
        // 听力保护：仅对声明支持该功能（DeviceModels.json hearing_protection=1）的型号查询，
        // 避免对不支持的型号浪费一次命令超时（虽被 catch 吞掉不阻塞，但会拖慢连接建立）。
        if (_vivoCapability.Model?.HasFeature("hearing_protection") == true)
            await RefreshHearingProtectionAsync(link, cancellationToken);
        // dszsu: 按 capability 条件查询游戏/空间/音效
        if (_vivoCapability.SupportsLowLatencyGaming)
            await RefreshGameModeAsync(link, cancellationToken);
        if (_vivoCapability.SupportsSpatialAudio)
            await RefreshSpatialAudioAsync(link, cancellationToken);
        if (_vivoCapability.SupportsAudioEffect)
            await RefreshAudioEffectAsync(link, cancellationToken);
        State.SetConnected(deviceName);
        _pollCancellation = new CancellationTokenSource();
        _pollTask = RunPollingAsync(link, _pollCancellation.Token);
        // 游戏模式/空间音效命令字尚未真机验证（见 VivoConstants 占位说明），且未知型号按白名单
        // 乐观显示；若在此同步等待其初始查询，最坏情况下两个命令各超时约 4s，会使"已连接"状态
        // 延迟近 8s 才上报。改为连接建立后在后台补偿查询：命中则回填开关状态并通知 UI，
        // 超时则由运行期探测标记"不支持"并隐藏对应控件（轮询中也不再重复查询）。
        if (IsFeatureLive(VivoFeatureMatrix.LowLatencyGaming))
            _ = RefreshGameModeAsync(link, cancellationToken);
        if (IsFeatureLive(VivoFeatureMatrix.SpatialAudio))
            _ = RefreshSpatialAudioAsync(link, cancellationToken);
        // 双连列表：对声明支持双连的型号（含 TWS 3e 等 KnownForced 型号）均尝试后台拉取设备列表。
        // 超时则静默忽略，不标记"运行期不支持"（避免误杀后续轮询）。
        // 注意：管理操作（开关/增删/优先级）仍由 CanManageMultiDevice(=IsFeatureLive) 控制，KnownForced 型号的 UI 开关保持隐藏。
        if (VivoFeatureMatrix.IsFeatureSupported(_deviceName, VivoFeatureMatrix.DualConnection)
            && !_runtimeUnsupported.Contains(VivoFeatureMatrix.DualConnection))
            _ = RefreshMultiDeviceCoreAsync(link, cancellationToken);
        // 触控手势配置：查询双击(0x0202)/长按(0x0231)，由已订阅的 0x8202/0x8231 上报回填。
        // 长按上报帧格式待真机核对，失败静默忽略，不影响连接建立。
        _ = RefreshGestureConfigAsync(link, cancellationToken);
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
        var seq = ++_noiseSeq;
        _noiseHighest = Math.Max(_noiseHighest, seq);
        try
        {
            // 噪声查询帧载荷按型号画像（官方 App：v4 家族带 1 字节 [0x00]，TWS 3e/Air3Pro 为空），见 VivoProfile.NoiseQueryPayload。
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
    // ---- 设备信息主动查询（首次连接即查，填充"设备详情"页固件版本/型号）----
    // 空闲态耳机不会主动推送固件版本，必须主动发 0x021C（同 OPPO 首次连接查 0x0105）。
    // 解析容错：先记录原始字节便于诊断，再尝试 UTF8 解码（跳过首 2 字节，与 OPPO 固件解析同理）；
    // 若解码结果非可打印固件串，回退为十六进制显示，不崩溃。
    private async Task RefreshDeviceInfoAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryFirmware, VivoConstants.ReportFirmware, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Vivo", $"初始固件查询：success={response is not null}。");
            if (response is not null)
                ApplyFirmware(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"固件查询失败：{exception.Message}");
        }
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryModel, VivoConstants.ReportModel, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Vivo", $"初始型号查询：success={response is not null}。");
            if (response is not null)
                ApplyModel(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"型号查询失败：{exception.Message}");
        }
    }
    private void OnFirmwareReport(ProtocolFrame frame) => ApplyFirmware(frame.Payload.Span);
    private void OnModelReport(ProtocolFrame frame) => ApplyModel(frame.Payload.Span);
    private void ApplyFirmware(ReadOnlySpan<byte> payload)
    {
        // 诊断：记录原始字节，便于确认 vivo 固件应答格式
        var rawHex = Convert.ToHexString(payload.ToArray());
        ApplicationLog.Current?.Debug("Vivo", $"固件版本原始响应：bytes={payload.Length}，hex={rawHex}。");
        if (payload.Length < 1)
            return;
        // vivo 固件为紧凑二进制版本（实测 00 01 02 03 04 05 09 0C → 2.5.9）：
        // 版本三段分别落在固定字节位 [2]=主版本、[5]=次版本、[6]=修订号，与官方 APP 显示一致。
        // 首字节 0x00 区分于字符串应答；若载荷主要是可打印文本则按字符串处理（不误伤可读固件串）。
        if (payload[0] == 0 && payload.Length >= 7 && !IsMostlyPrintable(payload))
        {
            var major = payload[2];
            var minor = payload[5];
            var patch = payload[6];
            // 合理性闸门：三段都在 [0,99] 才视为合法语义化版本，否则回退十六进制避免误报
            if (major <= 99 && minor <= 99 && patch <= 99)
            {
                SetFirmware(NormalizeFirmware($"{major}.{minor}.{patch}"), rawHex);
                return;
            }
        }
        // 兜底：可打印字符串（跳过首 2 字节头，与 OPPO 固件解析一致；若 0 字节头则整体解码）
        var text = payload[0] == 0 && payload.Length >= 3
            ? Encoding.UTF8.GetString(payload[2..]).TrimEnd('\0', ' ')
            : Encoding.UTF8.GetString(payload).TrimEnd('\0', ' ');
        // 仅接受含可打印 ASCII 的结果；否则回退十六进制，避免把二进制当版本号
        var printable = !string.IsNullOrEmpty(text) && text.All(c => c == '.' || (c >= 0x20 && c <= 0x7E));
        SetFirmware(printable ? NormalizeFirmware(text) : rawHex, rawHex);
    }
    // 统一写入固件版本（去重 + 日志）
    private void SetFirmware(string? firmware, string rawHex)
    {
        if (string.IsNullOrEmpty(firmware))
            return;
        EnsureIdentity();
        var current = State.Snapshot().Identity!;
        if (current.FirmwareVersion != firmware)
        {
            State.SetIdentity(current with { FirmwareVersion = firmware });
            ApplicationLog.Current?.Info("Vivo", $"固件版本解析完成：version={firmware}（rawHex={rawHex}）。");
        }
    }
    // 固件版本归一化：遥测 "V" 字段形如 "2.5.9_2.5.9"（下划线左=固件、右=兼容/副版本），
    // 官方 APP 仅展示左侧；去掉下划线及其后内容，统一为 "主.次.修订"。
    private static string NormalizeFirmware(string firmware)
    {
        var underscore = firmware.IndexOf('_');
        return underscore >= 0 ? firmware[..underscore] : firmware;
    }
    // 判断载荷是否主要是可打印 ASCII（用于区分二进制固件与字符串固件应答）
    private static bool IsMostlyPrintable(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return false;
        var printable = 0;
        foreach (var b in payload)
            if (b is (byte)'.' or >= 0x20 and <= 0x7E)
                printable++;
        return printable * 2 >= payload.Length; // 可打印占比 >= 50%
    }
    private void ApplyModel(ReadOnlySpan<byte> payload)
    {
        var rawHex = Convert.ToHexString(payload.ToArray());
        ApplicationLog.Current?.Debug("Vivo", $"型号原始响应：bytes={payload.Length}，hex={rawHex}。");
        if (payload.Length < 1)
            return;
        var text = payload[0] == 0 && payload.Length >= 3
            ? Encoding.UTF8.GetString(payload[2..]).TrimEnd('\0', ' ')
            : Encoding.UTF8.GetString(payload).TrimEnd('\0', ' ');
        var printable = !string.IsNullOrEmpty(text) && text.All(c => c == '.' || (c >= 0x20 && c <= 0x7E));
        var model = printable ? text : null;
        if (!string.IsNullOrEmpty(model))
        {
            EnsureIdentity();
            var current = State.Snapshot().Identity!;
            if (current.ModelName != model)
            {
                State.SetIdentity(current with { ModelName = model });
                ApplicationLog.Current?.Info("Vivo", $"型号解析完成：model={model}。");
            }
        }
    }
    // vivo 不走 OPPO 的 DeviceInfoManager 产品查询流程，首次填设备信息前确保 Identity 已建立
    private void EnsureIdentity()
    {
        if (State.Snapshot().Identity is not null)
            return;
        State.SetIdentity(new DeviceIdentity(
            string.Empty,
            _deviceName ?? "vivo / iQOO TWS",
            _vivoCapability.IsKnownModel ? _vivoCapability.ModelName : null,
            null,
            null));
    }
    // 读取一次低延迟游戏模式；官方通知接入前不参与周期轮询。
    private async Task RefreshGameModeAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        var seq = ++_gameSeq;
        _gameHighest = Math.Max(_gameHighest, seq);
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QueryLowLatencyGaming,
                VivoConstants.ReportLowLatencyGaming,
                Array.Empty<byte>(),
                cancellationToken);
            if (seq < _gameHighest)
            {
                ApplicationLog.Current?.Debug("Vivo", $"游戏模式查询响应已过期（seq={seq} < 最新={_gameHighest}），跳过。");
                return;
            }
            if (response is not null && response.Payload.Length >= 1)
            {
                var p = response.Payload.Span;
                _gameModeEnabled = (p.Length >= 2 ? p[1] : p[0]) != 0;
            }
        }
        catch (TimeoutException)
        {
            if (_runtimeUnsupported.Add(VivoFeatureMatrix.LowLatencyGaming))
            {
                ApplicationLog.Current?.Info("Vivo", "游戏模式查询超时，本会话停止轮询并隐藏控件。");
                State.NotifyChanged();
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"低延迟游戏模式查询失败：{exception.Message}");
        }
    }
    // 读取空间音频当前模式及设备可选的场景索引。
    private async Task RefreshSpatialAudioAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        var seq = ++_spatialSeq;
        _spatialHighest = Math.Max(_spatialHighest, seq);
        try
        {
            var response = await link.RequestAsync(
                VivoConstants.QuerySpatialAudio,
                VivoConstants.ReportSpatialAudio,
                Array.Empty<byte>(),
                cancellationToken);
            if (seq < _spatialHighest)
            {
                ApplicationLog.Current?.Debug("Vivo", $"空间音效查询响应已过期（seq={seq} < 最新={_spatialHighest}），跳过。");
                return;
            }
            if (response is not null && response.Payload.Length >= 1)
            {
                var p = response.Payload.Span;
                _spatialSoundEnabled = (p.Length >= 2 ? p[1] : p[0]) != 0;
            }
        }
        catch (TimeoutException)
        {
            if (_runtimeUnsupported.Add(VivoFeatureMatrix.SpatialAudio))
            {
                ApplicationLog.Current?.Info("Vivo", "空间音效查询超时，本会话停止轮询并隐藏控件。");
                State.NotifyChanged();
            }
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
    // 噪声控制当前模式切换：payload = [mode, 0x03, 0x01]（GAIA v4，官方 App HCI 抓包逐字节实锤）。
    // 第二、三字节为固定后缀，非降噪档位；旧工程误用档位发 2 字节载荷，耳机忽略 → 软件内无法切模式。
    private async Task<bool> SetNoiseModeCoreAsync(NoiseMode mode, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        // 当前生效降噪模式走 set_noise_mode（0x0130），帧版本取型号画像 GAIA 版本（TWS 4 = v4 / TWS 3e = Air3 Pro = v3）。
        // SET 载荷按型号覆盖点（VivoNoiseModeMap.NoiseSetSuffix）生成，对齐官方 App（Windows 逆向参考 [(byte)mode, ..NoiseSetSuffix]）：
        //   · 非 null（当前全部型号）→ 固定后缀：payload = [mode, ..NoiseSetSuffix]
        //       - TWS 4 系 / TWS Air3 / iQOO TWS 2：[mode, 0x03, 0x01]（官方 App 抓包逐字节实锤）
        //       - TWS 3e：[mode, 0x03]；Air3 Pro 系：[mode, 0x04, 0x00]（Windows 参考 Tws3eV3 / Air3ProV3）
        //   · null（保留的 legacy fallback，当前无型号使用）→ 按模式取降噪档位 [mode, ReduceForMode(mode)]
        // 0x010C(set_anc_mode) 仅改 ancModeConfig、不切出声模式——真机已验证 ACK 但不出声，故改走 0x0130。
        // mode 字节取当前型号映射（_noiseMap）。
        var vivoModeByte = _noiseMap.ModeByte(mode);
        byte[] payload;
        if (_noiseMap.NoiseSetSuffix is null)
        {
            // TWS 3e 旧固件：2 字节 [mode, reduceModel]，reduceModel 随模式取档位。
            payload = new byte[] { vivoModeByte, _noiseMap.ReduceForMode(mode) };
        }
        else
        {
            var suffix = _noiseMap.NoiseSetSuffix;
            payload = new byte[suffix.Length + 1];
            payload[0] = vivoModeByte;
            suffix.CopyTo(payload, 1);
        }
        try
        {
            await Link.RequestAsync(VivoConstants.ActiveNoiseSetCommand, VivoConstants.ActiveNoiseAckCommand, payload, cancellationToken);
        }
        catch (TimeoutException)
        {
            // 设备可能以 set 自身（0x0130）或 0x8230 上报帧回包；Router 订阅已捕获并解析，
            // 状态已被 ApplyNoise 更新，故视为已生效，不报错。
            ApplicationLog.Current?.Info("Vivo",
                $"set_noise_mode(0x{VivoConstants.ActiveNoiseSetCommand:X4}) 未在约定 ack(0x{VivoConstants.ActiveNoiseAckCommand:X4}) 收到回包，但已通过其他回包帧更新状态。");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置降噪失败：{exception.Message}", exception);
            return false;
        }
        // 乐观更新：立即把 UI 切到目标模式，避免"设值后回读旧值→UI 闪回切换前状态"。
        // 设备约 1~2s 才真正落定，立即回读必然拿到切换前的值。抬高查询序号纪元，
        // 使轮询中可能已在途的旧查询响应在回程时被丢弃，不会用旧模式覆盖本次乐观值；
        // 同时开启沉降窗口，丢弃窗口内（设值后设备未落定）的回读，避免旧模式覆盖乐观值；
        // 窗口过后轮询自然读回并校正真实状态（若 SET 实际生效则读回新模式，UI 无变化）。
        ++_noiseSeq;
        _noiseHighest = _noiseSeq;
        _noiseApplyDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2.5);
        _noiseMode = _noiseMap.ModeByte(mode);
        State.SetNoise(new NoiseSnapshot(mode, null));
        return true;
    }
    // ---- T1：查找耳机（发声），游戏模式，空间音效开关 ----
    private async Task<bool> SetFindDeviceCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        try
        {
            await Link.RequestAsync(VivoConstants.SetFindDevice, VivoConstants.AckFindDevice,
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
    private async Task<bool> SetSpatialSoundCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        try
        {
            await Link.RequestAsync(VivoConstants.SetSpatialSound, VivoConstants.ReportSpatialSound,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            await RefreshSpatialAudioAsync(Link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置空间音效失败：{exception.Message}", exception);
            return false;
        }
    }
    private async Task RunPollingAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = InteractivePolling ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(1);
            await Task.Delay(interval, cancellationToken);
            if (Link != link)
                break;
            await RefreshBatteryAsync(link, cancellationToken);
            await RefreshNoiseAsync(link, cancellationToken);
            // 佩戴状态：0x8203 连接后不再主动推送，靠周期查询刷新（查询失败已在内部吞掉，不影响轮询）
            await RefreshWearStatusAsync(link, cancellationToken);
            if (IsFeatureLive(VivoFeatureMatrix.LowLatencyGaming))
                await RefreshGameModeAsync(link, cancellationToken);
            if (IsFeatureLive(VivoFeatureMatrix.SpatialAudio))
                await RefreshSpatialAudioAsync(link, cancellationToken);
            if (VivoFeatureMatrix.IsFeatureSupported(_deviceName, VivoFeatureMatrix.DualConnection)
                && !_runtimeUnsupported.Contains(VivoFeatureMatrix.DualConnection))
                await RefreshMultiDeviceCoreAsync(link, cancellationToken);
        }
    }
    // 综合"能力白名单(VivoFeatureMatrix.KnownSupported，已知型号仅显示其声明支持的功能) + 运行期超时探测"判断某功能当前应查询/展示。
    private bool IsFeatureLive(int featureId)
        => VivoFeatureMatrix.IsFeatureSupported(_deviceName, featureId)
           && !_runtimeUnsupported.Contains(featureId)
           && !VivoFeatureMatrix.IsFeatureForced(_deviceName, featureId);
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
            // 注意：此处不再将 TimeoutException 标记为"运行期不支持"。
            // 原因：多设备列表查询现在对所有声明支持双连的型号（含 KnownForced 的 TWS 3e）都尝试，
            // 超时可能只是当前无其他配对设备，不应永久禁用后续轮询。
            // 真正需要隐藏管理 UI 的由 CanManageMultiDevice(=IsFeatureLive) 控制。
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
        State.SetMultiDevice(new MultiDeviceSnapshot(devices, false, priority));
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
        if (Link is null)
            return false;
        try
        {
            // 操作前确保手头有最新设备列表，才能构造合法的全量下发帧。
            if (_multiConnectCache.Count == 0)
                await RefreshMultiDeviceCoreAsync(Link, cancellationToken);
            switch (operation)
            {
                case MultiDeviceOperation.Unpair:
                    if (!TryParseAddress(address, out var removeMac))
                        return false;
                    await Link.RequestAsync(VivoConstants.RemoveMultiConnect, VivoConstants.AckRemoveMultiConnect, removeMac, cancellationToken);
                    break;
                case MultiDeviceOperation.AutomaticPriority:
                    // vivo 无"自动优先级"概念，忽略以免误发未知命令。
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
                    await Link.RequestAsync(VivoConstants.SetMultiConnect, VivoConstants.AckMultiConnect, payload, cancellationToken);
                    break;
                }
                default:
                    return false;
            }
            // 操作后回读最新列表，刷新 UI。
            await RefreshMultiDeviceCoreAsync(Link, cancellationToken);
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
        if (Link is null)
            return false;
        try
        {
            await Link.RequestAsync(VivoConstants.EnableMultiConnect, VivoConstants.AckEnableMultiConnect,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            _dualDeviceEnabled = enabled;
            State.NotifyChanged();
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
            if (Link is null)
                return;
            // 单向应答：复用连接层的请求串行门，避免与轮询发送在字节层面交错。
            await Link.SendFireAndForgetAsync(VivoConstants.HostTimeResponse, payload, CancellationToken.None);
            ApplicationLog.Current?.Debug("Vivo", "已应答耳机时间请求 0x0509。");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"应答时间请求失败：{exception.Message}");
        }
    }
    // ---- 实时佩戴/在盒状态（REPORT 0x820D，随取放主动推送）----
    // payload [status:0][flags]，flags 位域（官方 AbstractC7500c 全 APK 唯一"在耳"判定，已逐位核对）：
    //   bit0(0x01)=左耳在盒、bit1(0x02)=右耳在盒、bit2(0x04)=左耳佩戴、bit3(0x08)=右耳佩戴。
    //   某耳两位皆 0 → 摘下(Removed)；每耳占用 2 位，不会同时在盒与佩戴。
    private void OnWearReport(ProtocolFrame frame)
        => ApplyWear(frame.Payload.Span);
    // 佩戴检测开关设置（REPORT 0x8203，仅连接/改设置时上报一次，state 0=关 1=开）。
    // 注意：这是"佩戴检测"功能的总开关，不是实时佩戴状态，切勿写入 WearSnapshot。
    private void OnWearDetectionReport(ProtocolFrame frame)
    {
        var p = frame.Payload.Span;
        if (p.Length < 2)
            return;
        var enabled = p[1] != 0;
        var stateStr = enabled ? "开" : "关";
        ApplicationLog.Current?.Debug("Vivo", "佩戴检测开关状态：" + stateStr + "（payload=" + Convert.ToHexString(p.ToArray()) + "）。");
        _wearDetectionEnabled = enabled;
        State.NotifyChanged();
    }
    private void ApplyWear(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
            return;
        var flags = payload.Length >= 2 ? payload[1] : payload[0];
        // 官方真值（AbstractC7500c 全 APK 唯一"在耳"判定，已逐位核对）：
        //   bit0(0x01)=左在盒、bit1(0x02)=右在盒、bit2(0x04)=左佩戴、bit3(0x08)=右佩戴。
        //   某耳两位皆 0 → 摘下(Removed)。
        var leftInCase  = (flags & 0x01) != 0;
        var rightInCase = (flags & 0x02) != 0;
        var leftWorn    = (flags & 0x04) != 0;
        var rightWorn   = (flags & 0x08) != 0;
        var left  = leftWorn  ? EarWearState.Worn  : (leftInCase  ? EarWearState.InCase : EarWearState.Removed);
        var right = rightWorn ? EarWearState.Worn  : (rightInCase ? EarWearState.InCase : EarWearState.Removed);
        ApplicationLog.Current?.Debug("Vivo", $"佩戴状态更新：左={left}，右={right}（flags=0x{flags:X2}，左 在盒={leftInCase}/佩戴={leftWorn}，右 在盒={rightInCase}/佩戴={rightWorn}）。");
        State.SetWear(new WearSnapshot(left, right));
    }
    private async Task<bool> SetWearDetectionCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        try
        {
            await Link.RequestAsync(VivoConstants.SetWearDetection, VivoConstants.AckWearDetection,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            ApplicationLog.Current?.Info("Vivo", $"佩戴检测开关已发送：enabled={enabled}。");
            await RefreshWearStatusAsync(Link, cancellationToken);
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
                VivoConstants.QueryWearState, VivoConstants.ReportWearState, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                ApplyWear(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"佩戴状态查询失败：{exception.Message}");
        }
    }
    // 佩戴检测开关状态：查询 0x0203，耳机回 0x8203（由 OnWearDetectionReport 解析并回填 _wearDetectionEnabled）。
    private async Task RefreshWearDetectionAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            await link.RequestAsync(
                VivoConstants.QueryWearDetection, VivoConstants.ReportWearDetection, Array.Empty<byte>(), cancellationToken);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"佩戴检测开关查询失败：{exception.Message}");
        }
    }
    // ---- 听力保护（SET 0x0152 / QUERY 0x0252 / REPORT 0x8252，APK 逆向实锤；开关型，payload [enable]）----
    // 能力由 DeviceModels.json 的 hearing_protection 精确声明（49 机型支持）；DeviceModels 无独立 WearDetection 键，故佩戴检测走另一路径。
    private void OnHearingProtectionReport(ProtocolFrame frame)
    {
        var p = frame.Payload.Span;
        if (p.Length < 2)
            return;
        var enabled = p[1] != 0;
        ApplicationLog.Current?.Debug("Vivo", "听力保护开关状态：" + (enabled ? "开" : "关") + "（payload=" + Convert.ToHexString(p.ToArray()) + "）。");
        _hearingProtectionEnabled = enabled;
        State.NotifyChanged();
    }
    private async Task<bool> SetHearingProtectionCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (Link is null)
            return false;
        try
        {
            await Link.RequestAsync(VivoConstants.SetHearingProtection, VivoConstants.AckHearingProtection,
                new byte[] { enabled ? (byte)1 : (byte)0 }, cancellationToken);
            ApplicationLog.Current?.Info("Vivo", $"听力保护开关已发送：enabled={enabled}。");
            await RefreshHearingProtectionAsync(Link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Vivo", $"设置听力保护失败：{exception.Message}", exception);
            return false;
        }
    }
    private async Task RefreshHearingProtectionAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            await link.RequestAsync(
                VivoConstants.QueryHearingProtection, VivoConstants.ReportHearingProtection, Array.Empty<byte>(), cancellationToken);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Vivo", $"听力保护查询失败：{exception.Message}");
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
        State.NotifyChanged();
    }
    // ---- 双击手势配置同步（改设置时上报，非触发事件）----
    // 0x8202 payload = [00][左动作码][右动作码]；亦为注册通知-开始 ACK（空/短帧），按形态区分。
    private void OnDoubleTapConfigReport(ProtocolFrame frame)
        => ApplyDoubleTapConfig(frame.Payload.Span);
    private void ApplyDoubleTapConfig(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3 || payload[0] != 0x00)
            return; // 注册通知 ACK 等非配置帧，忽略
        var left = payload[1];
        var right = payload[2];
        _doubleTapLeft = left;
        _doubleTapRight = right;
        var lName = VivoConstants.TapLeftCodes.TryGetValue(left, out var ln) ? ln : $"0x{left:X2}";
        var rName = VivoConstants.TapRightCodes.TryGetValue(right, out var rn) ? rn : $"0x{right:X2}";
        ApplicationLog.Current?.Info("Vivo",
            $"双击手势配置同步：左={lName}(0x{left:X2}) 右={rName}(0x{right:X2})。");
        State.NotifyChanged();
    }
    // ---- 长按手势功能（左右耳下拉仅 无/切换噪声控制；来电拒接=0xFF 之外，为官方 App 长按区下的独立开关，非左右耳选项，电脑端不实现）----
    // 上报 0x8231 帧：SET/回显为 [type, leftFunc, rightFunc]（type=5 长按），设备主动推送常带引导 0x00
    // （[00][05][left][right]，与双击上报 0x8202=[00][left][right] 对称）；查询 0x0231 对此类机型仅回 2 字节
    // （据 SET 推断为 [left, right]，无 type 前缀；若首字节恰为 0x05 则按 [type, 全局func] 处理）。
    // 长按功能码 = 噪声模式码（0xFF=无、0x0B/0x0A/0x08/0x09=切换噪声控制各循环集合），非 0x01~0x03。
    private void OnLongPressFuncReport(ProtocolFrame frame)
        => ApplyLongPressFunc(frame.Payload.Span);
    private void ApplyLongPressFunc(ReadOnlySpan<byte> payload)
    {
        byte leftFunc;
        byte rightFunc;
        if (payload.Length >= 4 && payload[0] == 0x00 && payload[1] == 0x05)
        {
            // 主动上报/SET 回显：[pad, type, left, right]（与双击上报 0x8202=[00][left][right] 对称）
            leftFunc = payload[2];
            rightFunc = payload[3];
        }
        else if (payload.Length >= 3 && payload[0] == 0x05)
        {
            // SET 下发/回显：[type, left, right]
            leftFunc = payload[1];
            rightFunc = payload[2];
        }
        else if (payload.Length == 2)
        {
            // 查询 0x0231 对此类机型（如 Tws3eV3 / DPD2321A）仅回 2 字节。
            // 据 SET([type,left,right]) 推断为 [left, right]（无 type 前缀）；
            // 若首字节恰为 0x05 则按 [type, 全局func] 处理（左右耳共用）。
            if (payload[0] == 0x05)
            {
                leftFunc = rightFunc = payload[1];
                ApplicationLog.Current?.Debug("Vivo",
                    $"长按查询返回 2 字节 [type,func]={payload[0]:X2}{payload[1]:X2}，按全局功能解析。");
            }
            else
            {
                leftFunc = payload[0];
                rightFunc = payload[1];
                ApplicationLog.Current?.Debug("Vivo",
                    $"长按查询返回 2 字节 [左,右]={payload[0]:X2}{payload[1]:X2}。");
            }
        }
        else
        {
            ApplicationLog.Current?.Debug("Vivo",
                $"长按上报形态未识别（len={payload.Length}），忽略。");
            return;
        }
        _longPressLeftFunc = leftFunc;
        _longPressRightFunc = rightFunc;
        var lName = VivoConstants.LongPressFuncCodes.TryGetValue(leftFunc, out var ln) ? ln : $"0x{leftFunc:X2}";
        var rName = VivoConstants.LongPressFuncCodes.TryGetValue(rightFunc, out var rn) ? rn : $"0x{rightFunc:X2}";
        ApplicationLog.Current?.Info("Vivo",
            $"长按手势功能：左={lName}(0x{leftFunc:X2}) 右={rName}(0x{rightFunc:X2})。");
        State.NotifyChanged();
    }
    // ---- 遥测/设备信息上报（0x8224，耳机主动推送）----
    // payload = [00 01 22] + JSON + [00] 结束符
    // JSON 含 "V"（固件版本，如 "2.5.9_2.5.9"）、"C"（硬件版本）、"D"（计数器/时间戳）
    private void OnTelemetryReport(ProtocolFrame frame)
        => ApplyTelemetry(frame.Payload.Span);
    private void ApplyTelemetry(ReadOnlySpan<byte> payload)
    {
        // 最小长度：3 字节头 + 至少 {"V":""} + 1 字节结束符 ≈ 12 字节
        if (payload.Length < 12)
            return;
        // 跳过 3 字节头 (00 01 22)，截取到末尾 00 结束符之前
        var jsonStart = 3;
        var jsonEnd = payload.Length - 1; // 跳过尾部 00
        if (jsonEnd <= jsonStart)
            return;
        var jsonBytes = payload[jsonStart..jsonEnd];
        var jsonStr = Encoding.UTF8.GetString(jsonBytes);
        try
        {
            using var doc = JsonDocument.Parse(jsonStr);
            var root = doc.RootElement;
            if (root.TryGetProperty("V", out var vElem) && vElem.ValueKind == JsonValueKind.String)
            {
                var rawFirmware = vElem.GetString();
                var firmware = NormalizeFirmware(rawFirmware ?? string.Empty);
                if (!string.IsNullOrEmpty(firmware))
                {
                    var current = State.Snapshot().Identity;
                    if (current is not null && current.FirmwareVersion != firmware)
                    {
                        State.SetIdentity(current with { FirmwareVersion = firmware });
                        ApplicationLog.Current?.Info("Vivo", $"遥测上报固件版本：{firmware}。");
                    }
                    else if (current is null)
                    {
                        // 首次收到遥测时 Identity 尚未建立，用设备名创建最小身份
                        State.SetIdentity(new DeviceIdentity(
                            _deviceName ?? string.Empty,
                            _deviceName ?? "vivo / iQOO TWS",
                            _vivoCapability.IsKnownModel ? _vivoCapability.ModelName : null,
                            firmware,
                            null));
                        ApplicationLog.Current?.Info("Vivo", $"遥测首次建立设备身份，固件版本：{firmware}。");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            ApplicationLog.Current?.Debug("Vivo", $"遥测 JSON 解析失败：{ex.Message}，原始={jsonStr}。");
        }
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
        // 仅当 0x0300 握手成功（通道确认为活的 GAIA 通道）时才打印"已启用主动上报"，
        // 避免死通道上半死不活却误报成功、误导排查。
        if (_handshakeOk)
            ApplicationLog.Current?.Info("Vivo", "已发送 vivo 注册通知握手，启用耳机主动上报。");
        else
            ApplicationLog.Current?.Info("Vivo", "vivo 注册通知握手未确认（0x0300 握手未成功），主动上报可能不生效。");
    }
    // ---- dszsu: 统一通知处理器管理 ----
    // 注册持久协议订阅，使设备主动状态帧无需等待轮询即可更新业务快照。
    private void InstallNotificationHandlers(ConnectionLink link)
    {
        DisposeNotificationSubscriptions();
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportBattery,
            frame => ApplyBattery(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.AckNoiseMode,
            frame => ApplyNoiseAck(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportNoiseMode,
            frame => ApplyNoise(frame.Payload.Span)));
        // 0x010C 系（set_anc_mode）：仅改 ancModeConfig 配置项，不切当前出声模式，故仅作诊断订阅，路由到 ApplyAnc。
        // 真正切「当前出声模式」的回包帧（0x8130 ack / 0x8230 主动上报）已在上方订阅到 ApplyNoise。
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.SetAncMode,   frame => ApplyAnc(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.AckAncMode,   frame => ApplyAnc(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.ReportAncMode, frame => ApplyAnc(frame.Payload.Span)));
        _notificationSubscriptions.Add(link.Router.Subscribe(
            VivoConstants.QueryAncMode, frame => ApplyAnc(frame.Payload.Span)));
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
        => _notificationSubscriptions.DisposeAll();
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
        State.SetBattery(
            left.HasValue ? new BatteryLevel(left.Value, (charging & 1) != 0) : null,
            right.HasValue ? new BatteryLevel(right.Value, (charging & 2) != 0) : null,
            caseP.HasValue ? new BatteryLevel(caseP.Value, (charging & 4) != 0) : null);
        ApplicationLog.Current?.Debug(
            "Vivo",
            $"电量状态已更新：left={left?.ToString() ?? "-"}，right={right?.ToString() ?? "-"}，case={caseP?.ToString() ?? "-"}，charging=0x{charging:X2}。");
    }
    private void ApplyNoise(ReadOnlySpan<byte> payload)
    {
        // 官方回包格式（receiveNoiseModelState，对应 0x8230 主动上报 / 查询响应）：
        //   · 长度>=3：[状态][mode][reduceModel][transparent?] → mode=payload[1]，reduceModel=payload[2]
        //   · 长度 1~2：防御性分支，mode=payload[0]（SET ack 已独立路由到 ApplyNoiseAck，不在此处理）
        byte mode;
        byte reduceModel;
        if (payload.Length >= 2)
        {
            // 官方 App 回包（0x8230 主动上报 / 查询响应）：payload[0]=状态字节(0)，payload[1]=模式字节，
            // payload[2]=降噪档位(reduceModel，长度>=3 时)。与 SET 的 ACK(0x8130, mode 在 payload[0]) 不同。
            mode = payload[1];
            reduceModel = payload.Length >= 3 ? payload[2] : (byte)0;
        }
        else if (payload.Length == 1)
        {
            mode = payload[0];
            reduceModel = 0;
        }
        else
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略非法噪声控制回包：{Convert.ToHexString(payload)}。");
            return;
        }
        _noiseMode = mode;
        _reduceModel = reduceModel;
        var mapped = MapFromVivoMode(mode);
        // 缓存设备回读的每模式 reduceModel，SET 时原样回传（不同型号默认档位可能不同）。
        if (mapped is NoiseMode.Off or NoiseMode.NoiseCancellation or NoiseMode.Transparency)
            _reduceModelByMode[mapped] = reduceModel;
        ApplicationLog.Current?.Debug("Vivo",
            $"噪声控制更新：mode=0x{mode:X2}({mapped})，reduceModel=0x{reduceModel:X2}。");
        State.SetNoise(new NoiseSnapshot(mapped, null));
    }
    // SET 噪声模式帧(0x0130)的 ACK 回显：直接回显 SET 载荷（v4 格式下为 [mode, 0x03, 0x01]），mode 在首位。
    // 与主动上报(0x8230，[状态][mode][reduceModel][transparent?]) 解析位置不同，故单独处理，避免 3 字节 ACK 被误解析为 mode=payload[1]。
    private void ApplyNoiseAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1)
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略非法噪声控制 ACK 回包：{FormatBytes(payload)}。");
            return;
        }
        // ACK 回显 SET 帧，mode 在 payload[0]（v4 下 [mode, 0x03, 0x01]）。
        var mode = payload[0];
        var mapped = MapFromVivoMode(mode);
        ApplicationLog.Current?.Debug("Vivo",
            $"噪声控制 ACK：mode=0x{mode:X2}({mapped})，payload={FormatBytes(payload)}。");
        State.SetNoise(new NoiseSnapshot(mapped, null));
    }
    // 当前生效降噪模式解析（set_anc_mode 0x010C 系回包）。
    // APK receiveAncStateACK 取 bArr[1] 为模式字节（m20910F(bArr[1])），长度>=2 时 mode=payload[1]；
    // 个别固件单字节回包时 mode=payload[0]。不包含 reduceModel（降噪档位由 0x0131 循环集合承载）。
    private void ApplyAnc(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            ApplicationLog.Current?.Debug("Vivo", "忽略非法 ANC 生效模式回包：空。");
            return;
        }
        // 0x010C(set_anc_mode) 诊断系使用经典约定（APK 实锤）：mode 0=关闭 / 1=降噪 / 2=通透，与 0x0130 系字节相反，故单独映射。
        var mode = payload.Length >= 2 ? payload[1] : payload[0];
        var mapped = MapFromAncMode(mode);
        _noiseMode = mode;
        ApplicationLog.Current?.Debug("Vivo",
            $"ANC 生效模式更新：mode=0x{mode:X2}({mapped})，payload={Convert.ToHexString(payload)}。");
        State.SetNoise(new NoiseSnapshot(mapped, null));
    }
    // 0x010C(set_anc_mode) 诊断系使用经典约定（APK 实锤）：0=关闭 / 1=降噪 / 2=通透。
    private static NoiseMode MapFromAncMode(byte vivoMode) => vivoMode switch
    {
        0 => NoiseMode.Off,
        1 => NoiseMode.NoiseCancellation,
        2 => NoiseMode.Transparency,
        _ => NoiseMode.Unknown
    };
    // 官方回包第二个字节为低延迟游戏模式开关。
    private bool ApplyGameMode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[1] > 1)
        {
            ApplicationLog.Current?.Debug("Vivo", $"忽略无效低延迟游戏模式回包：{Convert.ToHexString(payload)}。");
            return false;
        }
        State.SetGame(new GameSnapshot(payload[1] == 1, null));
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
        State.SetSpatialAudio(new SpatialAudioSnapshot(mode));
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
                State.SetEqualizer(new EqualizerSnapshot(reportedPresetId, null));
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
        State.SetEqualizer(new EqualizerSnapshot(presetId, knownPreset ? presetKey : null));
        ApplicationLog.Current?.Debug(
            "Vivo",
            knownPreset
                ? $"内置音效状态已更新：reportedId={reportedPresetId}，id={presetId}，preset={presetKey}。"
                : $"设备返回当前型号不支持的音效：reportedId={reportedPresetId}，id={presetId}，不在内置列表显示。");
        return true;
    }
    private static string FormatBytes(ReadOnlySpan<byte> bytes)
        => bytes.Length == 0 ? "(空)" : BitConverter.ToString(bytes.ToArray());
    private byte? MapToVivoMode(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => _noiseMap.Off,
        NoiseMode.NoiseCancellation => _noiseMap.NoiseCancellation,
        NoiseMode.Transparency => _noiseMap.Transparency,
        _ => null
    };
    // set_noise_mode(0x0130) 系：mode 字节取当前型号映射（_noiseMap，canonical 0=NC/1=Off/2=Trans）。
    private NoiseMode MapFromVivoMode(byte vivoMode)
    {
        if (vivoMode == _noiseMap.NoiseCancellation) return NoiseMode.NoiseCancellation;
        if (vivoMode == _noiseMap.Off) return NoiseMode.Off;
        if (vivoMode == _noiseMap.Transparency) return NoiseMode.Transparency;
        return NoiseMode.Unknown;
    }
    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
        => StateChanged?.Invoke(this, snapshot);
    private BrandPresentation BuildPresentation()
    {
        // 降噪三档：key 必须与 DeviceProfileLoader.AncLabel 的识别集一致
        // （"Off"/"NC"/"Transparency"），否则会被兜底成"降噪"，导致三个按钮同名。
        IReadOnlyList<NoiseOptionModel> noiseOptions =
        [
            new("Off", NoiseMode.Off, _noiseMap.Off, []),
            new("NC", NoiseMode.NoiseCancellation, _noiseMap.NoiseCancellation, []),
            new("Transparency", NoiseMode.Transparency, _noiseMap.Transparency, []),
        ];
        // 可见控件由 vivo 能力白名单（EarbudFeatures.FeatureID）按型号决定，而非硬编码；
        // 开发期未知型号乐观显示，便于真机测试命令实现。
        var visibleControls = new HashSet<string>(VivoFeatureMatrix.ResolveVisibleControls(_deviceName), StringComparer.Ordinal);
        // 当前开关状态（设备轮询/回读得到）；查找耳机为瞬时动作，无持久状态。
        var controlStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (_gameModeEnabled is { } gameOn)
            controlStates["game-mode"] = gameOn;
        if (_spatialSoundEnabled is { } spatialOn)
            controlStates["spatial-sound"] = spatialOn;
        if (_wearDetectionEnabled is { } wearOn)
            controlStates["wear-detection"] = wearOn;
        if (_dualDeviceEnabled is { } dualOn)
            controlStates["dual-device"] = dualOn;
        // 测试期默认可操作；确认不支持的型号由白名单隐藏控件后此字典自然不含该键。
        var controlEnabledStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var key in visibleControls)
            controlEnabledStates[key] = true;
        // dszsu: 按 capability 补充可见控件和状态
        if (_vivoCapability.SupportsLowLatencyGaming)
        {
            visibleControls.Add("game-mode");
            controlEnabledStates["game-mode"] = true;
            var gameEnabled = State.Snapshot().Game.IsEnabled;
            if (gameEnabled.HasValue)
                controlStates["game-mode"] = gameEnabled.Value;
        }
        if (_vivoCapability.SupportsAudioEffect && _audioEffectVerified)
        {
            visibleControls.Add("equalizer");
            controlEnabledStates["equalizer"] = true;
        }
        // 听力保护：基于 DeviceModels.json 的 hearing_protection 能力精确显隐（49 机型支持），
        // 不再依赖 VivoFeatureMatrix 的手维护数字白名单（该矩阵未覆盖此功能）。
        if (_vivoCapability.Model?.HasFeature("hearing_protection") == true)
        {
            visibleControls.Add("hearing-enhancement");
            controlEnabledStates["hearing-enhancement"] = true;
            if (_hearingProtectionEnabled is { } hpOn)
                controlStates["hearing-enhancement"] = hpOn;
        }
        // 佩戴检测：命令已实现且为 TWS 通用基础功能，DeviceModels.json 无独立声明键；
        // 对所有型号乐观显示，设备不支持时由运行期超时探测（_runtimeUnsupported）隐藏对应控件。
        visibleControls.Add("wear-detection");
        controlEnabledStates["wear-detection"] = true;
        var currentNoiseKey = State.Snapshot().Noise.Mode switch
        {
            NoiseMode.Off => "Off",
            NoiseMode.NoiseCancellation => "NC",
            NoiseMode.Transparency => "Transparency",
            _ => "Off"
        };
        return new BrandPresentation(
            _vivoCapability.IsKnownModel ? _vivoCapability.ModelName : _deviceName ?? "vivo / iQOO TWS",
            _vivoCapability.IsKnownModel,
            _vivoCapability.SupportsSpatialAudio,
            false,                                   // SupportsCustomEqualizer
            _vivoCapability.SupportsNoiseCancellation, // SupportsNoiseCancellation
            IsFeatureLive(VivoFeatureMatrix.DualConnection), // CanManageMultiDevice
            [],                                      // CustomEqFrequencies
            BrandPresentation.DefaultCustomEqMinimumGain,
            BrandPresentation.DefaultCustomEqMaximumGain,
            _audioEffectVerified ? _vivoCapability.AudioEffectPresetKeys : [],
            visibleControls,
            controlStates,
            controlEnabledStates,
            noiseOptions,
            currentNoiseKey);
    }
    // ---- 地址格式化/解析工具 ----
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
        ApplicationLog.Current?.Debug("Vivo", $"型号命中官方目录：device={identificationName ?? ""}，model={match.Model}。");
    }
}
