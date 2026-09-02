using OppoPodsManager.Control.Brands.Oppo.Managers;
using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Core.Features;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 根据型号能力解析降噪模式通知及响应。
public sealed class NoiseCancellation : IDisposable
{
    private readonly BusinessState _state;
    private readonly DeviceCapability _capability;
    private readonly Notifier _notifier;

    public NoiseCancellation(BusinessState state, DeviceCapability capability, Notifier notifier)
    {
        _state = state;
        _capability = capability;
        _notifier = notifier;
        _notifier.NotificationReceived += OnNotificationReceived;
    }

    // 响应/通知 (v1,v2) → 逻辑降噪模式，对齐 main 分支的 AncValues 反向表。
    // 关键：响应编码与写入编码不同（如通透响应为 (0,1)、写入为 protocolIndex 2），
    // 故必须用此表，不能直接用 NoiseModes 做位图扫描。
    private static readonly Dictionary<(byte, byte), NoiseMode> AncReverse = new()
    {
        [(8, 0)] = NoiseMode.Off,
        [(2, 0)] = NoiseMode.Smart,
        [(0x80, 0)] = NoiseMode.Smart,
        [(0x40, 0)] = NoiseMode.Light,
        [(0x20, 0)] = NoiseMode.Medium,
        [(0x10, 0)] = NoiseMode.Deep,
        [(0, 1)] = NoiseMode.Transparency,
        [(4, 0)] = NoiseMode.Transparency,
        [(0, 8)] = NoiseMode.Adaptive,
    };

    // 对齐 main 分支 ParseAnc / ParseNoiseChange：
    // 0x0204 通知 payload[0]=0x03(EvtNoiseMode)，其后 body=[kind][mType=0x01][v1][v2]。
    // 0x810C 查询响应 payload[0]=0x00(status)，其后 body 结构相同。
    // kind=1 手动切换：直接设主模式；kind=4 智能实时档：主模式固定 Smart、子档=解析值。
    public void Apply(ReadOnlySpan<byte> payload)
    {
        var data = payload;
        // 剥离状态/事件字节（查询 0x00 或通知 0x03）。
        if (data.Length >= 1 && (data[0] == 0x00 || data[0] == 0x03))
            data = data[1..];
        // body 至少需 [kind][mType][v1][v2] 四字节；0x8404 设置回执仅 1 字节 0x00，直接忽略。
        if (data.Length < 4)
            return;

        var kind = data[0];
        var v1 = data[2];
        var v2 = data[3];

        if (!AncReverse.TryGetValue((v1, v2), out var mode))
            return;

        if (kind == 4)
            _state.SetNoise(new NoiseSnapshot(NoiseMode.Smart, mode));
        else
            _state.SetNoise(new NoiseSnapshot(mode, null));
    }

    // 根据型号能力构造降噪父子模式，统一处理官方 childrenMode 分组规则。
    public static IReadOnlyList<NoiseOptionModel> BuildOptions(DeviceCapability capability)
    {
        var options = new List<NoiseOptionModel>();
        var groupedParents = capability.NoiseGroups.Select(group => group.Parent).ToHashSet();
        var childModes = capability.NoiseGroups
            .SelectMany(group => group.Children)
            .Select(child => child.Mode)
            .ToHashSet();

        foreach (var group in capability.NoiseGroups)
        {
            var children = group.Children
                .Select(child => new NoiseOptionModel(GetKey(child.Mode), child.Mode, child.ProtocolIndex, []))
                .ToArray();
            var parentProtocol = group.ParentProtocolIndex != 0
                ? group.ParentProtocolIndex
                : capability.NoiseModes.FirstOrDefault(item => item.Value == group.Parent).Key;
            var parentKey = GetKey(group.Parent);

            // 对齐 main 分支 BuildAncOptions：
            // 单子且子模式与主模式同键（通透/关闭）折叠为可直接发送的主模式，使用父码
            // protocolIndex（与 main 的 AncTransparency=01 01 04 / AncOff=01 01 01 一致），不展开子框。
            // 多子模式（降噪）保留父容器 + 子模式列表，由 UI 展开后下发具体子模式位。
            if (children.Length == 1 && GetKey(children[0].Mode) == parentKey)
            {
                options.Add(new NoiseOptionModel(parentKey, group.Parent, parentProtocol, []));
                continue;
            }

            options.Add(new NoiseOptionModel(parentKey, group.Parent, parentProtocol, children));
        }

        options.AddRange(capability.NoiseModes
            .Where(item => !groupedParents.Contains(item.Value) && !childModes.Contains(item.Value))
            .GroupBy(item => GetKey(item.Value), StringComparer.Ordinal)
            .Select(group => new NoiseOptionModel(group.Key, group.First().Value, group.First().Key, [])));
        return options;
    }

    // 将业务降噪模式转换为稳定的界面键。
    public static string GetKey(NoiseMode mode) => mode switch
    {
        NoiseMode.Off => "Off",
        NoiseMode.Transparency => "Transparency",
        NoiseMode.Smart => "Smart",
        NoiseMode.NoiseCancellation => "NC",
        NoiseMode.Light => "Light",
        NoiseMode.Medium => "Medium",
        NoiseMode.Deep => "Deep",
        NoiseMode.Adaptive => "Adaptive",
        _ => "Adaptive"
    };

    // 解析界面键对应的业务降噪模式，供协议写入入口复用。
    public static bool TryParseKey(string key, out NoiseMode mode)
    {
        mode = key switch
        {
            "Off" => NoiseMode.Off,
            "NC" or "NoiseCancellation" => NoiseMode.NoiseCancellation,
            "Transparency" => NoiseMode.Transparency,
            "Smart" => NoiseMode.Smart,
            "Adaptive" => NoiseMode.Adaptive,
            "Light" => NoiseMode.Light,
            "Medium" => NoiseMode.Medium,
            "Deep" => NoiseMode.Deep,
            _ => NoiseMode.Unknown
        };
        return mode != NoiseMode.Unknown;
    }

    public void Dispose() => _notifier.NotificationReceived -= OnNotificationReceived;

    private void OnNotificationReceived(object? sender, NotificationReceived notification)
    {
        if (notification.EventId == Notifier.NoiseCancellationEvent)
            Apply(notification.Data.Span);
    }
}

