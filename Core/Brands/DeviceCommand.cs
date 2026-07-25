using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Core.Brands;

public abstract record DeviceCommand
{
    private DeviceCommand() { }

    public sealed record SetAnc(string Mode) : DeviceCommand;
    public sealed record SetSpatial(bool Enabled) : DeviceCommand;
    public sealed record SetSpatialAudio(string Mode) : DeviceCommand;
    public sealed record SetGameMode(bool Enabled) : DeviceCommand;
    public sealed record SetGameSound(bool Enabled) : DeviceCommand;
    public sealed record SetFeature(DeviceFeature Feature, bool Enabled) : DeviceCommand;
    public sealed record SetEqualizer(string Name) : DeviceCommand;
    public sealed record SetCustomEqualizer(
        IReadOnlyList<int> Gains,
        string Name,
        byte? Id = null,
        IReadOnlyList<int>? Frequencies = null,
        int Minimum = -6,
        int Maximum = 6) : DeviceCommand;
    public sealed record DeleteEqualizer(byte Id, EqualizerEntry? Entry = null) : DeviceCommand;
    public sealed record QueryEqualizer : DeviceCommand;
    public sealed record QueryEqualizerDetails : DeviceCommand;
    public sealed record FindDevice(bool Start) : DeviceCommand;
    public sealed record QueryBattery : DeviceCommand;
    public sealed record QueryMultiDevice : DeviceCommand;
    public sealed record OperateMultiDevice(string Address, MultiDeviceOperation Operation) : DeviceCommand;
}

public enum MultiDeviceOperation
{
    Connect,
    Disconnect,
    SetPriority,
    AutoSwitch,
    Unpair,
}
