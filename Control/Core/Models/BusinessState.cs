namespace OppoPodsManager.Control.Core.Models;

// 在锁保护下维护设备业务状态，并以不可变快照通知订阅者。
public sealed class BusinessState
{
    private readonly object _gate = new();
    private long _revision;

    public event EventHandler<BusinessSnapshot>? Changed;

    // 复制当前状态，避免调用方持有内部可变数据。
    public BusinessSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BusinessSnapshot(
                _revision,
                DeviceName,
                IsConnected,
                Identity,
                LeftBattery,
                RightBattery,
                CaseBattery,
                Wear,
                Noise,
                Equalizer,
                Game,
                SpatialAudio,
                SoundScene,
                FeatureStates,
                MultiDevice,
                EqualizerEntries,
                LastUpdatedUtc);
        }
    }

    // 主动触发一次状态变更通知（用于能力运行期变化，如某项功能超时后需隐藏控件并重绘 UI）。
    public void NotifyChanged()
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void Reset(string? deviceName = null)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            DeviceName = deviceName;
            IsConnected = false;
            Identity = null;
            LeftBattery = null;
            RightBattery = null;
            CaseBattery = null;
            Wear = WearSnapshot.Empty;
            Noise = NoiseSnapshot.Empty;
            Equalizer = EqualizerSnapshot.Empty;
            Game = GameSnapshot.Empty;
            SpatialAudio = SpatialAudioSnapshot.Empty;
            SoundScene = SoundEffectSceneSnapshot.Empty;
            FeatureStates = FeatureStateSnapshot.Empty;
            MultiDevice = MultiDeviceSnapshot.Empty;
            EqualizerEntries = [];
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetConnected(string deviceName)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            DeviceName = deviceName;
            IsConnected = true;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetBattery(BatteryLevel? left, BatteryLevel? right, BatteryLevel? chargingCase)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            LeftBattery = left;
            RightBattery = right;
            CaseBattery = chargingCase;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetIdentity(DeviceIdentity identity)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            DeviceName = identity.DisplayName;
            Identity = identity;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    // 手动型号覆盖只更新显示和能力来源，不篡改设备读取到的产品标识。
    public void SetModelName(string? modelName)
    {
        BusinessSnapshot? snapshot = null;
        lock (_gate)
        {
            if (Identity is null || string.Equals(Identity.ModelName, modelName, StringComparison.Ordinal))
                return;

            Identity = Identity with { ModelName = modelName };
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetWear(WearSnapshot wear)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            Wear = wear;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetNoise(NoiseSnapshot noise)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            Noise = noise;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetEqualizer(EqualizerSnapshot equalizer)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            Equalizer = equalizer;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetGame(GameSnapshot game)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            Game = game;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetSpatialAudio(SpatialAudioSnapshot spatialAudio)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            SpatialAudio = spatialAudio;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetSoundEffectScene(SoundEffectSceneSnapshot soundEffectScene)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            SoundScene = soundEffectScene;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetFeatureStates(FeatureStateSnapshot featureStates)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            FeatureStates = featureStates;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetMultiDevice(MultiDeviceSnapshot multiDevice)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            MultiDevice = multiDevice;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    public void SetEqualizerEntries(IReadOnlyList<EqualizerEntrySnapshot> entries)
    {
        BusinessSnapshot snapshot;
        lock (_gate)
        {
            EqualizerEntries = entries;
            LastUpdatedUtc = DateTimeOffset.UtcNow;
            _revision++;
            snapshot = CreateSnapshot();
        }

        Changed?.Invoke(this, snapshot);
    }

    private string? DeviceName { get; set; }
    private bool IsConnected { get; set; }
    private DeviceIdentity? Identity { get; set; }
    private BatteryLevel? LeftBattery { get; set; }
    private BatteryLevel? RightBattery { get; set; }
    private BatteryLevel? CaseBattery { get; set; }
    private WearSnapshot Wear { get; set; } = WearSnapshot.Empty;
    private NoiseSnapshot Noise { get; set; } = NoiseSnapshot.Empty;
    private EqualizerSnapshot Equalizer { get; set; } = EqualizerSnapshot.Empty;
    private GameSnapshot Game { get; set; } = GameSnapshot.Empty;
    private SpatialAudioSnapshot SpatialAudio { get; set; } = SpatialAudioSnapshot.Empty;
    private SoundEffectSceneSnapshot SoundScene { get; set; } = SoundEffectSceneSnapshot.Empty;
    private FeatureStateSnapshot FeatureStates { get; set; } = FeatureStateSnapshot.Empty;
    private MultiDeviceSnapshot MultiDevice { get; set; } = MultiDeviceSnapshot.Empty;
    private IReadOnlyList<EqualizerEntrySnapshot> EqualizerEntries { get; set; } = [];
    private DateTimeOffset LastUpdatedUtc { get; set; }

    private BusinessSnapshot CreateSnapshot() => new(
        _revision,
        DeviceName,
        IsConnected,
        Identity,
        LeftBattery,
        RightBattery,
        CaseBattery,
        Wear,
        Noise,
        Equalizer,
        Game,
        SpatialAudio,
        SoundScene,
        FeatureStates,
        MultiDevice,
        EqualizerEntries,
        LastUpdatedUtc);
}

public sealed record BusinessSnapshot(
    long Revision,
    string? DeviceName,
    bool IsConnected,
    DeviceIdentity? Identity,
    BatteryLevel? LeftBattery,
    BatteryLevel? RightBattery,
    BatteryLevel? CaseBattery,
    WearSnapshot Wear,
    NoiseSnapshot Noise,
    EqualizerSnapshot Equalizer,
    GameSnapshot Game,
    SpatialAudioSnapshot SpatialAudio,
    SoundEffectSceneSnapshot SoundScene,
    FeatureStateSnapshot FeatureStates,
    MultiDeviceSnapshot MultiDevice,
    IReadOnlyList<EqualizerEntrySnapshot> EqualizerEntries,
    DateTimeOffset LastUpdatedUtc);

public sealed record BatteryLevel(byte Percent, bool IsCharging);

public sealed record FeatureStateSnapshot(IReadOnlyDictionary<byte, bool> Values)
{
    public static FeatureStateSnapshot Empty { get; } = new(new Dictionary<byte, bool>());

    public bool TryGetValue(byte featureId, out bool enabled) => Values.TryGetValue(featureId, out enabled);
}

public sealed record ConnectedDeviceSnapshot(
    string Address,
    string Name,
    byte Type,
    byte ConnectionState,
    bool IsCurrent,
    bool IsAudioActive,
    bool IsAudioPriority);

public sealed record MultiDeviceSnapshot(
    IReadOnlyList<ConnectedDeviceSnapshot> Devices,
    bool IsAutomaticPriority,
    string? PriorityDeviceAddress)
{
    public static MultiDeviceSnapshot Empty { get; } = new([], true, null);
}

public sealed record EqualizerEntrySnapshot(
    byte Id,
    string Name,
    bool IsSelected,
    sbyte MinimumGain,
    sbyte MaximumGain,
    IReadOnlyList<ushort> Frequencies,
    IReadOnlyList<sbyte> Gains);

public sealed record DeviceIdentity(string ProductId, string DisplayName, string? ModelName, string? FirmwareVersion, string? Codec);

public enum EarWearState
{
    Unknown,
    Disconnected,
    Removed,
    Worn,
    InCase
}

public sealed record WearSnapshot(EarWearState Left, EarWearState Right)
{
    public static WearSnapshot Empty { get; } = new(EarWearState.Unknown, EarWearState.Unknown);
}

public sealed record NoiseSnapshot(NoiseMode Mode, NoiseMode? SmartLevel)
{
    public static NoiseSnapshot Empty { get; } = new(NoiseMode.Unknown, null);
}

public sealed record EqualizerSnapshot(byte? PresetId, string? PresetName)
{
    public static EqualizerSnapshot Empty { get; } = new(null, null);
}

public sealed record GameSnapshot(bool? IsEnabled, byte? SoundType)
{
    public static GameSnapshot Empty { get; } = new(null, null);
}

public enum SpatialAudioMode
{
    Unknown,
    Off,
    Fixed,
    HeadTracking
}

public sealed record SpatialAudioSnapshot(SpatialAudioMode Mode)
{
    public static SpatialAudioSnapshot Empty { get; } = new(SpatialAudioMode.Unknown);
}

// vivo 音效场景（set_audio_effect=0x0118，反编译实证）。scene 为 0-5 枚举，Name 为本地化描述。
public sealed record SoundEffectSceneSnapshot(byte? Scene, string? Name)
{
    public static SoundEffectSceneSnapshot Empty { get; } = new(null, null);

    public static string? ResolveName(byte scene) => scene switch
    {
        0 => "关闭",
        1 => "均衡",
        2 => "重低音",
        3 => "清澈人声",
        _ => null,
    };
}
