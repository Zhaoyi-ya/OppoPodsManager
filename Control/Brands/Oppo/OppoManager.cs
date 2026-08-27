using OppoPodsManager.Control.Brands.Oppo.Features;
using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Assets.Oplus;
using System.Collections.Generic;
using OppoPodsManager.Control.Subsystems.Gestures;
using OppoPodsManager.Control.Subsystems.Equalizers;
using OppoPodsManager.Control.Core;
using OppoPodsManager.Control.Core.Features;
using OppoPodsManager.Communication.Abstractions;
namespace OppoPodsManager.Control.Brands.Oppo;
// 管理单个 OPPO 耳机会话：识别型号、注册通知、维护状态并执行受能力约束的读写。
public sealed class OppoManager : BrandManagerBase, IBrandManager
{
    // 聚合所有业务状态，向前端发布不可变快照（由基类 BrandManagerBase 提供 State）。
    // 从本地型号库和设备运行时响应合并能力集。
    private readonly CapabilityLoader _capabilityLoader;
    private readonly CapabilityReader _capabilityReader;
    // 通过版本号隔离断开后的异步通知和轮询结果。
    private long _sessionVersion;
    private Notifier? _notifier;
    private Battery? _battery;
    private WearStatus? _wearStatus;
    private NoiseCancellation? _noiseCancellation;
    private GameMode? _gameMode;
    private SpatialAudio? _spatialAudio;
    private FeatureSwitches? _featureSwitches;
    private MultiDevice? _multiDevice;
    private GameSound? _gameSound;
    private IEqualizerProfile _equalizerProfile = NullEqualizerProfile.Instance;
    private BassEngineState? _bassEngineState;
    private readonly OppoGestureProfile _gestureProfile = new();
    // 长按「切换噪声控制」的循环模式集合（按 控制源+耳 分键）。后端编码尚未实现，仅作 UI 勾选的内存态；
    // 未记录时默认全勾（与 vivo 出厂 NOISE_ALL 全场景循环基线一致）。
    private readonly Dictionary<(GestureSource Source, EarSide Ear), IReadOnlyList<NoiseMode>> _longPressCycleSets = new();
    // 官方 App 长按面板的可勾选模式顺序：降噪 / 自适应 / 通透 / 关闭。
    private static readonly (NoiseMode Mode, string Key)[] LongPressCycleModes =
    {
        (NoiseMode.NoiseCancellation, "Anc_ModeNoiseCancellation"),
        (NoiseMode.Smart, "Anc_ModeAdaptive"),
        (NoiseMode.Transparency, "Anc_ModeTransparency"),
        (NoiseMode.Off, "Anc_ModeOff"),
    };
    // OPPO 触控（KeyFunction）当前值：每次成功 SET 后回填，供 GestureEntries 显示；GET 回读亦写入此处。
    // 键含控制源(Source)，使主触控区与柄的同一 (耳,手势) 互不覆盖。
    private readonly Dictionary<(GestureSource Source, EarSide Ear, TapKind Kind), GestureActionKind> _currentGestures = new();
    // GET 0x0108 返回的当前触控整表（帧列表）；null 表示尚未读取。
    private List<OppoGestureProfile.KeyFunctionFrame>? _keyFunctionFrames;
    // 控制通知缺失时的轻量回读循环。
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private DateTimeOffset _lastBatteryRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastWearRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastNoiseRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastEqualizerRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSpatialAudioRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFeatureStateRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastMultiDeviceRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCustomEqualizerRefreshUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGameSoundRefreshUtc = DateTimeOffset.MinValue;
    // 串行化 EQ 主动上报、用户写入和列表回读，避免旧列表覆盖新状态。
    private readonly SemaphoreSlim _customEqualizerOperationGate = new(1, 1);
    // 保存本次连接按产品白名单和能力位图得到的基础能力，避免探针收敛结果反向污染下一轮查询。
    private DeviceCapability _baseCapability = DeviceCapability.Unknown;
    private bool _featureProbeCompleted;
    // 使用共享官方型号目录和默认 Melody 协议能力表创建会话管理器。
    public OppoManager(ModelCatalog? modelCatalog = null, CommandCapabilityMap? commandMap = null)
    {
        _capabilityLoader = new CapabilityLoader(modelCatalog ?? DeviceModelData.LoadCatalog());
        _capabilityReader = new CapabilityReader(commandMap ?? CommandCapabilityMap.MelodyV16);
        State.Changed += PublishState;
    }
    public event EventHandler<BusinessSnapshot>? StateChanged;
    public BusinessSnapshot Snapshot => State.Snapshot();
    public DeviceCapability Capability { get; private set; } = DeviceCapability.Unknown;
    // 根据当前能力和最新功能状态生成窗口可直接消费的展示快照。
    public BrandPresentation Presentation
    {
        get
        {
            var featureStates = State.Snapshot().FeatureStates;
            return new BrandPresentation(
                Capability.ModelName,
                Capability.IsKnownModel,
                Capability.SupportsSpatialAudio,
                Capability.SupportsCustomEqualizer,
                Capability.SupportsNoiseCancellation,
                CanManageMultiDevice,
                // 与 main 分支 ResolveCustomEqFreqs 对齐：JSON 声明了 customEqualizer 但未提供
                // customEqFrequency 的型号（如 Enco Free4）回退到标准 6 频段。
                ResolveCustomEqFrequencies(),
                CustomEqualizerMinimumGain,
                CustomEqualizerMaximumGain,
                Capability.EqualizerPresets,
                FeatureSwitches.ResolveVisibleControls(Capability, featureStates),
                FeatureSwitches.ResolveControlStates(featureStates),
                FeatureSwitches.ResolveControlEnabledStates(Capability, featureStates, State.Snapshot().Game),
                NoiseCancellation.BuildOptions(Capability),
                NoiseCancellation.GetKey(State.Snapshot().Noise.SmartLevel ?? State.Snapshot().Noise.Mode));
        }
    }
    // 判断当前型号是否具备多设备策略管理所需的任一协议能力。
    public bool CanManageMultiDevice
        => Capability.SupportsCommand(CommandId.MultiDevicePriority)
            || Capability.SupportsCommand(CommandId.OperateMultiDevice);
    // 返回当前品牌实现使用的自定义 EQ 编辑范围。
    public sbyte CustomEqualizerMinimumGain => CustomEqualizer.DefaultMinimumGain;
    public sbyte CustomEqualizerMaximumGain => CustomEqualizer.DefaultMaximumGain;
    // 与 main 分支 ResolveCustomEqFreqs 对齐：JSON 声明了 customEqualizer 但未提供
    // customEqFrequency 的型号（如 Enco Free4）回退到标准 6 频段。
    private IReadOnlyList<ushort> ResolveCustomEqFrequencies()
        => Capability.ResolvedCustomEqFrequencies
            .Select(value => (ushort)Math.Clamp(value, 0, ushort.MaxValue))
            .ToArray();
    // 暴露 OPPO EQ 协议档案，供 UI 通过统一接口消费，不感知具体命令字与负载格式。
    public IEqualizerProfile EqualizerProfile => _equalizerProfile;
    // 由控制层校验自定义 EQ 名称，界面只负责收集文本。
    public bool IsValidCustomEqualizerName(string name) => _equalizerProfile.IsValidCustomEqualizerName(name);
    // 由控制层按当前型号白名单构造自定义 EQ 条目。
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(
        byte id,
        string name,
        IReadOnlyList<double> gains)
        => _equalizerProfile.CreateCustomEqualizerEntry(id, name, gains);
    // 由控制层把设备条目的协议频段对齐到当前型号白名单。
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
        => _equalizerProfile.AlignCustomEqualizerGains(entry);
    // 以用户指定型号重新解析本机会话能力，不重新连接也不改变设备实际产品标识。
    public void SetManualModel(string? modelName)
    {
        var snapshot = State.Snapshot();
        if (snapshot.Identity is null)
            return;
        _baseCapability = _capabilityLoader.Load(
            snapshot.Identity.ProductId,
            snapshot.DeviceName,
            Capability.SupportedCommands,
            modelName);
        Capability = _featureProbeCompleted
            ? FeatureSwitches.RefineCapability(_baseCapability, snapshot.FeatureStates)
            : _baseCapability;
        State.SetModelName(Capability.IsKnownModel ? Capability.ModelName : null);
    }
    // 由窗口可见性决定是否执行交互功能的补偿轮询。
    public void SetInteractivePolling(bool enabled)
    {
        ApplicationLog.Current?.Info("Polling", $"设备管理器设置交互轮询：enabled={enabled}。");
        InteractivePolling = enabled;
    }
    public Task<bool> SetWearDetectionAsync(bool enabled, CancellationToken cancellationToken)
        => SetFeatureAsync(FeatureSwitches.WearDetection, enabled, cancellationToken);
    public Task<bool> SetVoiceEnhancementAsync(bool enabled, CancellationToken cancellationToken)
        => SetFeatureAsync(FeatureSwitches.VoiceEnhancement, enabled, cancellationToken);
    public Task<bool> SetHearingEnhancementAsync(bool enabled, CancellationToken cancellationToken)
        => HearingEnhancement.HasFeatureSwitch(Capability)
            ? SetFeatureAsync(HearingEnhancement.FeatureId, enabled, cancellationToken)
            : Task.FromResult(false);
    public Task<bool> SetDualDeviceAsync(bool enabled, CancellationToken cancellationToken)
        => SetFeatureAsync(FeatureSwitches.DualDevice, enabled, cancellationToken);
    public Task<bool> SetLongBatteryAsync(bool enabled, CancellationToken cancellationToken)
        => SetFeatureAsync(FeatureSwitches.LongBattery, enabled, cancellationToken);
    public Task<bool> SetBassEngineAsync(bool enabled, CancellationToken cancellationToken)
        => SetBassEngineValueAsync(enabled, cancellationToken);
    public Task<bool> SetSpatialSoundAsync(bool enabled, CancellationToken cancellationToken)
        => SetSpatialSoundCoreAsync(enabled, cancellationToken);
    public Task<bool> SetSpineHealthAsync(bool enabled, CancellationToken cancellationToken)
        => SetFeatureAsync(FeatureSwitches.SpineHealth, enabled, cancellationToken);
    public Task<bool> SetGameModeAsync(bool enabled, CancellationToken cancellationToken)
    {
        var featureId = FeatureSwitches.ResolveGameModeFeature(Capability, State.Snapshot().FeatureStates);
        if (featureId is null)
            return Task.FromResult(false);
        return SetFeatureAsync(featureId.Value, enabled, cancellationToken);
    }
    public async Task<bool> SetEqualizerAsync(byte presetId, CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Equalizer.Protocol", $"发送内置 EQ：id={presetId}。");
        if (!CanUseCommand(CommandId.SetEqualizer, CommandId.CurrentEqualizer))
            return false;
        var link = RequireLink();
        if (!await WriteAsync(link, CommandId.SetEqualizer, CommandId.SetEqualizerResponse, _equalizerProfile.EncodeSetPreset(presetId), cancellationToken))
            return false;
        var response = await TryRequestAsync(link, CommandId.CurrentEqualizer, CommandId.CurrentEqualizerResponse, Array.Empty<byte>(), cancellationToken);
        if (response is not null)
            _equalizerProfile.ApplyCurrentPreset(response.Payload.Span);
        var success = State.Snapshot().Equalizer.PresetId == presetId;
        ApplicationLog.Current?.Info("Equalizer.Protocol", $"内置 EQ 完成：id={presetId}，success={success}。");
        return success;
    }
    // 根据业务名称解析内置或设备端 EQ，并统一执行设备切换。
    public Task<bool> SetEqualizerByNameAsync(string presetName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(presetName))
            return Task.FromResult(false);
        var builtinIndex = Capability.EqualizerPresets
            .Select((name, index) => (name, index))
            .FirstOrDefault(value => string.Equals(value.name, presetName, StringComparison.Ordinal));
        if (builtinIndex.name is not null)
        {
            ApplicationLog.Current?.Debug("Equalizer.Protocol", $"按名称切换内置 EQ：name={presetName}，id={builtinIndex.index}。");
            return SetEqualizerAsync((byte)builtinIndex.index, cancellationToken);
        }
        var deviceEntry = State.Snapshot().EqualizerEntries
            .FirstOrDefault(entry => string.Equals(entry.Name, presetName, StringComparison.Ordinal));
        if (deviceEntry is not { Id: > 0 })
        {
            ApplicationLog.Current?.Error("Equalizer.Protocol", $"按名称切换 EQ 失败：未找到条目，name={presetName}。");
            return Task.FromResult(false);
        }
        ApplicationLog.Current?.Debug("Equalizer.Protocol", $"按名称切换设备 EQ：name={presetName}，id={deviceEntry.Id}。");
        return SetEqualizerAsync(deviceEntry.Id, cancellationToken);
    }
    public async Task<bool> SetSpatialAudioAsync(SpatialAudioMode mode, CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.SetSpatialAudio, CommandId.SpatialAudio))
            return false;
        var payload = mode switch
        {
            SpatialAudioMode.Off => new byte[] { 0 },
            SpatialAudioMode.Fixed => new byte[] { 1 },
            SpatialAudioMode.HeadTracking => new byte[] { 2 },
            _ => null
        };
        if (payload is null)
            return false;
        var link = RequireLink();
        if (!await WriteAsync(link, CommandId.SetSpatialAudio, CommandId.SetSpatialAudioResponse, payload, cancellationToken))
            return false;
        var response = await TryRequestAsync(link, CommandId.SpatialAudio, CommandId.SpatialAudioResponse, Array.Empty<byte>(), cancellationToken);
        if (response is not null)
            _spatialAudio?.Apply(response.Payload.Span);
        return State.Snapshot().SpatialAudio.Mode == mode;
    }
    // 根据界面稳定键解析并设置空间音频模式，隐藏业务枚举转换细节。
    public Task<bool> SetSpatialAudioByKeyAsync(string modeKey, CancellationToken cancellationToken)
        => SetSpatialAudioAsync(SpatialAudio.ParseMode(modeKey), cancellationToken);
    public async Task<bool> SetNoiseCancellationAsync(NoiseMode mode, CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Noise.Protocol", $"请求设置降噪：mode={mode}。");
        if (!CanUseCommand(CommandId.SetNoiseCancellation, CommandId.NoiseCancellation))
        {
            ApplicationLog.Current?.Error("Noise.Protocol", $"设置降噪被拒绝：能力不支持，mode={mode}。");
            return false;
        }
        var protocolIndex = Capability.NoiseModes
            .Where(entry => entry.Value == mode)
            .Select(entry => (byte?)entry.Key)
            .FirstOrDefault();
        if (protocolIndex is null)
        {
            ApplicationLog.Current?.Error("Noise.Protocol", $"设置降噪被拒绝：没有找到协议索引，mode={mode}。");
            return false;
        }
        var success = await SetNoiseCancellationProtocolAsync(protocolIndex.Value, cancellationToken);
        ApplicationLog.Current?.Info("Noise.Protocol", $"设置降噪完成：mode={mode}，protocolIndex={protocolIndex.Value}，success={success}。");
        return success;
    }
    // 根据界面模式键解析型号分组中的真实协议索引，再执行降噪切换。
    public Task<bool> SetNoiseCancellationByKeyAsync(string modeKey, CancellationToken cancellationToken)
    {
        if (!NoiseCancellation.TryParseKey(modeKey, out var mode))
        {
            ApplicationLog.Current?.Error("Noise.Protocol", $"模式键无法解析：key={modeKey}。");
            return Task.FromResult(false);
        }
        foreach (var group in Capability.NoiseGroups)
        {
            var child = group.Children.FirstOrDefault(option => option.Mode == mode);
            if (child is not null)
            {
                ApplicationLog.Current?.Debug("Noise.Protocol", $"按模式键切换子模式：key={modeKey}，protocolIndex={child.ProtocolIndex}。");
                return SetNoiseCancellationProtocolAsync(child.ProtocolIndex, cancellationToken);
            }
        }
        var direct = Capability.NoiseModes.FirstOrDefault(entry => entry.Value == mode);
        if (direct.Key != 0 || direct.Value == NoiseMode.Off)
        {
            ApplicationLog.Current?.Debug("Noise.Protocol", $"按模式键切换叶子模式：key={modeKey}，protocolIndex={direct.Key}。");
            return SetNoiseCancellationProtocolAsync(direct.Key, cancellationToken);
        }
        ApplicationLog.Current?.Error("Noise.Protocol", $"型号能力中未找到降噪模式：key={modeKey}，mode={mode}。");
        return Task.FromResult(false);
    }
    public async Task<bool> SetNoiseCancellationProtocolAsync(byte protocolIndex, CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.SetNoiseCancellation, CommandId.NoiseCancellation))
        {
            ApplicationLog.Current?.Error("Noise.Protocol", $"设置降噪协议被拒绝：能力不支持，protocolIndex={protocolIndex}。");
            return false;
        }
        ApplicationLog.Current?.Info("Feature", $"发送降噪协议模式：protocolIndex={protocolIndex}。");
        var link = RequireLink();
        await link.SendAsync(CommandId.SetNoiseCancellation, BuildNoisePayload(protocolIndex), cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        var response = await TryRequestAsync(
            link,
            CommandId.NoiseCancellation,
            CommandId.NoiseCancellationResponse,
            new byte[] { 1, 1 },
            cancellationToken);
        if (response is null)
        {
            ApplicationLog.Current?.Error("Noise.Protocol", $"设置降噪协议失败：状态回读为空，protocolIndex={protocolIndex}。");
            return false;
        }
        _noiseCancellation?.Apply(response.Payload.Span);
        var success = State.Snapshot().Noise.Mode != NoiseMode.Unknown;
        ApplicationLog.Current?.Info("Noise.Protocol", $"设置降噪协议完成：protocolIndex={protocolIndex}，success={success}。");
        return success;
    }
    public async Task<bool> SetFindDeviceAsync(bool enabled, CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("FindDevice.Protocol", $"发送查找耳机：enabled={enabled}。");
        if (!FindDevice.IsSupported(Capability))
        {
            ApplicationLog.Current?.Error("FindDevice.Protocol", "查找耳机被拒绝：当前型号不支持。");
            return false;
        }
        var success = await WriteAsync(
            RequireLink(),
            FindDevice.SetCommand,
            FindDevice.SetResponse,
            FindDevice.BuildPayload(enabled),
            cancellationToken);
        ApplicationLog.Current?.Info("FindDevice.Protocol", $"查找耳机完成：enabled={enabled}，success={success}。");
        return success;
    }
    public async Task<bool> RefreshMultiDeviceAsync(CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.MultiDeviceInformation))
            return false;
        var response = await TryRequestAsync(
            RequireLink(),
            CommandId.MultiDeviceInformation,
            CommandId.MultiDeviceInformationResponse,
            Array.Empty<byte>(),
            cancellationToken);
        return response is not null && _multiDevice?.Apply(response.Payload.Span) == true;
    }
    public async Task<bool> RefreshMultiDevicePriorityAsync(CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.MultiDevicePriority))
            return false;
        var response = await TryRequestAsync(
            RequireLink(),
            CommandId.MultiDevicePriority,
            CommandId.MultiDevicePriorityResponse,
            Array.Empty<byte>(),
            cancellationToken);
        return response is not null && _multiDevice?.ApplyPriority(response.Payload.Span) == true;
    }
    public async Task<bool> RefreshCustomEqualizersAsync(CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Debug("Equalizer.Protocol", "请求刷新自定义 EQ 列表。");
        if (!CanUseCommand(CommandId.EqualizerEntries))
            return false;
        await _customEqualizerOperationGate.WaitAsync(cancellationToken);
        try
        {
            var response = await TryRequestAsync(
                RequireLink(),
                CommandId.EqualizerEntries,
                CommandId.EqualizerEntriesResponse,
                new byte[] { 1, 5 },
                cancellationToken);
            var success = response is not null && _equalizerProfile.ApplyCustomEqualizerEntries(response.Payload.Span);
            ApplicationLog.Current?.Debug("Equalizer.Protocol", $"刷新自定义 EQ 列表完成：success={success}。");
            return success;
        }
        finally
        {
            _customEqualizerOperationGate.Release();
        }
    }
    public Task<bool> PreviewCustomEqualizerAsync(
        EqualizerEntrySnapshot entry,
        CancellationToken cancellationToken)
        => WriteCustomEqualizerAsync(2, entry, cancellationToken);
    public Task<bool> SaveCustomEqualizerAsync(
        EqualizerEntrySnapshot entry,
        CancellationToken cancellationToken)
        => WriteCustomEqualizerAsync(entry.Id == 0 ? (byte)1 : (byte)2, entry, cancellationToken);
    public Task<bool> DeleteCustomEqualizerAsync(
        EqualizerEntrySnapshot entry,
        CancellationToken cancellationToken)
        => WriteCustomEqualizerAsync(3, entry, cancellationToken);
    // 按控制层决定的动作写入自定义 EQ，并在保存后激活设备端条目。
    private async Task<bool> WriteCustomEqualizerAsync(
        byte action,
        EqualizerEntrySnapshot entry,
        CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Equalizer.Protocol", $"写入自定义 EQ：action={action}，id={entry.Id}，name={entry.Name}。");
        if (!CanUseCommand(CommandId.SetEqualizerEntry, CommandId.EqualizerEntries)
            || !HasWhitelistedCustomEqFrequencies(entry)
            || !_equalizerProfile.TryEncodeCustomEqualizerEntry(action, entry, out var payload))
            return false;
        await _customEqualizerOperationGate.WaitAsync(cancellationToken);
        try
        {
            if (!await WriteAsync(
                    RequireLink(),
                    CommandId.SetEqualizerEntry,
                    CommandId.SetEqualizerEntryResponse,
                    payload,
                    cancellationToken))
                return false;
            var response = await TryRequestAsync(
                RequireLink(),
                CommandId.EqualizerEntries,
                CommandId.EqualizerEntriesResponse,
                new byte[] { 1, 5 },
                cancellationToken);
            var success = response is not null && _equalizerProfile.ApplyCustomEqualizerEntries(response.Payload.Span);
            if (success && action is 1 or 2)
            {
                // 保存后使用设备回读出的实际 ID 激活该 EQ，避免前端参与协议状态判断。
                var savedEntry = State.Snapshot().EqualizerEntries
                    .FirstOrDefault(value => string.Equals(value.Name, entry.Name, StringComparison.Ordinal));
                if (savedEntry is not { Id: > 0 })
                {
                    ApplicationLog.Current?.Error("Equalizer.Protocol", $"保存后未找到设备 EQ 条目：name={entry.Name}。");
                    return false;
                }
                ApplicationLog.Current?.Info("Equalizer.Protocol", $"保存后激活 EQ：name={savedEntry.Name}，id={savedEntry.Id}。");
                success = await SetEqualizerAsync(savedEntry.Id, cancellationToken);
            }
            ApplicationLog.Current?.Info("Equalizer.Protocol", $"写入自定义 EQ 完成：action={action}，id={entry.Id}，success={success}。");
            return success;
        }
        finally
        {
            _customEqualizerOperationGate.Release();
        }
    }
    // 只有官方白名单声明的频段数量和顺序才允许写入设备。
    private bool HasWhitelistedCustomEqFrequencies(EqualizerEntrySnapshot entry)
    {
        var frequencies = ResolveCustomEqFrequencies();
        return frequencies.Count > 0
            && entry.Frequencies.Count == frequencies.Count
            && entry.Frequencies.SequenceEqual(frequencies);
    }
    public async Task<bool> RefreshGameSoundAsync(CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.GameSound))
            return false;
        var response = await TryRequestAsync(
            RequireLink(), CommandId.GameSound, CommandId.GameSoundResponse, Array.Empty<byte>(), cancellationToken);
        return response is not null && _gameSound?.Apply(response.Payload.Span) == true;
    }
    public async Task<bool> SetGameSoundAsync(byte type, CancellationToken cancellationToken)
        => await SetGameSoundAsync(type, type != 0, cancellationToken);
    // 使用型号声明的音效类型启停游戏音效，关闭时按 Melody 协议发送类型 0。
    public async Task<bool> SetGameSoundAsync(byte type, bool enabled, CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.SetGameSound, CommandId.GameSound))
            return false;
        if (!await WriteAsync(
                RequireLink(), CommandId.SetGameSound, CommandId.SetGameSoundResponse,
                new byte[] { enabled ? type : (byte)0, 1 }, cancellationToken))
            return false;
        return await RefreshGameSoundAsync(cancellationToken)
            && (State.Snapshot().Game.SoundType is > 0) == enabled;
    }
    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var type = enabled
            ? State.Snapshot().Game.SoundType is > 0 and var currentType
                ? currentType
                : Capability.PreferredGameSoundType ?? 1
            : (byte)0;
        return SetGameSoundEnabledCoreAsync(type, enabled, cancellationToken);
    }
    // ---- 触控手势：品牌无关展示与下发（OPPO 触控表 GET 0x0108 / SET 0x0408，真机抓包确认）----
    public IReadOnlyList<GestureEntry> GestureEntries
    {
        get
        {
            var list = new List<GestureEntry>();
            foreach (var source in _gestureProfile.SupportedSources)
            {
                // 「柄」仅官方名单声明 supportPinch 的型号支持（Capability.SupportsFeature("stem")）。
                // Enco Free4 等无按压交互的型号不渲染柄分组，避免展示无法使用的控制项。
                if (source == GestureSource.Stem && !Capability.SupportsFeature("stem"))
                    continue;
                foreach (var kind in _gestureProfile.GetSupportedGestures(source))
                {
                    foreach (var ear in new[] { EarSide.Left, EarSide.Right })
                    {
                        var options = _gestureProfile.GetActionOptions(kind, ear, source);
                        GestureActionKind current = GestureActionKind.None;
                        if (_keyFunctionFrames is not null
                            && OppoGestureProfile.TryFindSlot(_keyFunctionFrames, ear, source, kind, out var idx))
                        {
                            var fn = _keyFunctionFrames[idx].Function;
                            if (OppoGestureProfile.TryResolveFunction(fn, out var resolved))
                                current = resolved;
                        }
                        _currentGestures[(source, ear, kind)] = current;
                        // 长按(主触控区)对齐官方交互：弹出多选面板勾选噪声循环模式（MultiCheckbox）；
                        // 其余手势仍用下拉（CycleSet）。柄键长按(press 家族)保持原样。
                        var isLongPressPanel = kind == TapKind.LongPress && source == GestureSource.Touch;
                        list.Add(new GestureEntry(source, kind, ear, _gestureProfile.IsGestureConfigurable(kind, source),
                            isLongPressPanel ? LongPressRenderMode.MultiCheckbox : LongPressRenderMode.CycleSet,
                            options, current,
                            isLongPressPanel ? BuildLongPressCycleOptions(source, ear) : null));
                    }
                }
            }
            return list;
        }
    }
    public Task<bool> SetTouchGestureAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken)
        => SetTouchGestureCoreAsync(ear, kind, action, source, cancellationToken);
    // 长按循环集合的内存勾选态：后端协议编码待核对，暂不下发；保存后通知快照刷新，让 UI 回显新勾选。
    public Task<bool> SetLongPressCycleAsync(EarSide ear, GestureSource source, IReadOnlyList<NoiseMode> modes, CancellationToken cancellationToken)
    {
        _longPressCycleSets[(source, ear)] = modes.ToArray();
        State.NotifyChanged();
        return Task.FromResult(true);
    }
    // 组装长按面板的勾选条目：按官方顺序输出四个模式，勾选态取自内存集合（未记录默认全勾）。
    private IReadOnlyList<LongPressCycleOption> BuildLongPressCycleOptions(GestureSource source, EarSide ear)
    {
        var selected = _longPressCycleSets.TryGetValue((source, ear), out var saved)
            ? saved
            : LongPressCycleModes.Select(m => m.Mode).ToArray();
        var selectedSet = new HashSet<NoiseMode>(selected);
        return LongPressCycleModes
            .Select(m => new LongPressCycleOption(m.Mode, m.Key, selectedSet.Contains(m.Mode)))
            .ToArray();
    }
    // OPPO 触控下发：OPPO 以「整表」写入（GET 0x0108 读取当前 → 改一帧 → SET 0x0408 回写整表）。
    // 命令不可用或槽位未定位（映射待核对）时安全跳过，不下发错误命令。
    private async Task<bool> SetTouchGestureCoreAsync(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.SetKeyFunction, CommandId.KeyFunction))
        {
            ApplicationLog.Current?.Debug("Gesture.OPPO", "OPPO 触控命令不可用（命令未配置或设备不支持），跳过下发。");
            return false;
        }
        try
        {
            var link = RequireLink();
            // 1. 读取当前整表（本地无缓存时先 GET）
            if (_keyFunctionFrames is null)
            {
                var get = await TryRequestAsync(link, CommandId.KeyFunction, CommandId.KeyFunctionResponse, Array.Empty<byte>(), cancellationToken);
                if (get is null || !OppoGestureProfile.DecodeTable(get.Payload.ToArray(), out var init))
                    return false;
                _keyFunctionFrames = init;
            }
            // 2. 定位 (耳, 控制源, 手势) 槽位；找不到说明推断映射需核对，安全跳过。
            if (!OppoGestureProfile.TryFindSlot(_keyFunctionFrames, ear, source, kind, out var index))
            {
                ApplicationLog.Current?.Debug("Gesture.OPPO",
                    $"未找到槽位 (ear={ear}, source={source}, kind={kind})，(控制源,手势)→(button,action) 映射待核对，跳过下发。");
                return false;
            }
            if (!OppoGestureProfile.TryEncodeFunction(action, out var function))
                return false;
            // 3. 修改目标帧并整表回写
            var frames = _keyFunctionFrames.ToList();
            var f = frames[index];
            frames[index] = new OppoGestureProfile.KeyFunctionFrame(f.DeviceType, f.Button, f.ButtonAction, function);
            var payload = OppoGestureProfile.EncodeTable(frames);
            if (!await WriteAsync(link, CommandId.SetKeyFunction, CommandId.SetKeyFunctionResponse, payload, cancellationToken))
            {
                // 回退：逆向发现 0x041C 与 0x0408 同族、疑为另一个键功能 SET。
                // 部分固件把真实写入入口放在 0x041C，0x0408 虽被识别但拒绝写入。
                // 仅当 0x0408 被设备拒绝时尝试，不影响原本成功的路径。
                ApplicationLog.Current?.Debug("Gesture.OPPO",
                    "SET 0x0408 被设备拒绝，尝试回退写入入口 0x041C。");
                if (!await WriteAsync(link, CommandId.Unknown041C, CommandId.Unknown041CResponse, payload, cancellationToken))
                    return false;
            }
            _keyFunctionFrames = frames;
            _currentGestures[(source, ear, kind)] = action;
            State.NotifyChanged();
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Gesture.OPPO", $"设置 OPPO 触控手势失败：{exception.Message}", exception);
            return false;
        }
    }
    // 根据当前设备快照和本地隐藏策略生成多设备显示数据。
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => MultiDevicePolicy.BuildDisplayState(State.Snapshot().MultiDevice, hiddenAddresses);
    // 将优先设备选择转换为官方多设备协议操作。
    public Task<bool> SetMultiDevicePriorityAsync(
        bool automatic,
        string? address,
        CancellationToken cancellationToken)
        => OperateMultiDeviceAsync(
            MultiDevicePolicy.GetPriorityOperation(automatic),
            automatic ? null : address,
            cancellationToken);
    // 游戏音效与部分型号的空间音效和均衡器互斥，先关闭冲突功能再写入目标状态。
    private async Task<bool> SetGameSoundEnabledCoreAsync(byte type, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled && Capability.GameSoundBlocksSpatialSound
            && !await SetFeatureAsync(FeatureSwitches.SpatialSound, false, cancellationToken))
            return false;
        if (enabled && Capability.GameSoundBlocksEqualizer && Capability.EqualizerPresets.Count > 0
            && !await SetEqualizerAsync(0, cancellationToken))
            return false;
        return await SetGameSoundAsync(type, enabled, cancellationToken);
    }
    // 开启空间音效前关闭与其互斥的游戏音效。
    private async Task<bool> SetSpatialSoundCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled && Capability.GameSoundBlocksSpatialSound
            && State.Snapshot().Game.SoundType is > 0
            && !await SetGameSoundAsync(0, false, cancellationToken))
            return false;
        return await SetFeatureAsync(FeatureSwitches.SpatialSound, enabled, cancellationToken);
    }
    public async Task<bool> OperateMultiDeviceAsync(
        MultiDeviceOperation operation,
        string? address,
        CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.OperateMultiDevice, CommandId.MultiDeviceInformation)
            || !MultiDevice.TryBuildOperationPayload(operation, address, out var payload))
            return false;
        if (!await WriteAsync(
                RequireLink(),
                CommandId.OperateMultiDevice,
                CommandId.OperateMultiDeviceResponse,
                payload,
                cancellationToken))
            return false;
        return await RefreshMultiDeviceAsync(cancellationToken);
    }
    // 建立完整会话：先识别产品，再读取能力、注册通知并获取初始状态。
    public async Task StartSessionAsync(string deviceName, ConnectionLink link, CancellationToken cancellationToken)
    {
        await DisconnectAsync();
        var sessionVersion = Interlocked.Increment(ref _sessionVersion);
        try
        {
            // 产品标识优先读取；部分设备会延迟回包，不能因此中断完整握手。
            var identityReader = new DeviceInfoManager(State);
            var productResponse = await TryRequestAsync(
                link,
                CommandId.ProductId,
                CommandId.ProductIdResponse,
                Array.Empty<byte>(),
                cancellationToken);
            var productIdApplied = productResponse is not null
                && identityReader.TryApplyProductId(productResponse.Payload.Span, deviceName);
            var dynamicCapability = await TryReadCapabilitiesAsync(link, cancellationToken);
            // 会话握手验证：产品标识与能力位图两轮请求都没有收到过“匹配命令字的应答”，说明该 RFCOMM
            // 通道只是“能建链但不回本协议”——典型为其它品牌设备的裸通道（真机案例：vivo TWS 3e 重新
            // 上线后其裸通道在 0x0100/通知/电量全部超时的情况下仍误“确认成功”；且 vivo 裸通道会发
            // 非 GAIA 噪声字节，仅凭 LastReceiveTicks==0 拦不住）。改用 LastResponseTicks==0（无任何
            // 协议应答）判死通道，抛 ChannelUnusableException 让 Discovery 切换下一品牌。
            if (link.LastResponseTicks == 0)
                throw new ChannelUnusableException(
                    $"OPPO 协议握手未收到任何应答（产品标识/能力位图均无应答），疑似落到非 OPPO 通道：{deviceName}。");
            _baseCapability = _capabilityLoader.Load(
                State.Snapshot().Identity?.ProductId,
                deviceName,
                dynamicCapability.SupportedCommands);
            Capability = _baseCapability;
            _featureProbeCompleted = false;
            if (productIdApplied && productResponse is not null)
                identityReader.TryApplyProductId(productResponse.Payload.Span, deviceName, Capability.ModelName);
            else
                identityReader.ApplyFallbackIdentity(deviceName, Capability.IsKnownModel ? Capability.ModelName : null);
            // 通知优先于轮询，用于及时同步设备侧变化。
            var notifier = new Notifier(
                link,
                link.Router,
                Capability.SupportsCommand(CommandId.RegisterNotifications));
            notifier.NotificationReceived += OnNotificationReceived;
            notifier.EqualizerChanged += OnEqualizerChanged;
            var battery = new Battery(State, notifier);
            var wearStatus = new WearStatus(State, notifier);
            var noiseCancellation = new NoiseCancellation(State, Capability, notifier);
            var gameMode = new GameMode(State, notifier);
            var equalizer = new OppoEqualizerProfile(State, () => Capability);
            var spatialAudio = new SpatialAudio(State);
            var featureSwitches = new FeatureSwitches(State);
            var multiDevice = new MultiDevice(State);
            var gameSound = new GameSound(State);
            Link = link;
            _notifier = notifier;
            _battery = battery;
            _wearStatus = wearStatus;
            _noiseCancellation = noiseCancellation;
            _gameMode = gameMode;
            _equalizerProfile = equalizer;
            _spatialAudio = spatialAudio;
            _featureSwitches = featureSwitches;
            _multiDevice = multiDevice;
            _gameSound = gameSound;
            link.Disconnected += OnLinkDisconnected;
            State.SetConnected(deviceName);
            await InitializeNotificationsAsync(notifier, cancellationToken);
            await RefreshInitialStateAsync(link, battery, wearStatus, noiseCancellation, equalizer, spatialAudio, featureSwitches, cancellationToken);
            if (Capability.SupportsCommand(CommandId.MultiDeviceInformation))
                await RefreshMultiDeviceAsync(cancellationToken);
            if (Capability.SupportsCommand(CommandId.MultiDevicePriority))
                await RefreshMultiDevicePriorityAsync(cancellationToken);
            if (Capability.SupportsCommand(CommandId.EqualizerEntries))
                await RefreshCustomEqualizersAsync(cancellationToken);
            if (Capability.SupportsCommand(CommandId.GameSound))
                await RefreshGameSoundAsync(cancellationToken);
            StartPolling(link, notifier, battery, wearStatus, noiseCancellation, equalizer, spatialAudio);
        }
        catch
        {
            if (sessionVersion == Volatile.Read(ref _sessionVersion))
                await DisconnectAsync();
            throw;
        }
    }
    // 停止轮询、解除通知并清空本次会话的所有状态。
    public async Task DisconnectAsync()
    {
        Interlocked.Increment(ref _sessionVersion);
        await StopPollingAsync();
        _battery?.Dispose();
        _wearStatus?.Dispose();
        _noiseCancellation?.Dispose();
        _gameMode?.Dispose();
        if (_notifier is not null)
        {
            _notifier.NotificationReceived -= OnNotificationReceived;
            _notifier.EqualizerChanged -= OnEqualizerChanged;
        }
        _notifier?.Dispose();
        if (Link is not null)
        {
            Link.Disconnected -= OnLinkDisconnected;
            await Link.DisposeAsync();
        }
        _battery = null;
        _wearStatus = null;
        _noiseCancellation = null;
        _gameMode = null;
        _spatialAudio = null;
        _featureSwitches = null;
        _multiDevice = null;
        _gameSound = null;
        _equalizerProfile = NullEqualizerProfile.Instance;
        _bassEngineState = null;
        _notifier = null;
        Link = null;
        _baseCapability = DeviceCapability.Unknown;
        Capability = DeviceCapability.Unknown;
        _featureProbeCompleted = false;
        State.Reset();
    }
    public ValueTask DisposeAsync()
    {
        State.Changed -= PublishState;
        return new(DisconnectAsync());
    }
    private void PublishState(object? sender, BusinessSnapshot snapshot)
    {
        StateChanged?.Invoke(this, snapshot);
    }
    // 传输中断后异步清理当前会话，避免前端保留已连接的陈旧状态。
    private void OnLinkDisconnected(object? sender, EventArgs args)
    {
        var sessionVersion = Volatile.Read(ref _sessionVersion);
        _ = DisconnectAfterLinkFailureAsync(sessionVersion, sender as ConnectionLink);
    }
    private async Task DisconnectAfterLinkFailureAsync(long sessionVersion, ConnectionLink? link)
    {
        if (link is null || sessionVersion != Volatile.Read(ref _sessionVersion) || !ReferenceEquals(link, Link))
            return;
        try
        {
            await DisconnectAsync();
        }
        catch
        {
        }
    }
    // 多设备通知只表示列表已变化，需回读完整列表和优先级。
    private void OnNotificationReceived(object? sender, NotificationReceived notification)
    {
        ApplicationLog.Current?.Debug("MultiDevice", $"处理设备通知：event={notification.EventId}，bytes={notification.Data.Length}。");
        if (notification.EventId != Notifier.MultiDeviceEvent || Link is null)
            return;
        var sessionVersion = Volatile.Read(ref _sessionVersion);
        _ = RefreshMultiDeviceFromNotificationAsync(sessionVersion);
    }
    // 官方 0x0504 先更新当前预设，再回读 0x8122 获取完整 EQ 条目和选中状态。
    private void OnEqualizerChanged(object? sender, EqualizerChangedReceived notification)
    {
        ApplicationLog.Current?.Info(
            "Equalizer.Protocol",
            $"处理 EQ 变化通知：bytes={notification.Payload.Length}。");
        if (Link is null)
            return;
        _equalizerProfile.ApplyCurrentPreset(notification.Payload.Span);
        var sessionVersion = Volatile.Read(ref _sessionVersion);
        _ = RefreshEqualizerFromNotificationAsync(sessionVersion);
    }
    private async Task RefreshEqualizerFromNotificationAsync(long sessionVersion)
    {
        try
        {
            if (sessionVersion != Volatile.Read(ref _sessionVersion) || Link is null)
                return;
            ApplicationLog.Current?.Debug("Equalizer.Protocol", "EQ 变化通知触发 0x8122 全量回读。");
            await RefreshCustomEqualizersAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Equalizer.Protocol", "EQ 变化通知回读失败。", exception);
        }
    }
    private async Task RefreshMultiDeviceFromNotificationAsync(long sessionVersion)
    {
        try
        {
            if (sessionVersion == Volatile.Read(ref _sessionVersion))
            {
                ApplicationLog.Current?.Debug("MultiDevice", "通知触发多设备列表回读。");
                await RefreshMultiDeviceAsync(CancellationToken.None);
                await RefreshMultiDevicePriorityAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("MultiDevice", "通知触发的多设备回读失败。", exception);
        }
    }
    // 在会话建立后主动读取各项状态，避免等待第一条通知。
    private async Task RefreshInitialStateAsync(
        ConnectionLink link,
        Battery battery,
        WearStatus wearStatus,
        NoiseCancellation noiseCancellation,
        IEqualizerProfile equalizer,
        SpatialAudio spatialAudio,
        FeatureSwitches featureSwitches,
        CancellationToken cancellationToken)
    {
        ApplicationLog.Current?.Info("Session", "开始读取设备初始状态。");
        var batteryResponse = await TryRequestAsync(link, CommandId.Battery, CommandId.BatteryResponse, Array.Empty<byte>(), cancellationToken);
        ApplicationLog.Current?.Debug("Session", $"初始电量查询：success={batteryResponse is not null}。");
        if (batteryResponse is not null)
            battery.Apply(batteryResponse.Payload.Span);
        if (Capability.SupportsCommand(CommandId.WearStatus))
        {
            var response = await TryRequestAsync(link, CommandId.WearStatus, CommandId.WearStatusResponse, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始佩戴状态查询：success={response is not null}。");
            if (response is not null)
                wearStatus.Apply(response.Payload.Span);
        }
        var deviceInfo = new DeviceInfoManager(State);
        if (Capability.SupportsCommand(CommandId.FirmwareVersion))
        {
            var response = await TryRequestAsync(link, CommandId.FirmwareVersion, CommandId.FirmwareVersionResponse, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始固件查询：success={response is not null}。");
            if (response is not null)
                deviceInfo.ApplyFirmware(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.Codec))
        {
            var response = await TryRequestAsync(link, CommandId.Codec, CommandId.CodecResponse, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始编解码器查询：success={response is not null}。");
            if (response is not null)
                deviceInfo.ApplyCodec(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.NoiseCancellation))
        {
            var response = await TryRequestAsync(link, CommandId.NoiseCancellation, CommandId.NoiseCancellationResponse, new byte[] { 0x01, 0x01 }, cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始降噪查询：success={response is not null}。");
            if (response is not null)
                noiseCancellation.Apply(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.CurrentEqualizer))
        {
            var response = await TryRequestAsync(link, CommandId.CurrentEqualizer, CommandId.CurrentEqualizerResponse, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始 EQ 查询：success={response is not null}。");
            if (response is not null)
                equalizer.ApplyCurrentPreset(response.Payload.Span);
        }
        if (BassEngine.IsSupported(Capability)
            && Capability.SupportsCommand(BassEngine.QueryCommand))
        {
            var response = await TryRequestAsync(
                link,
                BassEngine.QueryCommand,
                BassEngine.QueryResponse,
                Array.Empty<byte>(),
                cancellationToken);
            if (response is not null && BassEngine.TryParse(response.Payload.Span, out var bassState))
            {
                _bassEngineState = bassState;
                ApplicationLog.Current?.Debug(
                    "BassEngine.Protocol",
                    $"初始低音引擎状态：min={bassState.Minimum}，max={bassState.Maximum}，current={bassState.Current}。");
            }
        }
        if (Capability.SupportsCommand(CommandId.SpatialAudio))
        {
            var response = await TryRequestAsync(link, CommandId.SpatialAudio, CommandId.SpatialAudioResponse, Array.Empty<byte>(), cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始空间音频查询：success={response is not null}。");
            if (response is not null)
                spatialAudio.Apply(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.SetFeature))
        {
            var response = await TryRequestAsync(
                link,
                CommandId.FeatureStates,
                CommandId.FeatureStatesResponse,
                FeatureSwitches.BuildQuery(_baseCapability),
                cancellationToken);
            ApplicationLog.Current?.Debug("Session", $"初始功能状态查询：success={response is not null}。");
            if (response is not null)
            {
                featureSwitches.Apply(response.Payload.Span);
                _featureProbeCompleted = true;
                Capability = FeatureSwitches.RefineCapability(_baseCapability, State.Snapshot().FeatureStates);
            }
        }
        // 触控表（KeyFunction）初始回读：GET 0x0108，解析为整表供 GestureEntries 显示与后续 SET 使用。
        if (Capability.SupportsCommand(CommandId.KeyFunction))
        {
            var gestureResp = await TryRequestAsync(link, CommandId.KeyFunction, CommandId.KeyFunctionResponse, Array.Empty<byte>(), cancellationToken);
            if (gestureResp is not null && OppoGestureProfile.DecodeTable(gestureResp.Payload.ToArray(), out var gestureFrames))
            {
                _keyFunctionFrames = gestureFrames;
                ApplicationLog.Current?.Debug("Gesture.OPPO", $"初始触控表读取完成：frames={gestureFrames.Count}。");
            }
        }
#if DEBUG
        // 真机命令面探测改为后台执行，避免阻塞“已连接”状态发布与初始信息回读。
        // 依据 OPPO Enco Free4 真机日志：未命名 GET 多数 50ms 内响应，但 0x011E/0x011F
        // 等设备通过推送帧（0x1E00/0x1F00）而非请求-响应通道返回，在连接关键路径上层层
        // 等待 3 秒超时（每轮 Probe 因此多耗约 6 秒，且连接期间执行两轮）。改为后台 + 短超时
        // 后，连接不再被拖慢；探测结果仍在 DEBUG 日志中产出。仅 Debug 构建生效。
        _ = RunFeatureProbeAsync(link, cancellationToken);
#endif
        ApplicationLog.Current?.Info("Session", "设备初始状态读取完成。");
    }
#if DEBUG
    // 后台探测未命名 GET 命令，与 RefreshInitialStateAsync 解耦，确保连接不上报延迟。
    // 使用较短超时（800ms）：根据 OPPO Enco Free4 真机日志，未命名命令绝大多数在 50ms 内
    // 响应；少数（如 0x011E/0x011F）通过推送帧 0x1E00/0x1F00 返回，在请求-响应通道永远等不到，
    // 用短超时即可快速跳过，避免 3 秒×N 的浪费。全部为只读 GET，不写入任何设备状态。
    private async Task RunFeatureProbeAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            var knownGet = new HashSet<ushort>
            {
                0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0105, 0x0106, 0x0109, 0x010B, 0x010C,
                0x010D, 0x010F, 0x0112, 0x0114, 0x0115, 0x0122, 0x0124, 0x012A, 0x012B, 0x0132,
                // 以下命令通过推送帧（0x1E00/0x1F00）而非请求-响应通道返回，真机日志证实在请求-响应
                // 通道永远等不到响应；探测它们只会白白等待，故直接跳过。
                0x011E, 0x011F
            };
            var candidates = new List<ushort>();
            foreach (var c in Capability.SupportedCommands)
            {
                if (c >= 0x0100 && c <= 0x01FF && !knownGet.Contains(c))
                    candidates.Add(c);
            }
            ApplicationLog.Current?.Info("Probe", $"开始后台探测未命名 GET 命令：count={candidates.Count}。");
            foreach (var cmd in candidates)
            {
                var respCmd = (ushort)(cmd | 0x8000);
                var resp = await TryRequestAsync(link, cmd, respCmd, Array.Empty<byte>(), cancellationToken, TimeSpan.FromMilliseconds(800));
                if (resp is null)
                {
                    ApplicationLog.Current?.Debug("Probe", $"  0x{cmd:X4} -> 无响应/超时。");
                    continue;
                }
                var bytes = resp.Payload.ToArray();
                if (cmd == CommandId.KeyFunction && OppoGestureProfile.DecodeTable(bytes, out var gFrames))
                {
                    ApplicationLog.Current?.Info("Probe", $"  0x{cmd:X4} (KeyFunction) -> frames={gFrames.Count}");
                    for (int i = 0; i < gFrames.Count; i++)
                    {
                        var fr = gFrames[i];
                        OppoGestureProfile.DecodeFrame(fr, out var ear, out var source, out var kind, out var act);
                        ApplicationLog.Current?.Debug("Probe",
                            $"    [{i}] dt=0x{fr.DeviceType:X2} btn=0x{fr.Button:X2} act=0x{fr.ButtonAction:X2} fn=0x{fr.Function:X2}" +
                            $" => {(ear?.ToString() ?? "?")}/{(source?.ToString() ?? "?")}/{(kind?.ToString() ?? "?")}/{(act?.ToString() ?? "?")}");
                    }
                }
                else
                {
                    var hex = Convert.ToHexString(bytes);
                    var looksGesture = bytes.Length >= 4 && bytes.Length % 4 == 0;
                    var tag = looksGesture ? " [疑似手势表 len%4==0]" : "";
                    ApplicationLog.Current?.Debug("Probe", $"  0x{cmd:X4} -> len={bytes.Length}, payload={hex}{tag}");
                }
            }
            ApplicationLog.Current?.Info("Probe", "未命名 GET 命令探测完成。");
        }
        catch (Exception probeEx)
        {
            ApplicationLog.Current?.Error("Probe", $"探测过程异常（已忽略）：{probeEx.Message}");
        }
    }
#endif
    // 通知注册失败不阻断连接，后续由轻量回读保证基本状态可用。
    private static async Task InitializeNotificationsAsync(Notifier notifier, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await notifier.InitializeAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Error("Notifier", "通知初始化超时，继续使用轮询兜底。");
        }
        catch (InvalidOperationException exception)
        {
            ApplicationLog.Current?.Error("Notifier", "通知初始化不受支持，继续使用轮询兜底。", exception);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Error("Notifier", "通知初始化失败，继续使用轮询兜底。", exception);
        }
    }
    // 初始化各类别回读时间并启动两秒节拍器。
    private void StartPolling(
        ConnectionLink link,
        Notifier notifier,
        Battery battery,
        WearStatus wearStatus,
        NoiseCancellation noiseCancellation,
        IEqualizerProfile equalizer,
        SpatialAudio spatialAudio)
    {
        ApplicationLog.Current?.Info("Polling", $"启动轻量轮询：interactive={InteractivePolling}，interval=2s。");
        var now = DateTimeOffset.UtcNow;
        _lastBatteryRefreshUtc = now;
        _lastWearRefreshUtc = now;
        _lastNoiseRefreshUtc = now;
        _lastEqualizerRefreshUtc = now;
        _lastSpatialAudioRefreshUtc = now;
        _lastFeatureStateRefreshUtc = now;
        _lastMultiDeviceRefreshUtc = now;
        _lastCustomEqualizerRefreshUtc = now;
        _lastGameSoundRefreshUtc = now;
        _pollCancellation = new CancellationTokenSource();
        _pollTask = RunPollingAsync(
            link,
            notifier,
            battery,
            wearStatus,
            noiseCancellation,
            equalizer,
            spatialAudio,
            _pollCancellation.Token);
    }
    // 取消并等待正在运行的回读任务，避免链接释放后继续访问。
    private async Task StopPollingAsync()
    {
        var cancellation = Interlocked.Exchange(ref _pollCancellation, null);
        var task = Interlocked.Exchange(ref _pollTask, null);
        if (cancellation is null)
            return;
        ApplicationLog.Current?.Info("Polling", "停止轻量轮询。");
        cancellation.Cancel();
        try
        {
            if (task is not null)
                await task;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
    // 无界面时仅维护电量兜底；界面可见时再补偿交互状态。
    private async Task RunPollingAsync(
        ConnectionLink link,
        Notifier notifier,
        Battery battery,
        WearStatus wearStatus,
        NoiseCancellation noiseCancellation,
        IEqualizerProfile equalizer,
        SpatialAudio spatialAudio,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        ApplicationLog.Current?.Debug("Polling", $"轮询循环开始：interactive={InteractivePolling}。");
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            ApplicationLog.Current?.Debug("Polling", $"轮询 tick：interactive={InteractivePolling}。");
            await RefreshBatteryFallbackAsync(link, notifier, battery, now, cancellationToken);
            if (!InteractivePolling)
                continue;
            await RefreshInteractiveFallbacksAsync(
                link,
                notifier,
                wearStatus,
                noiseCancellation,
                equalizer,
                spatialAudio,
                now,
                cancellationToken);
            await RefreshExtendedInteractiveFallbacksAsync(now, cancellationToken);
        }
    }
    // 按低频间隔补偿功能开关、多设备和自定义音效状态。
    private async Task RefreshExtendedInteractiveFallbacksAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (Link is null)
            return;
        if (Capability.SupportsCommand(CommandId.SetFeature)
            && now - _lastFeatureStateRefreshUtc >= TimeSpan.FromSeconds(15))
        {
            _lastFeatureStateRefreshUtc = now;
            await RefreshFeatureStatesAsync(Link, cancellationToken);
        }
        if (Capability.SupportsCommand(CommandId.MultiDeviceInformation)
            && now - _lastMultiDeviceRefreshUtc >= TimeSpan.FromSeconds(30))
        {
            _lastMultiDeviceRefreshUtc = now;
            await RefreshMultiDeviceAsync(cancellationToken);
            await RefreshMultiDevicePriorityAsync(cancellationToken);
        }
        if (Capability.SupportsCommand(CommandId.EqualizerEntries)
            && now - _lastCustomEqualizerRefreshUtc >= TimeSpan.FromMinutes(2))
        {
            _lastCustomEqualizerRefreshUtc = now;
            await RefreshCustomEqualizersAsync(cancellationToken);
        }
        if (Capability.SupportsCommand(CommandId.GameSound)
            && now - _lastGameSoundRefreshUtc >= TimeSpan.FromMinutes(2))
        {
            _lastGameSoundRefreshUtc = now;
            await RefreshGameSoundAsync(cancellationToken);
        }
    }
    // 电量通知长期缺失时每十秒进行一次轻量回读。
    private async Task RefreshBatteryFallbackAsync(
        ConnectionLink link,
        Notifier notifier,
        Battery battery,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!ShouldRefresh(notifier, Notifier.BatteryEvent, TimeSpan.FromSeconds(10), _lastBatteryRefreshUtc, now))
            return;
        ApplicationLog.Current?.Debug("Polling", "执行电量兜底回读。");
        _lastBatteryRefreshUtc = now;
        var response = await TryRequestAsync(link, CommandId.Battery, CommandId.BatteryResponse, Array.Empty<byte>(), cancellationToken);
        if (response is not null)
            battery.Apply(response.Payload.Span);
    }
    // 只在主窗口打开时补偿佩戴、降噪、均衡器和空间音频状态。
    private async Task RefreshInteractiveFallbacksAsync(
        ConnectionLink link,
        Notifier notifier,
        WearStatus wearStatus,
        NoiseCancellation noiseCancellation,
        IEqualizerProfile equalizer,
        SpatialAudio spatialAudio,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Capability.SupportsCommand(CommandId.WearStatus)
            && ShouldRefresh(notifier, Notifier.WearEvent, TimeSpan.FromSeconds(15), _lastWearRefreshUtc, now))
        {
            ApplicationLog.Current?.Debug("Polling", "执行佩戴状态兜底回读。");
            _lastWearRefreshUtc = now;
            var response = await TryRequestAsync(link, CommandId.WearStatus, CommandId.WearStatusResponse, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                wearStatus.Apply(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.NoiseCancellation)
            && ShouldRefresh(notifier, Notifier.NoiseCancellationEvent, TimeSpan.FromSeconds(8), _lastNoiseRefreshUtc, now))
        {
            ApplicationLog.Current?.Debug("Polling", "执行降噪状态兜底回读。");
            _lastNoiseRefreshUtc = now;
            var response = await TryRequestAsync(link, CommandId.NoiseCancellation, CommandId.NoiseCancellationResponse, new byte[] { 0x01, 0x01 }, cancellationToken);
            if (response is not null)
                noiseCancellation.Apply(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.CurrentEqualizer)
            && now - _lastEqualizerRefreshUtc >= TimeSpan.FromMinutes(2))
        {
            ApplicationLog.Current?.Debug("Polling", "执行 EQ 状态兜底回读。");
            _lastEqualizerRefreshUtc = now;
            var response = await TryRequestAsync(link, CommandId.CurrentEqualizer, CommandId.CurrentEqualizerResponse, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                equalizer.ApplyCurrentPreset(response.Payload.Span);
        }
        if (Capability.SupportsCommand(CommandId.SpatialAudio)
            && now - _lastSpatialAudioRefreshUtc >= TimeSpan.FromMinutes(2))
        {
            ApplicationLog.Current?.Debug("Polling", "执行空间音频状态兜底回读。");
            _lastSpatialAudioRefreshUtc = now;
            var response = await TryRequestAsync(link, CommandId.SpatialAudio, CommandId.SpatialAudioResponse, Array.Empty<byte>(), cancellationToken);
            if (response is not null)
                spatialAudio.Apply(response.Payload.Span);
        }
    }
    // 同时满足时间间隔和近期未收到通知时才允许回读。
    private static bool ShouldRefresh(
        Notifier notifier,
        byte eventId,
        TimeSpan interval,
        DateTimeOffset lastRefresh,
        DateTimeOffset now)
        => now - lastRefresh >= interval && !notifier.HasFreshEvent(eventId, interval, now);
    // 写入通用功能开关后立即回读，确认设备已接受目标值。
    private async Task<bool> SetFeatureAsync(byte featureId, bool enabled, CancellationToken cancellationToken)
    {
        if (!CanUseCommand(CommandId.SetFeature) || !State.Snapshot().FeatureStates.Values.ContainsKey(featureId))
            return false;
        var link = RequireLink();
        if (!await WriteAsync(
                link,
                CommandId.SetFeature,
                CommandId.SetFeatureResponse,
                FeatureSwitches.BuildPayload(featureId, enabled),
                cancellationToken))
            return false;
        await RefreshFeatureStatesAsync(link, cancellationToken);
        return State.Snapshot().FeatureStates.TryGetValue(featureId, out var current) && current == enabled;
    }
    // 使用官方低音引擎三元组命令设置最小值或最大值，避免把它误发成 0x0403。
    private async Task<bool> SetBassEngineValueAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!BassEngine.IsSupported(Capability)
            || !CanUseCommand(BassEngine.SetCommand, BassEngine.QueryCommand))
            return false;
        var current = _bassEngineState ?? new BassEngineState(0, 100, 0);
        var target = enabled ? current.Maximum : current.Minimum;
        var value = current with { Current = target };
        ApplicationLog.Current?.Info(
            "BassEngine.Protocol",
            $"设置低音引擎：min={value.Minimum}，max={value.Maximum}，current={value.Current}。");
        if (!await WriteAsync(
                RequireLink(),
                BassEngine.SetCommand,
                BassEngine.SetResponse,
                BassEngine.BuildValuePayload(value),
                cancellationToken))
            return false;
        var response = await TryRequestAsync(
            RequireLink(),
            BassEngine.QueryCommand,
            BassEngine.QueryResponse,
            Array.Empty<byte>(),
            cancellationToken);
        if (response is null || !BassEngine.TryParse(response.Payload.Span, out var actual))
            return false;
        _bassEngineState = actual;
        var state = State.Snapshot().FeatureStates.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        state[BassEngine.FeatureId] = actual.Current != actual.Minimum;
        State.SetFeatureStates(new FeatureStateSnapshot(state));
        return actual.Current == target;
    }
    private async Task RefreshFeatureStatesAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        if (_featureSwitches is null || !CanUseCommand(CommandId.SetFeature))
            return;
        var response = await TryRequestAsync(
            link,
            CommandId.FeatureStates,
            CommandId.FeatureStatesResponse,
            FeatureSwitches.BuildQuery(_baseCapability),
            cancellationToken);
        if (response is not null)
        {
            _featureSwitches.Apply(response.Payload.Span);
            _featureProbeCompleted = true;
            Capability = FeatureSwitches.RefineCapability(_baseCapability, State.Snapshot().FeatureStates);
        }
    }
    private bool CanUseCommand(ushort writeCommand, ushort? readCommand = null)
        => Link is not null
            && Capability.SupportsCommand(writeCommand)
            && (readCommand is null || Capability.SupportsCommand(readCommand.Value));
    // 写命令委托给共享的 CommandSender（统一三秒超时与异常归一化）。
    private static Task<bool> WriteAsync(
        ConnectionLink link,
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
        => CommandSender.WriteAsync(link, command, responseCommand, payload, cancellationToken);
    // 将能力表中的降噪位索引编码为设备要求的位图负载。
    private static byte[] BuildNoisePayload(byte protocolIndex)
    {
        var bytes = new byte[3 + protocolIndex / 8];
        bytes[0] = 1;
        bytes[1] = 1;
        bytes[2 + protocolIndex / 8] = (byte)(1 << (protocolIndex % 8));
        return bytes;
    }
    // 读取命令委托给共享的 CommandSender（统一两秒超时与异常归一化）。
    private static Task<ProtocolFrame?> TryRequestAsync(
        ConnectionLink link,
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
        => CommandSender.RequestAsync(link, command, responseCommand, payload, cancellationToken, timeout);
    // 能力位图缺失时保留型号库能力，后续通知和轻量读取仍可继续工作。
    private async Task<CapabilityBitmap> TryReadCapabilitiesAsync(ConnectionLink link, CancellationToken cancellationToken)
    {
        try
        {
            return await _capabilityReader.ReadAsync(link, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Error("Capability", "读取 0x0100 能力位图超时，保留官方常驻命令。 ");
            return _capabilityReader.Empty;
        }
        catch (TimeoutException)
        {
            ApplicationLog.Current?.Error("Capability", "读取 0x0100 能力位图失败：请求超时，保留官方常驻命令。 ");
            return _capabilityReader.Empty;
        }
        catch (InvalidOperationException)
        {
            ApplicationLog.Current?.Error("Capability", "读取 0x0100 能力位图失败：当前链路不支持，保留官方常驻命令。 ");
            return _capabilityReader.Empty;
        }
    }
}
