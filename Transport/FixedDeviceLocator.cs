namespace OppoPodsManager;

/// <summary>
/// 固定目标定位器：始终返回构造时给定的 (地址, 名称)，不做任何扫描。
/// 用于"多耳机切换"——用户选定某副耳机后，把它的地址固定注入到
/// SppTransport / WindowsGattTransport，使连接精确指向该设备而非"第一个匹配"。
/// </summary>
public sealed class FixedDeviceLocator : IDeviceLocator
{
    private readonly ulong _addr;
    private readonly string? _name;

    public FixedDeviceLocator(ulong addr, string? name)
    {
        _addr = addr;
        _name = name;
    }

    public (ulong addr, string? name) Locate() => (_addr, _name);
}
