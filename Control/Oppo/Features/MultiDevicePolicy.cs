using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Oppo.Features;

// 根据本地隐藏策略和设备快照生成多设备界面所需的业务集合。
public static class MultiDevicePolicy
{
    // 过滤本地隐藏设备，并从可显示设备中筛出可设置优先级的已连接设备。
    public static MultiDeviceDisplayState BuildDisplayState(
        MultiDeviceSnapshot snapshot,
        IReadOnlySet<string> hiddenAddresses)
    {
        var visibleDevices = snapshot.Devices
            .Where(device => device.IsCurrent || !hiddenAddresses.Contains(device.Address))
            .ToArray();
        var connectedDevices = visibleDevices
            .Where(device => device.ConnectionState == 2
                && !string.IsNullOrWhiteSpace(device.Address))
            .ToArray();
        return new MultiDeviceDisplayState(visibleDevices, connectedDevices);
    }

    // 将优先设备选择转换为控制层使用的设备操作。
    public static MultiDeviceOperation GetPriorityOperation(bool automatic)
        => automatic
            ? MultiDeviceOperation.AutomaticPriority
            : MultiDeviceOperation.SetPriority;
}

// 表示多设备列表经过本地策略处理后的显示数据。
public sealed record MultiDeviceDisplayState(
    IReadOnlyList<ConnectedDeviceSnapshot> VisibleDevices,
    IReadOnlyList<ConnectedDeviceSnapshot> ConnectedDevices);
