namespace OppoPodsManager.Control.Core.Features;

public enum MultiDeviceOperation : byte
{
    Connect = 1,
    Disconnect = 2,
    Unpair = 3,
    SetPriority = 4,
    AutomaticPriority = 5
}
