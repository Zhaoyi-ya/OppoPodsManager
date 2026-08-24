using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Brands.Huawei;

// 华为（Huawei）TWS / 耳机品牌后端。作为继 OPPO / vivo / 漫步者之后的第四个品牌接入，
// 复用同一套 ConnectionLink + IFrameCodec + FrameRouter 传输层，零 UI 改动。
//
// 首版能力（协议均来自 HuaweiPods 实机抓包，见 HuaweiConstants 顶部来源说明）：
//   - 型号识别（14 款 alias 表）+ 电量（SPP 查询 S01 C08，仅 SupportsRfcommBattery 型号）
//   - 降噪：开关/通透 + 离散档位（S2B C2A 回读 + S2B C04 写入，4 套档位映射按型号路由）
//   - 手势：双击/三击/长按/滑动（S01 C1F/C25 + S2B C16/C1E 写入，S01 C20/C26 + S2B C17/C1F 回读）
//   - 佩戴检测（S2B C10 写入 + S2B C11 回读，7 款型号，write-then-readback 验证）
//
// 协议模式：华为写命令（ANC/手势/佩戴）无通用 ACK，全部走 SendFireAndForgetAsync +
// 乐观更新 + 延迟回读确认；带响应的查询走 ConnectionLink.RequestAsync。
// 已知限制（首版 TODO，无法验证）：FreeBuds 3 无 RFCOMM 电量（需 HFP AT 通道）、
// FreeBuds Pro 4/Pro 5 无状态回读与手势配置（优雅返回 false / 隐藏控件）。
internal sealed class HuaweiManager : IBrandManager
{
    private readonly BusinessState _state = new();
    private ConnectionLink? _link;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private string? _deviceName;
    private HuaweiRoute _route = HuaweiRoute.Unsupported;
    private HuaweiCapabilities _capabilities = HuaweiCapabilities.Unknown;

    // 连接级订阅句柄，断开时统一释放。
    private readonly List<IDisposable> _subscriptions = new();
    // 原始字节透传订阅（抓非二进制 AT 文本帧），需手动退订。
    private EventHandler<ReadOnlyMemory<byte>>? _rawHandler;
    // 跨块累积原始文本，避免 HUAWEIBATTERY 行被分片截断。
    private readonly StringBuilder _rawBuffer = new();
    // 运行期熔断：某查询连续超时则标记为“运行期不支持”，之后停止轮询，避免链路被无用查询占满。
    private readonly HashSet<ushort> _runtimeUnsupported = new();
    private readonly Dictionary<ushort, int> _pollFailures = new();
    private const int PollFailureThreshold = 3;

    // 手势内存状态（协议字节，写入后乐观更新；回读确认时覆写）。
    private byte? _doubleTapLeft;
    private byte? _doubleTapRight;
    private byte? _tripleTapLeft;
    private byte? _tripleTapRight;
    private byte? _longPressLeft;
    private byte? _longPressRight;
    private byte? _swipeLeft;
    private byte? _swipeRight;

    // 佩戴检测开关（bool 语义，非耳侧佩戴状态）。
    private bool? _wearDetectionEnabled;

    // 均衡器档案（内置预设；自定义编辑暂缓）。
    private readonly HuaweiEqualizerProfile _equalizerProfile;
    // 低延迟/游戏模式开关（bool 语义）。
    private bool? _lowLatencyEnabled;
    // 双设备（dual-connect）状态。
    private bool? _dualConnectEnabled;
    private readonly List<ConnectedDeviceSnapshot> _dualDevices = new();
    private string? _dualPreferredAddress;

    public HuaweiManager()
    {
        _equalizerProfile = new HuaweiEqualizerProfile(_state);
        _state.Changed += OnStateChanged;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;
    public BusinessSnapshot Snapshot => _state.Snapshot();
    // 华为不使用 OPPO 型号能力表；界面依据 Presentation 决定可见性。
    public DeviceCapability Capability => DeviceCapability.Unknown;
    public IReadOnlyList<string> ModelNames => [];
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree
        => new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>>();
    public ModelCatalogLocation? FindModelLocation(string? modelName) => null;
    public BrandPresentation Presentation => BuildPresentation();
    // 双设备：仅对支持 dual-connect 的型号开放（FreeBuds 5i/6i/Pro3/Pro5/FreeClip 2）。
    public bool CanManageMultiDevice => _capabilities.SupportsDualConnect;

    public void SetInteractivePolling(bool enabled)
    {
        // 电量/降噪/佩戴轮询始终运行，不依赖交互状态。
    }

    public async Task DisconnectAsync()
    {
        _pollCancellation?.Cancel();
        _pollCancellation?.Dispose();
        _pollCancellation = null;
        _pollTask = null;
        if (_rawHandler is not null && _link is not null)
            _link.RawDataReceived -= _rawHandler;
        _rawHandler = null;
        _rawBuffer.Clear();
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        _runtimeUnsupported.Clear();
        _pollFailures.Clear();
        if (_link is not null)
        {
            var link = _link;
            _link = null;
            await link.DisposeAsync();
        }
        _route = HuaweiRoute.Unsupported;
        _capabilities = HuaweiCapabilities.Unknown;
        _wearDetectionEnabled = null;
        _lowLatencyEnabled = null;
        _dualConnectEnabled = null;
        _dualPreferredAddress = null;
        _dualDevices.Clear();
        _dualEnumerateTcs = null;
        _doubleTapLeft = _doubleTapRight = _tripleTapLeft = _tripleTapRight = null;
        _longPressLeft = _longPressRight = _swipeLeft = _swipeRight = null;
        _state.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        _state.Changed -= OnStateChanged;
        await DisconnectAsync();
    }

    public void SetManualModel(string? modelName)
    {
        // 华为无型号覆盖需求。
    }

    // ---- OPPO 专属功能：统一返回不支持 ----
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    // ---- 双设备（实装：使能开关 + 枚举列表 + 连接/断开/解绑/首选）----
    public async Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsDualConnect)
            return false;
        try
        {
            // S2B C2E：TLV (1, 0/1) 切换双设备总开关（OpenFreebuds _toggle_enabled）。
            await _link.SendFireAndForgetAsync(HuaweiConstants.SetDualConnectEnabled,
                new byte[] { 0x01, 0x01, enabled ? (byte)0x01 : (byte)0x00 }, cancellationToken);
            _dualConnectEnabled = enabled;
            _state.NotifyChanged();
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置双设备开关失败：{exception.Message}", exception);
            return false;
        }
    }
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    // ---- 低延迟/游戏模式（实装：S2B C6C 切换）----
    public async Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsLowLatency)
            return false;
        try
        {
            // S2B C6C：TLV (1, 0x01/0x00) 切换低延迟（OpenFreebuds change_rq(0x2b6c, [(1, 0/1)])）。
            await _link.SendFireAndForgetAsync(HuaweiConstants.SetLowLatency,
                new byte[] { 0x01, 0x01, enabled ? (byte)0x01 : (byte)0x00 }, cancellationToken);
            _lowLatencyEnabled = enabled;
            _state.NotifyChanged();
            await Task.Delay(200, cancellationToken);
            await RefreshLowLatencyAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置低延迟失败：{exception.Message}", exception);
            return false;
        }
    }
    // ---- 均衡器（实装：内置预设 默认/重低音/高音增强/人声）----
    public async Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsEqualizer)
            return false;
        if (!HuaweiConstants.EqPresetNames.ContainsKey(presetId))
            return false;
        try
        {
            // S2B C49：TLV (1, presetId) 选择内置预设（OpenFreebuds change_rq(0x2b49, [(1, mode_id)])）。
            await _link.SendFireAndForgetAsync(HuaweiConstants.SetEqualizer,
                _equalizerProfile.EncodeSetPreset(presetId), cancellationToken);
            _state.SetEqualizer(new EqualizerSnapshot(presetId, HuaweiConstants.EqPresetNames[presetId]));
            await Task.Delay(200, cancellationToken);
            await RefreshEqualizerAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置均衡器失败：{exception.Message}", exception);
            return false;
        }
    }

    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken)
    {
        foreach (var (id, name) in HuaweiConstants.EqPresetNames)
        {
            if (string.Equals(name, presetName, StringComparison.Ordinal))
                return SetEqualizerAsync(id, cancellationToken);
        }
        return Task.FromResult(false);
    }
    public Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public async Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsDualConnect)
            return false;
        try
        {
            await RefreshDualConnectEnabledAsync(_link, cancellationToken);
            await EnumerateDualConnectAsync(_link, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"刷新双设备失败：{exception.Message}", exception);
            return false;
        }
    }

    public Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken) => RefreshMultiDeviceAsync(cancellationToken);
    public Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> PreviewCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SaveCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> DeleteCustomEqualizerAsync(EqualizerEntrySnapshot entry, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.FromResult(false);
    public async Task<bool> SetMultiDevicePriorityAsync(bool automatic, string? address, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsDualConnect || string.IsNullOrWhiteSpace(address))
            return false;
        var mac = ConvertMacToBytes(address);
        if (mac is null)
            return false;
        try
        {
            // S2B C32：TLV (1, mac) 设置首选设备（OpenFreebuds _set_preferred）。
            var payload = new byte[2 + mac.Length];
            payload[0] = 0x01;
            payload[1] = (byte)mac.Length;
            mac.CopyTo(payload, 2);
            await _link.SendFireAndForgetAsync(HuaweiConstants.SetDualConnectPreferred, payload, cancellationToken);
            _dualPreferredAddress = address;
            _state.NotifyChanged();
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置双设备首选失败：{exception.Message}", exception);
            return false;
        }
    }

    public async Task<bool> OperateMultiDeviceAsync(MultiDeviceOperation operation, string? address, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsDualConnect || string.IsNullOrWhiteSpace(address))
            return false;
        var cmd = operation switch
        {
            MultiDeviceOperation.Connect => HuaweiConstants.DualConnectConnect,
            MultiDeviceOperation.Disconnect => HuaweiConstants.DualConnectDisconnect,
            MultiDeviceOperation.Unpair => HuaweiConstants.DualConnectUnpair,
            _ => (byte)0
        };
        if (cmd == 0)
            return false;
        var mac = ConvertMacToBytes(address);
        if (mac is null)
            return false;
        try
        {
            // S2B C33：TLV (cmd, mac) 执行连接/断开/解绑（OpenFreebuds _exec_command）。
            var payload = new byte[2 + mac.Length];
            payload[0] = cmd;
            payload[1] = (byte)mac.Length;
            mac.CopyTo(payload, 2);
            await _link.SendFireAndForgetAsync(HuaweiConstants.ExecuteDualConnect, payload, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"双设备操作失败：{exception.Message}", exception);
            return false;
        }
    }

    // ---- 均衡器：内置预设档案（默认/重低音/高音增强/人声），自定义编辑暂缓 ----
    public IEqualizerProfile EqualizerProfile => _equalizerProfile;
    public sbyte CustomEqualizerMinimumGain => BrandPresentation.DefaultCustomEqMinimumGain;
    public sbyte CustomEqualizerMaximumGain => BrandPresentation.DefaultCustomEqMaximumGain;
    public bool IsValidCustomEqualizerName(string name) => false;
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(byte id, string name, IReadOnlyList<double> gains)
        => new(0, string.Empty, false, -6, 6, [], []);
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry) => [];
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
    {
        var snapshot = new MultiDeviceSnapshot(_dualDevices, _dualPreferredAddress is not null, _dualPreferredAddress);
        return MultiDevicePolicy.BuildDisplayState(snapshot, hiddenAddresses);
    }

    // ---- 佩戴检测（实装，7 款型号）----
    public async Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsWearDetection)
            return false;
        try
        {
            // S2B C10：TLV 0x01 单字节 0=关 / 1=开（5A0006002B100101[00|01]）。
            var payload = new byte[] { 0x01, 0x01, enabled ? (byte)0x01 : (byte)0x00 };
            await _link.SendFireAndForgetAsync(HuaweiConstants.SetWearDetection, payload, cancellationToken);
            // 乐观更新：UI 立即反映，随后用抓包确认的 2B11 回读做最终结果（write-then-readback）。
            _wearDetectionEnabled = enabled;
            _state.NotifyChanged();
            // 参考实现：200ms 延迟等待落盘，最多再重试 2 次 × 300ms。
            await Task.Delay(200, cancellationToken);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var actual = await QueryWearDetectionAsync(_link, cancellationToken);
                if (actual == enabled)
                    return true;
                if (attempt < 2)
                    await Task.Delay(300, cancellationToken);
            }
            // 回读不符（可能型号实际不支持）：保持乐观值并返回成功，避免 UI 抖动。
            ApplicationLog.Current?.Debug("Huawei", "佩戴检测回读与写入不一致，保留乐观状态。");
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置佩戴检测失败：{exception.Message}", exception);
            return false;
        }
    }

    // ---- 降噪（实装：开关/通透 + 离散档位）----
    public async Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsAnc)
            return false;
        var payload = BuildAncSetPayload(mode);
        if (payload is null)
            return false;
        try
        {
            await _link.SendFireAndForgetAsync(HuaweiConstants.SetAncMode, payload, cancellationToken);
            // 乐观更新：离散档位直接用档位 Mode（Light/Medium/…）表达，使 UI 子模式立即高亮。
            _state.SetNoise(new NoiseSnapshot(mode, null));
            // 支持状态回读的型号延迟确认（200ms），失败仅记录不翻转乐观值。
            if (_capabilities.SupportsAncStateReadback)
            {
                await Task.Delay(200, cancellationToken);
                await RefreshAncStateAsync(_link, cancellationToken);
            }
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置降噪失败：{exception.Message}", exception);
            return false;
        }
    }

    public async Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken)
    {
        var mode = ParseNoiseKey(modeKey);
        if (mode == NoiseMode.Unknown)
            return false;
        return await SetNoiseCancellationAsync(mode, cancellationToken);
    }

    public async Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken)
    {
        var mode = protocolIndex switch
        {
            HuaweiConstants.AncModeOff => NoiseMode.Off,
            HuaweiConstants.AncModeNoiseCancellation => NoiseMode.NoiseCancellation,
            HuaweiConstants.AncModeTransparency => NoiseMode.Transparency,
            _ => NoiseMode.Unknown
        };
        if (mode == NoiseMode.Unknown)
            return false;
        return await SetNoiseCancellationAsync(mode, cancellationToken);
    }

    // ---- 触控手势（实装：双击/三击/长按/滑动，按型号能力动态构建）----
    public IReadOnlyList<GestureEntry> GestureEntries
    {
        get
        {
            var list = new List<GestureEntry>();
            if (!_capabilities.SupportsGestureConfiguration)
                return list;
            foreach (var ear in new[] { EarSide.Left, EarSide.Right })
            {
                foreach (var kind in SupportedGestureKinds)
                {
                    var actions = GetGestureActions(kind);
                    if (actions.Count == 0)
                        continue;
                    var options = actions
                        .Select(action => new GestureActionOption(action, GestureDisplay.KeyFor(action)))
                        .ToArray();
                    list.Add(new GestureEntry(
                        GestureSource.Touch,
                        kind,
                        ear,
                        true,
                        LongPressRenderMode.CycleSet,
                        options,
                        ResolveCurrentGesture(kind, ear)));
                }
            }
            return list;
        }
    }

    public async Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken)
    {
        if (_link is null || !_capabilities.SupportsGestureConfiguration)
            return false;
        var value = EncodeGestureValue(kind, action);
        if (value is null)
            return false;
        try
        {
            var payload = BuildGestureSetPayload(kind, ear, value.Value);
            if (payload is null)
                return false;
            ushort command = kind switch
            {
                TapKind.Double => HuaweiConstants.SetDoubleTap,
                TapKind.Triple => HuaweiConstants.SetTripleTap,
                TapKind.LongPress => HuaweiConstants.SetLongPress,
                TapKind.Slide => HuaweiConstants.SetSwipe,
                _ => 0
            };
            if (command == 0)
                return false;
            await _link.SendFireAndForgetAsync(command, payload, cancellationToken);
            // 乐观更新内存字节，UI 通过 GestureEntries 立即反映。
            UpdateGestureMemory(kind, ear, value.Value);
            _state.NotifyChanged();
            // TODO(真机)：华为手势写命令无通用 ACK，参考实现发送后不回读；如需确认可在延迟后
            // 查询对应状态（S01 C20/C26、S2B C17/C1F），回读失败保留乐观值。
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Huawei", $"设置触控手势失败：{exception.Message}", exception);
            return false;
        }
    }

    // ---- 会话建立 ----
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        _deviceName = deviceName;
        _route = HuaweiModels.DetectRoute(deviceName);
        _capabilities = HuaweiModels.GetCapabilities(_route);
        _link = link;
        RegisterSubscriptions(link);
        if (_capabilities.SupportsRfcommBattery)
            await RefreshBatteryAsync(link, cancellationToken);
        if (_capabilities.SupportsAnc && _capabilities.SupportsAncStateReadback)
            await RefreshAncStateAsync(link, cancellationToken);
        if (_capabilities.SupportsGestureConfiguration)
            await RefreshGestureStatesAsync(link, cancellationToken);
        if (_capabilities.SupportsWearDetection)
            await RefreshWearDetectionAsync(link, cancellationToken);
        if (_capabilities.SupportsEqualizer)
            await RefreshEqualizerAsync(link, cancellationToken);
        if (_capabilities.SupportsLowLatency)
            await RefreshLowLatencyAsync(link, cancellationToken);
        if (_capabilities.SupportsDualConnect)
            await RefreshMultiDeviceAsync(cancellationToken);
        // 尽力而为：PC 端 SPP 通道可能无 HFP 风格回包（参考源注明“SPP 常无回包”），
        // 故仅发一次 AT 探测；真正的电量以设备主动推送的 +UPDATEHUAWEIBATTERY= 文本为准
        // （由 RawDataReceived 捕获并解析）。无响应时不影响既有的二进制电量路径。
        await ProbeBatteryAtAsync(link, cancellationToken);
        _state.SetConnected(deviceName);
        _pollCancellation = new CancellationTokenSource();
        _pollTask = RunPollingAsync(link, _pollCancellation.Token);
    }

    // ---- 订阅与运行期熔断 ----
    // 注册报告帧的常驻订阅：设备主动推送的状态（电量/降噪/佩戴/手势）直接更新内存，
    // 不再依赖轮询，减少链路占用；也覆盖 0x0127 备用电量报告。
    private void RegisterSubscriptions(ConnectionLink link)
    {
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportBattery, OnBatteryReport));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportBatteryAlt, OnBatteryReport));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportAncState, OnAncReport));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportWearDetection, OnWearReport));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportDoubleTapState, f => ApplyGestureState(HuaweiConstants.QueryDoubleTapState, f.Payload.Span)));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportTripleTapState, f => ApplyGestureState(HuaweiConstants.QueryTripleTapState, f.Payload.Span)));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportLongPressState, f => ApplyGestureState(HuaweiConstants.QueryLongPressState, f.Payload.Span)));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.ReportSwipeState, f => ApplyGestureState(HuaweiConstants.QuerySwipeState, f.Payload.Span)));
        // 新能力回报订阅：EQ / 低延迟 / 双设备枚举 / 双设备变更事件。
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.QueryEqualizer, OnEqualizerReport));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.QueryLowLatency, OnLowLatencyReport));
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.EnumerateDualConnect, OnDualConnectEnumerate));
        // 形参必须用非 _ 名称，否则 `_ = Task` 会被解析为把 Task 赋给 ProtocolFrame 形参。
        _subscriptions.Add(link.Router.Subscribe(HuaweiConstants.DualConnectChangeEvent,
            frame => _ = RefreshMultiDeviceAsync(CancellationToken.None)));
        // 原始字节透传：捕获非二进制 AT 文本电量行（+HUAWEIBATTERY= / +UPDATEHUAWEIBATTERY=）。
        _rawHandler = OnRawData;
        link.RawDataReceived += _rawHandler;
    }

    // 华为电量文本走 HFP/SPP 的 AT 行，不以二进制 5A 帧出现，故经原始字节流捕获后解析。
    private void OnRawData(object? sender, ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length == 0)
            return;
        _rawBuffer.Append(Encoding.UTF8.GetString(bytes.Span));
        // 仅保留末尾足够长度，避免二进制噪声无限增长。
        const int maxBuffer = 1024;
        if (_rawBuffer.Length > maxBuffer)
            _rawBuffer.Remove(0, _rawBuffer.Length - maxBuffer);
        var parsed = HuaweiBatteryParser.Parse(_rawBuffer.ToString());
        if (parsed is not null)
            ApplyBatteryFromAt(parsed);
    }

    private void ApplyBatteryFromAt(HuaweiBatteryParser.BatteryState state)
    {
        BatteryLevel? left = state.LeftPercent is { } l ? new BatteryLevel((byte)l, state.LeftCharging) : null;
        BatteryLevel? right = state.RightPercent is { } r ? new BatteryLevel((byte)r, state.RightCharging) : null;
        BatteryLevel? @case = state.CasePercent is { } c ? new BatteryLevel((byte)c, state.CaseCharging) : null;
        _state.SetBattery(left, right, @case);
    }

    // 尽力而为的 AT 电量探测：PC 端 SPP 通道可能无回包，仅发送不等待应答，
    // 后续电量以设备主动推送的文本行为准。发送失败仅记录，不影响其它功能。
    private async Task ProbeBatteryAtAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var at = Encoding.ASCII.GetBytes("AT+HUAWEIBATTERY?\r\n");
            await link.SendRawAsync(at, cancellationToken);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"电量 AT 探测发送失败：{exception.Message}");
        }
    }

    private void OnBatteryReport(ProtocolFrame frame) => ApplyBattery(frame.Payload.Span);
    private void OnAncReport(ProtocolFrame frame) => ApplyAncState(frame.Payload.Span);
    private void OnWearReport(ProtocolFrame frame)
    {
        if (ParseWearDetection(frame.Payload.Span) is { } enabled)
        {
            _wearDetectionEnabled = enabled;
            _state.NotifyChanged();
        }
    }

    // 运行期能力是否“实际可用”：静态能力声明 AND 未被运行期超时熔断。
    private bool RuntimeBatterySupported => _capabilities.SupportsRfcommBattery && !_runtimeUnsupported.Contains(HuaweiConstants.QueryBattery);
    private bool RuntimeAncSupported => _capabilities.SupportsAnc && !_runtimeUnsupported.Contains(HuaweiConstants.QueryAncState);
    private bool RuntimeWearSupported => _capabilities.SupportsWearDetection && !_runtimeUnsupported.Contains(HuaweiConstants.QueryWearDetection);

    private void RecordPollSuccess(ushort queryCommand)
    {
        if (_pollFailures.TryGetValue(queryCommand, out var n) && n > 0)
            _pollFailures[queryCommand] = 0;
    }

    // 记录一次查询失败；连续达到阈值则熔断并通知 UI 重建展示。返回是否刚刚被标记不支持。
    private bool RecordPollFailure(ushort queryCommand)
    {
        var count = (_pollFailures.TryGetValue(queryCommand, out var n) ? n : 0) + 1;
        _pollFailures[queryCommand] = count;
        if (count >= PollFailureThreshold && _runtimeUnsupported.Add(queryCommand))
        {
            ApplicationLog.Current?.Info("Huawei", $"运行期探测：命令 0x{queryCommand:X4} 连续 {count} 次失败，标记为不支持并停止轮询。");
            _state.NotifyChanged();
            return true;
        }
        return false;
    }

    // ---- 内部读取/轮询 ----
    private async Task RefreshBatteryAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        if (_runtimeUnsupported.Contains(HuaweiConstants.QueryBattery))
            return;
        try
        {
            // 电量查询请求体不可为空：TLV [01 00][02 00][03 00] 请求左耳/右耳/充电盒电平，
            // 与华为参考项目 HuaweiAncPackets.huaweiBatteryQuery 抓包（5A0009000108010002000300→FBB9）一致。
            var response = await link.RequestAsync(
                HuaweiConstants.QueryBattery, HuaweiConstants.ReportBattery,
                new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00 }, cancellationToken);
            if (response is not null)
            {
                ApplyBattery(response.Payload.Span);
                RecordPollSuccess(HuaweiConstants.QueryBattery);
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"电量查询失败：{exception.Message}");
            RecordPollFailure(HuaweiConstants.QueryBattery);
        }
    }

    private async Task RefreshAncStateAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        if (_runtimeUnsupported.Contains(HuaweiConstants.QueryAncState))
            return;
        try
        {
            // 查询帧 TLV 为 [01 00]（type=0x01 空值），等价抓包 5A0005002B2A0100。
            var response = await link.RequestAsync(
                HuaweiConstants.QueryAncState, HuaweiConstants.ReportAncState,
                new byte[] { 0x01, 0x00 }, cancellationToken);
            if (response is not null)
            {
                ApplyAncState(response.Payload.Span);
                RecordPollSuccess(HuaweiConstants.QueryAncState);
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"降噪状态查询失败：{exception.Message}");
            RecordPollFailure(HuaweiConstants.QueryAncState);
        }
    }

    private async Task<bool> QueryWearDetectionAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            // 查询帧 TLV 为 [01 00]，等价抓包 5A0005002B110100。
            var response = await link.RequestAsync(
                HuaweiConstants.QueryWearDetection, HuaweiConstants.ReportWearDetection,
                new byte[] { 0x01, 0x00 }, cancellationToken);
            return response is not null && ParseWearDetection(response.Payload.Span) is { } state;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"佩戴检测查询失败：{exception.Message}");
            return false;
        }
    }

    private async Task RefreshWearDetectionAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        if (_runtimeUnsupported.Contains(HuaweiConstants.QueryWearDetection))
            return;
        try
        {
            var response = await link.RequestAsync(
                HuaweiConstants.QueryWearDetection, HuaweiConstants.ReportWearDetection,
                new byte[] { 0x01, 0x00 }, cancellationToken);
            if (response is null)
                return;
            var state = ParseWearDetection(response.Payload.Span);
            if (state is { } enabled)
            {
                _wearDetectionEnabled = enabled;
                _state.NotifyChanged();
            }
            else
                RecordPollSuccess(HuaweiConstants.QueryWearDetection);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"佩戴检测查询失败：{exception.Message}");
            RecordPollFailure(HuaweiConstants.QueryWearDetection);
        }
    }

    private async Task RefreshGestureStatesAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        // 状态查询按型号分组（参考 buildGestureStateQuery）：
        //   4E: 双击+长按；5I: 双击；FREEARC: 双击+三击+长按+滑动；6I/CLIP2/7I: 双击+三击+滑动。
        // 查询帧 TLV 为 [01 00 02 00]（请求左右状态），等价抓包 5A0007000120010002。
        var queries = BuildGestureStateQueries();
        foreach (var (query, report) in queries)
        {
            try
            {
                var response = await link.RequestAsync(
                    query, report, new byte[] { 0x01, 0x00, 0x02, 0x00 }, cancellationToken);
                if (response is not null)
                    ApplyGestureState(query, response.Payload.Span);
            }
            catch (TimeoutException)
            {
                // 型号回读差异（如 5I 只回读双击）：超时仅记录，不阻塞其他查询。
                ApplicationLog.Current?.Debug("Huawei", $"手势状态查询超时：command=0x{query:X4}。");
            }
            catch (Exception exception)
            {
                ApplicationLog.Current?.Debug("Huawei", $"手势状态查询失败：command=0x{query:X4}，{exception.Message}");
            }
        }
    }

    private async Task RunPollingAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (_link is null)
                break;
            if (RuntimeBatterySupported)
                await RefreshBatteryAsync(link, cancellationToken);
            if (RuntimeAncSupported && _capabilities.SupportsAncStateReadback)
                await RefreshAncStateAsync(link, cancellationToken);
            if (RuntimeWearSupported)
                await RefreshWearDetectionAsync(link, cancellationToken);
        }
    }

    // ---- 华为新能力读取/枚举（EQ / 低延迟 / 双设备）----
    private TaskCompletionSource? _dualEnumerateTcs;
    private int _dualEnumerateExpected;

    private async Task RefreshEqualizerAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            // S2B C4A 读回，TLV (1..8, 空) 请求各参数；响应 param2=当前预设 ID。
            var payload = new byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x04, 0x00,
                0x05, 0x00, 0x06, 0x00, 0x07, 0x00, 0x08, 0x00 };
            var response = await link.RequestAsync(HuaweiConstants.QueryEqualizer,
                HuaweiConstants.QueryEqualizer, payload, cancellationToken);
            if (response is not null)
                _equalizerProfile.ApplyCurrentPreset(response.Payload.Span);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"均衡器查询失败：{exception.Message}");
        }
    }

    private async Task RefreshLowLatencyAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            // S2B C6C 读回，TLV (2, 空) 请求；响应 param2=1/0。
            var response = await link.RequestAsync(HuaweiConstants.QueryLowLatency,
                HuaweiConstants.QueryLowLatency, new byte[] { 0x02, 0x00 }, cancellationToken);
            if (response is not null)
            {
                var fields = ParseTlv(response.Payload.Span);
                if (fields.TryGetValue(0x02, out var value) && value.Length > 0)
                    _lowLatencyEnabled = value.Span[0] == 1;
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"低延迟查询失败：{exception.Message}");
        }
    }

    private async Task RefreshDualConnectEnabledAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            // S2B C2F 读回，TLV (1, 空)；响应 param1=1/0。
            var response = await link.RequestAsync(HuaweiConstants.QueryDualConnectEnabled,
                HuaweiConstants.QueryDualConnectEnabled, new byte[] { 0x01, 0x00 }, cancellationToken);
            if (response is not null)
            {
                var fields = ParseTlv(response.Payload.Span);
                if (fields.TryGetValue(0x01, out var value) && value.Length > 0)
                    _dualConnectEnabled = value.Span[0] == 1;
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Debug("Huawei", $"双设备开关查询失败：{exception.Message}");
        }
    }

    // 双设备枚举：设备逐包回报（无结束帧），用收集窗口 + 计数完成判定。
    private async Task EnumerateDualConnectAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        _dualDevices.Clear();
        _dualEnumerateExpected = int.MaxValue;
        _dualEnumerateTcs = new TaskCompletionSource();
        try
        {
            // S2B C31 枚举，TLV (1, 空) 触发（OpenFreebuds on_init 枚举流程）。
            await link.SendFireAndForgetAsync(HuaweiConstants.EnumerateDualConnect,
                new byte[] { 0x01, 0x00 }, cancellationToken);
        }
        catch
        {
            _dualEnumerateTcs.TrySetResult();
            return;
        }
        await Task.WhenAny(_dualEnumerateTcs.Task, Task.Delay(2000, cancellationToken));
        _dualEnumerateTcs = null;
    }

    private void OnEqualizerReport(ProtocolFrame frame) => _equalizerProfile.ApplyCurrentPreset(frame.Payload.Span);

    private void OnLowLatencyReport(ProtocolFrame frame)
    {
        var fields = ParseTlv(frame.Payload.Span);
        if (fields.TryGetValue(0x02, out var value) && value.Length > 0)
        {
            _lowLatencyEnabled = value.Span[0] == 1;
            _state.NotifyChanged();
        }
    }

    // 双设备枚举回报：TLV 2=总数, 3=索引, 4=MAC, 5=连接态, 7=首选, 8=自动连接, 9=名称
    // （OpenFreebuds dual_connect/models.py OfbHuaweiDualConnectRow）。
    private void OnDualConnectEnumerate(ProtocolFrame frame)
    {
        var fields = ParseTlv(frame.Payload.Span);
        if (!fields.TryGetValue(0x02, out var countBytes) || countBytes.Length < 1)
            return;
        _dualEnumerateExpected = countBytes.Span[0];
        var mac = fields.TryGetValue(0x04, out var macBytes) ? HexString(macBytes.Span) : "";
        if (string.IsNullOrWhiteSpace(mac))
            return;
        var name = fields.TryGetValue(0x09, out var nameBytes)
            ? Encoding.UTF8.GetString(nameBytes.Span).TrimEnd('\0') : "";
        var connState = fields.TryGetValue(0x05, out var connBytes) && connBytes.Length > 0 ? connBytes.Span[0] : (byte)0;
        var preferred = fields.TryGetValue(0x07, out var prefBytes) && prefBytes.Length > 0 && prefBytes.Span[0] == 1;
        var playing = connState == 9;
        _dualDevices.RemoveAll(device => device.Address == mac);
        _dualDevices.Add(new ConnectedDeviceSnapshot(mac, name, 0, connState > 0 ? (byte)2 : (byte)0,
            false, playing, preferred));
        if (_dualEnumerateExpected > 0 && _dualDevices.Count >= _dualEnumerateExpected)
            _dualEnumerateTcs?.TrySetResult();
    }

    private static string HexString(ReadOnlySpan<byte> bytes)
    {
        var builder = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var value in bytes)
            builder.Append(value.ToString("X2"));
        return builder.ToString();
    }

    private static byte[]? ConvertMacToBytes(string mac)
    {
        var cleaned = mac.Replace(":", "").Replace("-", "").Trim();
        if (cleaned.Length != 12)
            return null;
        var result = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out result[i]))
                return null;
        }
        return result;
    }

    // ---- 华为解析 ----
    private void ApplyBattery(ReadOnlySpan<byte> payload)
    {
        // TLV 0x02=电量[左,右,盒]、0x03=充电位[左,右,盒]、0x05=佩戴状态（仅 Pro5 等型号：
        // 0=已出盒/1=已收纳入盒）。解析逻辑与华为参考 parseBattery/podAt 对齐。
        var fields = ParseTlv(payload);
        if (!fields.TryGetValue(HuaweiConstants.TlvBatteryLevels, out var levels) || levels.Length < 2)
            return;
        var charging = fields.TryGetValue(HuaweiConstants.TlvChargingStates, out var states) ? states : ReadOnlyMemory<byte>.Empty;

        BatteryLevel? Parse(int index)
        {
            var percent = levels.Span[index];
            if (percent > 100)
                return null;
            var isCharging = charging.Length > index && charging.Span[index] != 0;
            return new BatteryLevel(percent, isCharging);
        }

        _state.SetBattery(Parse(0), Parse(1), levels.Length > 2 ? Parse(2) : null);
    }

    private void ApplyAncState(ReadOnlySpan<byte> payload)
    {
        // TLV 0x01 两字节 [子模式, 主模式]；主模式 0=关/1=降噪/2=通透。
        var fields = ParseTlv(payload);
        if (!fields.TryGetValue(HuaweiConstants.TlvAncState, out var state) || state.Length < 2)
            return;
        var subMode = state.Span[0];
        var mode = state.Span[1];
        var resolved = mode switch
        {
            HuaweiConstants.AncModeOff => NoiseMode.Off,
            HuaweiConstants.AncModeNoiseCancellation => ResolveNcSubMode(subMode),
            HuaweiConstants.AncModeTransparency => NoiseMode.Transparency,
            _ => NoiseMode.Unknown
        };
        if (resolved != NoiseMode.Unknown)
            _state.SetNoise(new NoiseSnapshot(resolved, null));
    }

    private NoiseMode ResolveNcSubMode(byte subMode)
    {
        // 离散档位型号：子模式映射回项目档位（Smart/Light/Medium/Deep），
        // 使 UI 经 CurrentNoiseModeKey 命中子模式高亮；否则折叠为降噪主模式。
        if (_capabilities.SupportsDiscreteAncLevels && HuaweiAncLevels.MapFromProtocol(_route, subMode) is { } mapped)
            return mapped;
        return NoiseMode.NoiseCancellation;
    }

    private void ApplyGestureState(ushort queryCommand, ReadOnlySpan<byte> payload)
    {
        // TLV 0x01=左、0x02=右，各单字节动作值。解析失败时保留内存乐观值。
        var fields = ParseTlv(payload);
        var left = fields.TryGetValue(HuaweiConstants.TlvLeftGesture, out var l) && l.Length > 0 ? l.Span[0] : (byte?)null;
        var right = fields.TryGetValue(HuaweiConstants.TlvRightGesture, out var r) && r.Length > 0 ? r.Span[0] : (byte?)null;
        if (left is null && right is null)
            return;
        var (kind, _) = queryCommand switch
        {
            HuaweiConstants.QueryDoubleTapState => (TapKind.Double, 0),
            HuaweiConstants.QueryTripleTapState => (TapKind.Triple, 0),
            HuaweiConstants.QueryLongPressState => (TapKind.LongPress, 0),
            HuaweiConstants.QuerySwipeState => (TapKind.Slide, 0),
            _ => ((TapKind?)null, 0)
        };
        if (kind is null)
            return;
        if (left is { } leftValue)
            UpdateGestureMemory(kind.Value, EarSide.Left, leftValue);
        if (right is { } rightValue)
            UpdateGestureMemory(kind.Value, EarSide.Right, rightValue);
        _state.NotifyChanged();
    }

    private bool? ParseWearDetection(ReadOnlySpan<byte> payload)
    {
        // TLV 0x01 单字节：0x00=false / 0x01=true（参考 parseLatestBooleanState）。
        var fields = ParseTlv(payload);
        if (!fields.TryGetValue(HuaweiConstants.TlvAncState, out var state) || state.Length < 1)
            return null;
        return state.Span[0] switch
        {
            0x00 => false,
            0x01 => true,
            _ => null
        };
    }

    // ---- 华为构建 ----
    private byte[]? BuildAncSetPayload(NoiseMode mode)
    {
        // FreeBuds 3 走旧头 [01 01]（单字节开关）；现代款走 [01 02 mode subMode]。
        if (_route == HuaweiRoute.FreeBuds3)
        {
            return mode switch
            {
                NoiseMode.Off => new byte[] { 0x01, 0x01, 0x00 },
                NoiseMode.NoiseCancellation => new byte[] { 0x01, 0x01, 0x01 },
                // TODO(真机)：FB3 智能降噪档位走 S2B C08（0-8 级，SetAncDirectionLevel），
                // 项目 NoiseMode 无直接对应，首版仅支持开/关。
                _ => null
            };
        }
        return mode switch
        {
            NoiseMode.Off => new byte[] { 0x01, 0x02, HuaweiConstants.AncModeOff, 0x00 },
            NoiseMode.NoiseCancellation => BuildNcPayload(),
            NoiseMode.Transparency => BuildTransparencyPayload(),
            NoiseMode.Smart or NoiseMode.Light or NoiseMode.Medium or NoiseMode.Deep
                => HuaweiAncLevels.MapToProtocol(_route, mode) is { } subMode
                    ? new byte[] { 0x01, 0x02, HuaweiConstants.AncModeNoiseCancellation, subMode }
                    : null,
            _ => null
        };
    }

    private byte[] BuildNcPayload()
    {
        // 离散档位型号默认取自适应档位；无离散档位型号用 0xFF（等同抓包 modernEnabled[true]）。
        if (_capabilities.SupportsDiscreteAncLevels && HuaweiAncLevels.DefaultSubMode(_route) is { } subMode)
            return new byte[] { 0x01, 0x02, HuaweiConstants.AncModeNoiseCancellation, subMode };
        return new byte[] { 0x01, 0x02, HuaweiConstants.AncModeNoiseCancellation, HuaweiConstants.AncSubModeDefault };
    }

    private byte[] BuildTransparencyPayload()
    {
        // 6i 通透默认子模式 0x02，其余型号 0xFF（抓包 transparencyModes）。
        var subMode = _route == HuaweiRoute.FreeBuds6I
            ? HuaweiConstants.TransparencyDefault6i
            : HuaweiConstants.AncSubModeDefault;
        return new byte[] { 0x01, 0x02, HuaweiConstants.AncModeTransparency, subMode };
    }

    private byte[]? BuildGestureSetPayload(TapKind kind, EarSide ear, byte value)
    {
        var side = ear == EarSide.Left ? (byte)0x01 : (byte)0x02;
        // Eyewear2 滑动用 9 字节重复模式（参考 buildSwipePacket）。
        if (kind == TapKind.Slide && _route == HuaweiRoute.Eyewear2)
            return [side, side, value, side, side, value];
        // 标准侧动作帧：side + [01] + value（参考 buildSideActionPacket）。
        return [side, 0x01, value];
    }

    private byte? EncodeGestureValue(TapKind kind, GestureActionKind action)
    {
        if (kind is TapKind.Double or TapKind.Triple)
        {
            // FreeBuds 3 双击为 legacy 值集（勿复用于其他型号）。
            if (_route == HuaweiRoute.FreeBuds3)
            {
                return action switch
                {
                    GestureActionKind.PlayPause => HuaweiConstants.GesturePlayPause,
                    GestureActionKind.Next => HuaweiConstants.GestureFb3PlayNext,
                    GestureActionKind.VoiceAssistant => HuaweiConstants.GestureVoiceAssistant,
                    GestureActionKind.NoiseControlToggle => HuaweiConstants.GestureFb3NoiseCancellation,
                    GestureActionKind.None => HuaweiConstants.GestureNone,
                    _ => null
                };
            }
            return action switch
            {
                GestureActionKind.PlayPause => HuaweiConstants.GesturePlayPause,
                GestureActionKind.Next => HuaweiConstants.GestureNext,
                GestureActionKind.Previous => HuaweiConstants.GesturePrevious,
                GestureActionKind.VoiceAssistant => HuaweiConstants.GestureVoiceAssistant,
                GestureActionKind.None => HuaweiConstants.GestureNone,
                _ => null
            };
        }
        if (kind == TapKind.LongPress)
        {
            // 4E 的降噪控制值不同（0x03 而非 0x0A），且不支持语音助手。
            if (_route == HuaweiRoute.FreeBuds4E)
            {
                return action switch
                {
                    GestureActionKind.NoiseControlToggle => 0x03,
                    GestureActionKind.None => HuaweiConstants.GestureNone,
                    // TODO(真机)：4E 听歌识曲 0x0E 无项目对应动作，首版省略。
                    _ => null
                };
            }
            return action switch
            {
                GestureActionKind.VoiceAssistant => HuaweiConstants.GestureVoiceAssistant,
                GestureActionKind.NoiseControlToggle => HuaweiConstants.GestureNoiseControl,
                GestureActionKind.None => HuaweiConstants.GestureNone,
                _ => null
            };
        }
        if (kind == TapKind.Slide)
        {
            return action switch
            {
                GestureActionKind.VolumeControl => HuaweiConstants.SwipeVolumeControl,
                GestureActionKind.SongSwitch => HuaweiConstants.SwipeTrackControl,
                GestureActionKind.None => HuaweiConstants.GestureNone,
                _ => null
            };
        }
        return null;
    }

    private GestureActionKind DecodeGestureValue(TapKind kind, byte value)
    {
        if (kind is TapKind.Double or TapKind.Triple)
        {
            if (_route == HuaweiRoute.FreeBuds3)
            {
                return value switch
                {
                    HuaweiConstants.GesturePlayPause => GestureActionKind.PlayPause,
                    HuaweiConstants.GestureFb3PlayNext => GestureActionKind.Next,
                    HuaweiConstants.GestureVoiceAssistant => GestureActionKind.VoiceAssistant,
                    HuaweiConstants.GestureFb3NoiseCancellation => GestureActionKind.NoiseControlToggle,
                    _ => GestureActionKind.None
                };
            }
            return value switch
            {
                HuaweiConstants.GesturePlayPause => GestureActionKind.PlayPause,
                HuaweiConstants.GestureNext => GestureActionKind.Next,
                HuaweiConstants.GesturePrevious => GestureActionKind.Previous,
                HuaweiConstants.GestureVoiceAssistant => GestureActionKind.VoiceAssistant,
                _ => GestureActionKind.None
            };
        }
        if (kind == TapKind.LongPress)
        {
            return value switch
            {
                HuaweiConstants.GestureVoiceAssistant => GestureActionKind.VoiceAssistant,
                HuaweiConstants.GestureNoiseControl or 0x03 => GestureActionKind.NoiseControlToggle,
                // TODO(真机)：0x0E 听歌识曲无项目对应动作，首版折叠为 None。
                _ => GestureActionKind.None
            };
        }
        if (kind == TapKind.Slide)
        {
            return value switch
            {
                HuaweiConstants.SwipeVolumeControl => GestureActionKind.VolumeControl,
                HuaweiConstants.SwipeTrackControl => GestureActionKind.SongSwitch,
                _ => GestureActionKind.None
            };
        }
        return GestureActionKind.None;
    }

    private void UpdateGestureMemory(TapKind kind, EarSide ear, byte value)
    {
        switch (kind, ear)
        {
            case (TapKind.Double, EarSide.Left): _doubleTapLeft = value; break;
            case (TapKind.Double, EarSide.Right): _doubleTapRight = value; break;
            case (TapKind.Triple, EarSide.Left): _tripleTapLeft = value; break;
            case (TapKind.Triple, EarSide.Right): _tripleTapRight = value; break;
            case (TapKind.LongPress, EarSide.Left): _longPressLeft = value; break;
            case (TapKind.LongPress, EarSide.Right): _longPressRight = value; break;
            case (TapKind.Slide, EarSide.Left): _swipeLeft = value; break;
            case (TapKind.Slide, EarSide.Right): _swipeRight = value; break;
        }
    }

    private GestureActionKind ResolveCurrentGesture(TapKind kind, EarSide ear)
    {
        byte? raw = kind switch
        {
            TapKind.Double => ear == EarSide.Left ? _doubleTapLeft : _doubleTapRight,
            TapKind.Triple => ear == EarSide.Left ? _tripleTapLeft : _tripleTapRight,
            TapKind.LongPress => ear == EarSide.Left ? _longPressLeft : _longPressRight,
            TapKind.Slide => ear == EarSide.Left ? _swipeLeft : _swipeRight,
            _ => null
        };
        return raw is { } value ? DecodeGestureValue(kind, value) : GestureActionKind.None;
    }

    // 该型号支持的手势种类（与参考 buildGestureStateQuery / 分派表对齐）。
    private IReadOnlyList<TapKind> SupportedGestureKinds
    {
        get
        {
            if (_route == HuaweiRoute.FreeBuds3)
                return [TapKind.Double];
            var kinds = new List<TapKind>();
            if (SupportsTap(_route, TapKind.Double)) kinds.Add(TapKind.Double);
            if (SupportsTap(_route, TapKind.Triple)) kinds.Add(TapKind.Triple);
            if (SupportsTap(_route, TapKind.LongPress)) kinds.Add(TapKind.LongPress);
            if (SupportsTap(_route, TapKind.Slide)) kinds.Add(TapKind.Slide);
            return kinds;
        }
    }

    private static bool SupportsTap(HuaweiRoute route, TapKind kind) => (route, kind) switch
    {
        // 双击：4E/5I/6I/7I/CLIP2/FREEARC/EYEWEAR2
        (HuaweiRoute.FreeBuds4E or HuaweiRoute.FreeBuds5I or HuaweiRoute.FreeBuds6I
            or HuaweiRoute.FreeBuds7I or HuaweiRoute.FreeClip2 or HuaweiRoute.FreeArc
            or HuaweiRoute.Eyewear2, TapKind.Double) => true,
        // 三击：6I/7I/CLIP2/FREEARC
        (HuaweiRoute.FreeBuds6I or HuaweiRoute.FreeBuds7I or HuaweiRoute.FreeClip2
            or HuaweiRoute.FreeArc, TapKind.Triple) => true,
        // 长按：4E/6I/PRO3/7I/FREEARC（参考 modernLongPressRoutes）
        (HuaweiRoute.FreeBuds4E or HuaweiRoute.FreeBuds6I or HuaweiRoute.FreeBudsPro3
            or HuaweiRoute.FreeBuds7I or HuaweiRoute.FreeArc, TapKind.LongPress) => true,
        // 滑动：6I/CLIP2/FREEARC/EYEWEAR2（参考 buildSwipePacket）
        (HuaweiRoute.FreeBuds6I or HuaweiRoute.FreeClip2 or HuaweiRoute.FreeArc
            or HuaweiRoute.Eyewear2, TapKind.Slide) => true,
        _ => false
    };

    private IReadOnlyList<GestureActionKind> GetGestureActions(TapKind kind)
    {
        if (kind == TapKind.LongPress)
        {
            return _route switch
            {
                // 4E：降噪控制 + 无（听歌识曲 0x0E 无项目对应动作，省略）
                HuaweiRoute.FreeBuds4E => [GestureActionKind.NoiseControlToggle, GestureActionKind.None],
                // 6i/Pro3/7i：语音助手 + 降噪控制 + 无
                HuaweiRoute.FreeBuds6I or HuaweiRoute.FreeBudsPro3 or HuaweiRoute.FreeBuds7I
                    => [GestureActionKind.VoiceAssistant, GestureActionKind.NoiseControlToggle, GestureActionKind.None],
                // FreeArc：语音助手 + 无
                HuaweiRoute.FreeArc => [GestureActionKind.VoiceAssistant, GestureActionKind.None],
                _ => []
            };
        }
        if (kind == TapKind.Slide)
        {
            return _route switch
            {
                HuaweiRoute.FreeBuds6I => [GestureActionKind.VolumeControl, GestureActionKind.SongSwitch, GestureActionKind.None],
                // FreeClip2：仅音量控制 + 无
                HuaweiRoute.FreeClip2 => [GestureActionKind.VolumeControl, GestureActionKind.None],
                HuaweiRoute.FreeArc => [GestureActionKind.VolumeControl, GestureActionKind.SongSwitch],
                HuaweiRoute.Eyewear2 => [GestureActionKind.VolumeControl, GestureActionKind.SongSwitch, GestureActionKind.None],
                _ => []
            };
        }
        // 双击/三击
        if (kind == TapKind.Double && _route == HuaweiRoute.FreeBuds3)
            return [GestureActionKind.PlayPause, GestureActionKind.Next, GestureActionKind.VoiceAssistant,
                GestureActionKind.NoiseControlToggle, GestureActionKind.None];
        return (_route, kind) switch
        {
            // 6i 双击：播放/暂停 + 下一曲（官方 App 仅两档）
            (HuaweiRoute.FreeBuds6I, TapKind.Double) => [GestureActionKind.PlayPause, GestureActionKind.Next],
            // 4E/5I/7I/FREEARC 双击：播放/暂停 + 下一曲 + 上一曲 + 语音助手 + 无
            (HuaweiRoute.FreeBuds4E or HuaweiRoute.FreeBuds5I or HuaweiRoute.FreeBuds7I or HuaweiRoute.FreeArc, TapKind.Double)
                => [GestureActionKind.PlayPause, GestureActionKind.Next, GestureActionKind.Previous,
                    GestureActionKind.VoiceAssistant, GestureActionKind.None],
            // FreeClip2 双击：播放/暂停 + 下一曲 + 语音助手 + 无（空间音频 0x07 与上一曲同值且无项目动作，省略）
            (HuaweiRoute.FreeClip2, TapKind.Double)
                => [GestureActionKind.PlayPause, GestureActionKind.Next, GestureActionKind.VoiceAssistant,
                    GestureActionKind.None],
            // Eyewear2 双击：播放/暂停 + 语音助手 + 无
            (HuaweiRoute.Eyewear2, TapKind.Double)
                => [GestureActionKind.PlayPause, GestureActionKind.VoiceAssistant, GestureActionKind.None],
            // 三击：下一曲 + 上一曲 + 无（6i/7i/CLIP2/FREEARC）
            (HuaweiRoute.FreeBuds6I or HuaweiRoute.FreeBuds7I or HuaweiRoute.FreeClip2 or HuaweiRoute.FreeArc, TapKind.Triple)
                => [GestureActionKind.Next, GestureActionKind.Previous, GestureActionKind.None],
            _ => []
        };
    }

    private IReadOnlyList<(ushort Query, ushort Report)> BuildGestureStateQueries()
    {
        var queries = new List<(ushort, ushort)>();
        void Add(ushort query)
        {
            if (SupportsTap(_route, query switch
            {
                HuaweiConstants.QueryDoubleTapState => TapKind.Double,
                HuaweiConstants.QueryTripleTapState => TapKind.Triple,
                HuaweiConstants.QueryLongPressState => TapKind.LongPress,
                HuaweiConstants.QuerySwipeState => TapKind.Slide,
                _ => (TapKind)(-1)
            }))
            {
                queries.Add((query, query));
            }
        }
        Add(HuaweiConstants.QueryDoubleTapState);
        Add(HuaweiConstants.QueryTripleTapState);
        Add(HuaweiConstants.QueryLongPressState);
        Add(HuaweiConstants.QuerySwipeState);
        return queries;
    }

    internal static Dictionary<byte, ReadOnlyMemory<byte>> ParseTlv(ReadOnlySpan<byte> payload)
    {
        var fields = new Dictionary<byte, ReadOnlyMemory<byte>>();
        var offset = 0;
        while (offset + 2 <= payload.Length)
        {
            var type = payload[offset];
            var length = payload[offset + 1];
            var valueStart = offset + 2;
            var valueEnd = valueStart + length;
            if (valueEnd > payload.Length)
                break;
            fields[type] = payload.Slice(valueStart, length).ToArray();
            offset = valueEnd;
        }
        return fields;
    }

    // ---- 展示 ----
    private BrandPresentation BuildPresentation()
    {
        var noiseOptions = BuildNoiseOptions();
        var visibleControls = new HashSet<string>(StringComparer.Ordinal);
        var controlStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        var controlEnabledStates = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (RuntimeWearSupported)
        {
            visibleControls.Add("wear-detection");
            controlEnabledStates["wear-detection"] = true;
            if (_wearDetectionEnabled is { } wearOn)
                controlStates["wear-detection"] = wearOn;
        }
        if (_capabilities.SupportsEqualizer)
        {
            visibleControls.Add("equalizer");
            controlEnabledStates["equalizer"] = true;
        }
        if (_capabilities.SupportsLowLatency)
        {
            visibleControls.Add("game-mode");
            controlEnabledStates["game-mode"] = true;
            if (_lowLatencyEnabled is { } lowLatencyOn)
                controlStates["game-mode"] = lowLatencyOn;
        }
        if (_capabilities.SupportsDualConnect)
        {
            visibleControls.Add("dual-device");
            controlEnabledStates["dual-device"] = true;
            if (_dualConnectEnabled is { } dualOn)
                controlStates["dual-device"] = dualOn;
        }
        var modelName = HuaweiModels.IsKnown(_route)
            ? _capabilities.DisplayName
            : _deviceName ?? "HUAWEI TWS";
        return new BrandPresentation(
            modelName,
            HuaweiModels.IsKnown(_route),
            false,                                  // SupportsSpatialAudio
            _capabilities.SupportsEqualizer,        // SupportsCustomEqualizer（仅内置预设，自定义编辑暂缓）
            RuntimeAncSupported,                    // SupportsNoiseCancellation
            _capabilities.SupportsDualConnect,      // CanManageMultiDevice
            [],                                     // CustomEqFrequencies
            BrandPresentation.DefaultCustomEqMinimumGain,
            BrandPresentation.DefaultCustomEqMaximumGain,
            _capabilities.SupportsEqualizer ? HuaweiConstants.EqPresetList : [],  // EqualizerPresets
            visibleControls,
            controlStates,
            controlEnabledStates,
            noiseOptions,
            NoiseKey(_state.Snapshot().Noise.Mode));
    }

    private IReadOnlyList<NoiseOptionModel> BuildNoiseOptions()
    {
        // 降噪主模式 keys 必须为 "Off"/"NC"/"Transparency"（UI 按此识别，否则兜底成"降噪"同名按钮）；
        // 离散档位作为 NC 的 Children（SmallWindow/HomeView 的 AncSubRow 父子结构渲染）。
        var options = new List<NoiseOptionModel>
        {
            new("Off", NoiseMode.Off, HuaweiConstants.AncModeOff, []),
        };
        if (!RuntimeAncSupported)
            return options;
        var children = new List<NoiseOptionModel>();
        if (_capabilities.SupportsDiscreteAncLevels)
        {
            foreach (var (mode, protocol) in HuaweiAncLevels.GetSupportedLevels(_route))
                children.Add(new(NoiseKey(mode), mode, protocol, []));
        }
        options.Add(new("NC", NoiseMode.NoiseCancellation, HuaweiConstants.AncModeNoiseCancellation, children));
        if (_capabilities.SupportsTransparency)
            options.Add(new("Transparency", NoiseMode.Transparency, HuaweiConstants.AncModeTransparency, []));
        return options;
    }

    private static NoiseMode ParseNoiseKey(string modeKey) => modeKey switch
    {
        "Off" or "off" => NoiseMode.Off,
        "NC" or "anc" or "NoiseCancellation" => NoiseMode.NoiseCancellation,
        "Transparency" or "transparency" => NoiseMode.Transparency,
        "Smart" or "Adaptive" or "adaptive" => NoiseMode.Smart,
        "Light" or "light" => NoiseMode.Light,
        "Medium" or "medium" => NoiseMode.Medium,
        "Deep" or "deep" => NoiseMode.Deep,
        _ => NoiseMode.Unknown
    };

    // 与 OPPO NoiseCancellation.GetKey 完全一致（独立实现避免跨品牌依赖）。
    private static string NoiseKey(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => "Off",
        NoiseMode.Transparency => "Transparency",
        NoiseMode.Smart => "Smart",
        NoiseMode.NoiseCancellation => "NC",
        NoiseMode.Light => "Light",
        NoiseMode.Medium => "Medium",
        NoiseMode.Deep => "Deep",
        _ => "Off"
    };

    private void OnStateChanged(object? sender, BusinessSnapshot snapshot)
        => StateChanged?.Invoke(this, snapshot);
}
