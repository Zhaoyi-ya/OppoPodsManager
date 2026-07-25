namespace OppoPodsManager.Core.Devices;

public sealed class HeadsetState
{
    public ConnectionState Connection { get; set; } = ConnectionState.Disconnected;
    public BatteryState Battery { get; } = new();
    public AncState Anc { get; } = new();
    public EqualizerState Equalizer { get; } = new();
    public bool SpatialAudioEnabled { get; set; }
    public string SpatialMode { get; set; } = "Off";
    public bool GamingEnabled { get; set; }
    public MultiDeviceState MultiDevice { get; } = new();
    public string? FirmwareVersion { get; set; }
    public int CodecType { get; set; } = -1;
    public string? LeftWearing { get; set; }
    public string? RightWearing { get; set; }
    public IReadOnlySet<ushort> SupportedCommands { get; set; } = new HashSet<ushort>();
    public IReadOnlyList<EqualizerEntry> DeviceEqualizers { get; set; } = [];
    public IReadOnlyDictionary<DeviceFeature, bool> FeatureStates { get; set; } =
        new Dictionary<DeviceFeature, bool>();
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Identifying,
    Faulted,
}

public sealed class BatteryState
{
    public int? Left { get; set; }
    public int? Right { get; set; }
    public int? Case { get; set; }
    public bool LeftCharging { get; set; }
    public bool RightCharging { get; set; }
    public bool CaseCharging { get; set; }
}

public sealed class AncState
{
    public string? Mode { get; set; }
    public string? Level { get; set; }
    /// <summary>
    /// Real-time adaptive ANC sub-level when Mode is Smart (e.g. depth).
    /// Empty/null when not in intelligent mode.
    /// </summary>
    public string? IntelligentRealtime { get; set; }
}

public sealed class EqualizerState
{
    public string? Preset { get; set; }
    public IReadOnlyList<int> Gains { get; set; } = [];
}

public sealed record EqualizerEntry(
    byte Id,
    string Name,
    IReadOnlyList<int> Frequencies,
    IReadOnlyList<int> Gains,
    int Minimum = -6,
    int Maximum = 6,
    bool IsSelected = false);

public sealed class MultiDeviceState
{
    public IReadOnlyList<MultiDeviceEntry> Devices { get; set; } = [];
    public IReadOnlyList<string> ConnectedAddresses { get; set; } = [];
    public string? PriorityAddress { get; set; }
    public bool AutomaticPriority { get; set; }
}

public sealed record MultiDeviceEntry(
    string Address,
    string? Name = null,
    int ConnectionState = 0,
    int DeviceType = 0,
    bool IsCurrentDevice = false,
    bool IsAudioActive = false,
    bool IsMainAudioDevice = false);
