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

    public void Apply(ReadOnlySpan<byte> payload)
    {
        var data = payload;
        // 查询响应使用 [status][query-kind][sub-kind][bitmap...]，通知没有 status。
        if (data.Length >= 2 && data[0] == 0)
            data = data[1..];
        if (data.Length == 0)
            return;

        var isSmartLevel = data.Length >= 2
            && (data[0] == 4 && data[1] == 1 || data[0] == 1 && data[1] == 4);
        var bitmap = data.Length >= 4 && data[0] == 1 && (data[1] == 1 || data[1] == 4)
            ? data[2..]
            : data.Length > 1 ? data[1..] : data;
        var mode = ResolveBitmap(bitmap);
        if (mode == NoiseMode.Unknown && data.Length > 1)
            mode = ResolveMode(data[1]);
        var current = _state.Snapshot().Noise;
        _state.SetNoise(isSmartLevel
            ? current with { Mode = NoiseMode.Smart, SmartLevel = mode }
            : new NoiseSnapshot(mode, null));
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
            var parentProtocol = children.Length == 0
                ? capability.NoiseModes.FirstOrDefault(item => item.Value == group.Parent).Key
                : (byte)0;
            options.Add(new NoiseOptionModel(GetKey(group.Parent), group.Parent, parentProtocol, children));
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
            "Smart" or "Adaptive" => NoiseMode.Smart,
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

    private NoiseMode ResolveMode(byte encodedMode)
    {
        if (_capability.NoiseModes.TryGetValue(encodedMode, out var mode))
            return mode;

        return encodedMode switch
        {
            0 => NoiseMode.Off,
            1 => NoiseMode.NoiseCancellation,
            2 => NoiseMode.Transparency,
            3 => NoiseMode.Smart,
            _ => NoiseMode.Unknown
        };
    }

    // 从小端位图找出当前协议位，并根据型号映射为逻辑降噪模式。
    private NoiseMode ResolveBitmap(ReadOnlySpan<byte> bitmap)
    {
        for (var byteIndex = 0; byteIndex < bitmap.Length && byteIndex < 4; byteIndex++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                if ((bitmap[byteIndex] & (1 << bit)) == 0)
                    continue;

                var protocolIndex = (byte)(byteIndex * 8 + bit);
                if (_capability.NoiseModes.TryGetValue(protocolIndex, out var mode))
                    return mode;

                return ResolveMode(protocolIndex);
            }
        }

        return NoiseMode.Unknown;
    }
}

