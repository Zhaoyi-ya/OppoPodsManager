using OppoPodsManager.Control.Oppo.Features;
using OppoPodsManager.Control.Oppo.Managers;
using OppoPodsManager.Control.Oppo.Models;
using OppoPodsManager.Control.Logging;
using OppoPodsManager.Control;
using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Assets.Oplus;

namespace OppoPodsManager.Control.Oppo;

// 管理单个 OPPO 耳机会话：识别型号、注册通知、维护状态并执行受能力约束的读写。
public sealed class OppoManager : IBrandManager
{
    // 聚合所有业务状态，向前端发布不可变快照。
    private readonly BusinessState _state = new();
    // 从本地型号库和设备运行时响应合并能力集。
    private readonly CapabilityLoader _capabilityLoader;
    private readonly CapabilityReader _capabilityReader;
    // 通过版本号隔离断开后的异步通知和轮询结果。
    private long _sessionVersion;
    private ConnectionLink? _link;
    private Notifier? _notifier;
    private Battery? _battery;
    private WearStatus? _wearStatus;
    private NoiseCancellation? _noiseCancellation;
    private GameMode? _gameMode;
    private Equalizer? _equalizer;
    private SpatialAudio? _spatialAudio;
    private FeatureSwitches? _featureSwitches;
    private MultiDevice? _multiDevice;
    private CustomEqualizer? _customEqualizer;
    private GameSound? _gameSound;
    private BassEngineState? _bassEngineState;
    // 控制通知缺失时的轻量回读循环。
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private volatile bool _interactivePolling;
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

    // 使用默认 Melody 协议能力表创建会话管理器。
    public OppoManager(CapabilityLoader? capabilityLoader = null, CommandCapabilityMap? commandMap = null)
    {
        _capabilityLoader = capabilityLoader ?? new CapabilityLoader(DeviceModelData.LoadCatalog());
        _capabilityReader = new CapabilityReader(commandMap ?? CommandCapabilityMap.MelodyV16);
        _state.Changed += PublishState;
    }

    public event EventHandler<BusinessSnapshot>? StateChanged;

    public BusinessSnapshot Snapshot => _state.Snapshot();

    public DeviceCapability Capability { get; private set; } = DeviceCapability.Unknown;

    // 暴露同一份官方型号白名单，避免界面重新维护型号列表。
    public IReadOnlyList<string> ModelNames => _capabilityLoader.Catalog.Models
        .Select(model => model.DisplayName)
        .ToArray();

    // 返回官方型号目录的层级数据，供界面直接展示筛选项。
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> ModelTree
        => _capabilityLoader.Catalog.BrandTree;

    // 将型号目录定位请求交给官方型号库处理。
    public ModelCatalogLocation? FindModelLocation(string? modelName)
        => _capabilityLoader.Catalog.FindLocation(modelName);

    // 根据当前能力和最新功能状态生成窗口可直接消费的展示快照。
    public BrandPresentation Presentation
    {
        get
        {
            var featureStates = _state.Snapshot().FeatureStates;
            return new BrandPresentation(
                Capability.ModelName,
                Capability.IsKnownModel,
                Capability.SupportsSpatialAudio,
                Capability.SupportsCustomEqualizer,
                Capability.SupportsNoiseCancellation,
                CanManageMultiDevice,
                Capability.CustomEqFrequencies
                    .Select(value => (ushort)Math.Clamp(value, 0, ushort.MaxValue))
                    .ToArray(),
                CustomEqualizerMinimumGain,
                CustomEqualizerMaximumGain,
                Capability.EqualizerPresets,
                FeatureSwitches.ResolveVisibleControls(Capability, featureStates),
                FeatureSwitches.ResolveControlStates(featureStates),
                FeatureSwitches.ResolveControlEnabledStates(Capability, featureStates, _state.Snapshot().Game),
                NoiseCancellation.BuildOptions(Capability),
                NoiseCancellation.GetKey(_state.Snapshot().Noise.SmartLevel ?? _state.Snapshot().Noise.Mode));
        }
    }

    // 判断当前型号是否具备多设备策略管理所需的任一协议能力。
    public bool CanManageMultiDevice
        => Capability.SupportsCommand(CommandId.MultiDevicePriority)
            || Capability.SupportsCommand(CommandId.OperateMultiDevice);

    // 返回当前品牌实现使用的自定义 EQ 编辑范围。
    public sbyte CustomEqualizerMinimumGain => CustomEqualizer.DefaultMinimumGain;

    public sbyte CustomEqualizerMaximumGain => CustomEqualizer.DefaultMaximumGain;

    // 由控制层校验自定义 EQ 名称，界面只负责收集文本。
    public bool IsValidCustomEqualizerName(string name) => CustomEqualizer.IsValidName(name);

    // 由控制层按当前型号白名单构造自定义 EQ 条目。
    public EqualizerEntrySnapshot CreateCustomEqualizerEntry(
        byte id,
        string name,
        IReadOnlyList<double> gains)
    {
        var frequencies = Capability.CustomEqFrequencies
            .Select(value => (ushort)Math.Clamp(value, 0, ushort.MaxValue))
            .ToArray();
        var existing = _state.Snapshot().EqualizerEntries.FirstOrDefault(entry =>
            id > 0 && entry.Id == id
            || string.Equals(entry.Name, name, StringComparison.Ordinal));
        var minimumGain = existing?.MinimumGain ?? CustomEqualizerMinimumGain;
        var maximumGain = existing?.MaximumGain ?? CustomEqualizerMaximumGain;
        return CustomEqualizer.CreateEntry(
            id,
            name,
            frequencies,
            gains,
            minimumGain,
            maximumGain);
    }

    // 由控制层把设备条目的协议频段对齐到当前型号白名单。
    public IReadOnlyList<sbyte> AlignCustomEqualizerGains(EqualizerEntrySnapshot entry)
    {
        var frequencies = Capability.CustomEqFrequencies
            .Select(value => (ushort)Math.Clamp(value, 0, ushort.MaxValue))
            .ToArray();
        return CustomEqualizer.AlignGains(frequencies, entry);
    }

    // 以用户指定型号重新解析本机会话能力，不重新连接也不改变设备实际产品标识。
    public void SetManualModel(string? modelName)
    {
        var snapshot = _state.Snapshot();
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
        _state.SetModelName(Capability.IsKnownModel ? Capability.ModelName : null);
    }

    // 由窗口可见性决定是否执行交互功能的补偿轮询。
    public void SetInteractivePolling(bool enabled)
    {
        ApplicationLog.Current?.Info("Polling", $"设备管理器设置交互轮询：enabled={enabled}。");
        _interactivePolling = enabled;
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
        var featureId = FeatureSwitches.ResolveGameModeFeature(Capability, _state.Snapshot().FeatureStates);
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
        if (!await WriteAsync(link, CommandId.SetEqualizer, CommandId.SetEqualizerResponse, new byte[] { presetId }, cancellationToken))
            return false;

        var response = await TryRequestAsync(link, CommandId.CurrentEqualizer, CommandId.CurrentEqualizerResponse, Array.Empty<byte>(), cancellationToken);
        if (response is not null)
            _equalizer?.ApplyCurrentPreset(response.Payload.Span);

        var success = _state.Snapshot().Equalizer.PresetId == presetId;
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

        var deviceEntry = _state.Snapshot().EqualizerEntries
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

        return _state.Snapshot().SpatialAudio.Mode == mode;
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
        var success = _state.Snapshot().Noise.Mode != NoiseMode.Unknown;
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
            var success = response is not null && _customEqualizer?.Apply(response.Payload.Span) == true;
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
            || !CustomEqualizer.TryBuildPayload(action, entry, out var payload))
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
            var success = response is not null && _customEqualizer?.Apply(response.Payload.Span) == true;
            if (success && action is 1 or 2)
            {
                // 保存后使用设备回读出的实际 ID 激活该 EQ，避免前端参与协议状态判断。
                var savedEntry = _state.Snapshot().EqualizerEntries
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
        var frequencies = Capability.CustomEqFrequencies;
        return frequencies.Count > 0
            && entry.Frequencies.Count == frequencies.Count
            && entry.Frequencies.Select(value => (int)value).SequenceEqual(frequencies);
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
            && (_state.Snapshot().Game.SoundType is > 0) == enabled;
    }

    public Task<bool> SetGameSoundEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var type = enabled
            ? _state.Snapshot().Game.SoundType is > 0 and var currentType
                ? currentType
                : Capability.PreferredGameSoundType ?? 1
            : (byte)0;
        return SetGameSoundEnabledCoreAsync(type, enabled, cancellationToken);
    }

    // 根据当前设备快照和本地隐藏策略生成多设备显示数据。
    public MultiDeviceDisplayState GetMultiDeviceDisplayState(IReadOnlySet<string> hiddenAddresses)
        => MultiDevicePolicy.BuildDisplayState(_state.Snapshot().MultiDevice, hiddenAddresses);

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
            && _state.Snapshot().Game.SoundType is > 0
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
            var identityReader = new DeviceInfoManager(_state);
            var productResponse = await TryRequestAsync(
                link,
                CommandId.ProductId,
                CommandId.ProductIdResponse,
                Array.Empty<byte>(),
                cancellationToken);
            var productIdApplied = productResponse is not null
                && identityReader.TryApplyProductId(productResponse.Payload.Span, deviceName);
            var dynamicCapability = await TryReadCapabilitiesAsync(link, cancellationToken);
            _baseCapability = _capabilityLoader.Load(
                _state.Snapshot().Identity?.ProductId,
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
            var battery = new Battery(_state, notifier);
            var wearStatus = new WearStatus(_state, notifier);
            var noiseCancellation = new NoiseCancellation(_state, Capability, notifier);
            var gameMode = new GameMode(_state, notifier);
            var equalizer = new Equalizer(_state, Capability);
            var spatialAudio = new SpatialAudio(_state);
            var featureSwitches = new FeatureSwitches(_state);
            var multiDevice = new MultiDevice(_state);
            var customEqualizer = new CustomEqualizer(_state);
            var gameSound = new GameSound(_state);
            _link = link;
            _notifier = notifier;
            _battery = battery;
            _wearStatus = wearStatus;
            _noiseCancellation = noiseCancellation;
            _gameMode = gameMode;
            _equalizer = equalizer;
            _spatialAudio = spatialAudio;
            _featureSwitches = featureSwitches;
            _multiDevice = multiDevice;
            _customEqualizer = customEqualizer;
            _gameSound = gameSound;
            link.Disconnected += OnLinkDisconnected;
            _state.SetConnected(deviceName);
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
        if (_link is not null)
        {
            _link.Disconnected -= OnLinkDisconnected;
            await _link.DisposeAsync();
        }

        _battery = null;
        _wearStatus = null;
        _noiseCancellation = null;
        _gameMode = null;
        _equalizer = null;
        _spatialAudio = null;
        _featureSwitches = null;
        _multiDevice = null;
        _customEqualizer = null;
        _gameSound = null;
        _bassEngineState = null;
        _notifier = null;
        _link = null;
        _baseCapability = DeviceCapability.Unknown;
        Capability = DeviceCapability.Unknown;
        _featureProbeCompleted = false;
        _state.Reset();
    }

    public ValueTask DisposeAsync()
    {
        _state.Changed -= PublishState;
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
        if (link is null || sessionVersion != Volatile.Read(ref _sessionVersion) || !ReferenceEquals(link, _link))
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
        if (notification.EventId != Notifier.MultiDeviceEvent || _link is null)
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
        if (_link is null || _equalizer is null)
            return;

        _equalizer.ApplyCurrentPreset(notification.Payload.Span);
        var sessionVersion = Volatile.Read(ref _sessionVersion);
        _ = RefreshEqualizerFromNotificationAsync(sessionVersion);
    }

    private async Task RefreshEqualizerFromNotificationAsync(long sessionVersion)
    {
        try
        {
            if (sessionVersion != Volatile.Read(ref _sessionVersion) || _link is null)
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
        Equalizer equalizer,
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

        var deviceInfo = new DeviceInfoManager(_state);
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
                Capability = FeatureSwitches.RefineCapability(_baseCapability, _state.Snapshot().FeatureStates);
            }
        }
        ApplicationLog.Current?.Info("Session", "设备初始状态读取完成。");
    }

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
        Equalizer equalizer,
        SpatialAudio spatialAudio)
    {
        ApplicationLog.Current?.Info("Polling", $"启动轻量轮询：interactive={_interactivePolling}，interval=2s。");
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
        Equalizer equalizer,
        SpatialAudio spatialAudio,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        ApplicationLog.Current?.Debug("Polling", $"轮询循环开始：interactive={_interactivePolling}。");
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var now = DateTimeOffset.UtcNow;
            ApplicationLog.Current?.Debug("Polling", $"轮询 tick：interactive={_interactivePolling}。");
            await RefreshBatteryFallbackAsync(link, notifier, battery, now, cancellationToken);
            if (!_interactivePolling)
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
        if (_link is null)
            return;

        if (Capability.SupportsCommand(CommandId.SetFeature)
            && now - _lastFeatureStateRefreshUtc >= TimeSpan.FromSeconds(15))
        {
            _lastFeatureStateRefreshUtc = now;
            await RefreshFeatureStatesAsync(_link, cancellationToken);
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
        Equalizer equalizer,
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
        if (!CanUseCommand(CommandId.SetFeature) || !_state.Snapshot().FeatureStates.Values.ContainsKey(featureId))
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
        return _state.Snapshot().FeatureStates.TryGetValue(featureId, out var current) && current == enabled;
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
        var state = _state.Snapshot().FeatureStates.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        state[BassEngine.FeatureId] = actual.Current != actual.Minimum;
        _state.SetFeatureStates(new FeatureStateSnapshot(state));
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
            Capability = FeatureSwitches.RefineCapability(_baseCapability, _state.Snapshot().FeatureStates);
        }
    }

    private bool CanUseCommand(ushort writeCommand, ushort? readCommand = null)
        => _link is not null
            && Capability.SupportsCommand(writeCommand)
            && (readCommand is null || Capability.SupportsCommand(readCommand.Value));

    private ConnectionLink RequireLink()
        => _link ?? throw new InvalidOperationException("No device session is active.");

    // 为写命令统一附加三秒超时，并把超时转为可恢复失败。
    private static async Task<bool> WriteAsync(
        ConnectionLink link,
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await new CommandWriter(link).WriteAsync(command, responseCommand, payload, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // 将能力表中的降噪位索引编码为设备要求的位图负载。
    private static byte[] BuildNoisePayload(byte protocolIndex)
    {
        var bytes = new byte[3 + protocolIndex / 8];
        bytes[0] = 1;
        bytes[1] = 1;
        bytes[2 + protocolIndex / 8] = (byte)(1 << (protocolIndex % 8));
        return bytes;
    }

    // 为读取命令统一附加三秒超时，避免单个设备响应拖住会话。
    private static async Task<ProtocolFrame?> TryRequestAsync(
        ConnectionLink link,
        ushort command,
        ushort responseCommand,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await link.RequestAsync(command, responseCommand, payload, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

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
