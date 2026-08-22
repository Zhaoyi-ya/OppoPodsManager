namespace OppoPodsManager;

public sealed class KeyFunctionItem
{
    public byte DeviceType { get; init; }
    public byte DeviceButton { get; init; }
    public byte ButtonAction { get; init; }
    public byte Function { get; set; }

    public KeyFunctionItem()
    {
    }

    public KeyFunctionItem(byte deviceType, byte deviceButton, byte buttonAction, byte function)
    {
        DeviceType = deviceType;
        DeviceButton = deviceButton;
        ButtonAction = buttonAction;
        Function = function;
    }

    public byte[] ToBytes() => new[] { DeviceType, DeviceButton, ButtonAction, Function };
}
