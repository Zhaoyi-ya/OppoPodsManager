using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Huawei;

// 华为设备型号识别与能力路由。
//
// 型号识别：蓝牙名小写并去除非字母数字后，与 alias 表精确匹配（与参考实现 normalizeDeviceName 一致）。
// 能力路由：14 款型号的能力声明来自 DeviceCapabilities.kt 的华为设备能力表，逐项对齐。
public enum HuaweiRoute
{
    FreeBuds3,
    FreeBuds4E,
    FreeBuds5,
    FreeBuds5I,
    FreeBuds6I,
    FreeBudsPro3,
    FreeBudsPro4,
    FreeBudsPro5,
    FreeBuds7I,
    FreeClip,
    FreeClip2,
    FreeArc,
    Eyewear,
    Eyewear2,
    Unsupported,
}

public sealed record HuaweiCapabilities(
    string DisplayName,
    bool SupportsAnc,
    bool SupportsTransparency,
    bool SupportsAncStateReadback,
    bool SupportsDiscreteAncLevels,
    bool SupportsAncDirectionDial,
    bool SupportsRfcommBattery,
    bool SupportsGestureConfiguration,
    bool SupportsWearDetection,
    bool SupportsEqualizer,
    bool SupportsLowLatency,
    bool SupportsDualConnect,
    bool HasChargingCase,
    bool UsesReportedEarbudAvailability)
{
    public static HuaweiCapabilities Unknown { get; } = new("HUAWEI TWS",
        SupportsAnc: false, SupportsTransparency: false, SupportsAncStateReadback: false,
        SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
        SupportsRfcommBattery: false, SupportsGestureConfiguration: false,
        SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
        HasChargingCase: true, UsesReportedEarbudAvailability: false);
}

public static class HuaweiModels
{
    // 与 DeviceCapabilities.kt routeCapabilities 逐项对齐的 14 款型号能力表。
    // SupportsEqualizer / SupportsLowLatency / SupportsDualConnect 来自 OpenFreebuds per_model handler 注册：
    //   buds_5i/6i: EQ + 低延迟 + 双设备；buds_pro_3/5: EQ + 低延迟 + 双设备（含自定义 EQ）；
    //   free_clip_2: EQ + 低延迟 + 双设备。其余型号无明确 handler 映射，保守置 false。
    private static readonly IReadOnlyDictionary<HuaweiRoute, HuaweiCapabilities> Capabilities =
        new Dictionary<HuaweiRoute, HuaweiCapabilities>
        {
            [HuaweiRoute.FreeBuds3] = new("HUAWEI FreeBuds 3",
                SupportsAnc: true, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: true,
                SupportsRfcommBattery: false, SupportsGestureConfiguration: true,
                SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBuds4E] = new("HUAWEI FreeBuds 4E",
                SupportsAnc: true, SupportsTransparency: false, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: true, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: true, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBuds5] = new("HUAWEI FreeBuds 5",
                SupportsAnc: true, SupportsTransparency: false, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: true, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: false,
                SupportsWearDetection: true, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBuds5I] = new("HUAWEI FreeBuds 5i",
                SupportsAnc: true, SupportsTransparency: true, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: true, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: true, SupportsEqualizer: true, SupportsLowLatency: true, SupportsDualConnect: true,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBuds6I] = new("HUAWEI FreeBuds 6i",
                SupportsAnc: true, SupportsTransparency: true, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: true, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: true, SupportsEqualizer: true, SupportsLowLatency: true, SupportsDualConnect: true,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBudsPro3] = new("HUAWEI FreeBuds Pro 3",
                SupportsAnc: true, SupportsTransparency: true, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: true, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: true, SupportsEqualizer: true, SupportsLowLatency: true, SupportsDualConnect: true,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBudsPro4] = new("HUAWEI FreeBuds Pro 4",
                SupportsAnc: true, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: false,
                SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeBudsPro5] = new("HUAWEI FreeBuds Pro 5",
                SupportsAnc: true, SupportsTransparency: true, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: false,
                SupportsWearDetection: false, SupportsEqualizer: true, SupportsLowLatency: true, SupportsDualConnect: true,
                HasChargingCase: true, UsesReportedEarbudAvailability: true),
            [HuaweiRoute.FreeBuds7I] = new("HUAWEI FreeBuds 7i",
                SupportsAnc: true, SupportsTransparency: true, SupportsAncStateReadback: true,
                SupportsDiscreteAncLevels: true, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: true, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeClip] = new("HUAWEI FreeClip",
                SupportsAnc: false, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: false,
                SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeClip2] = new("HUAWEI FreeClip 2",
                SupportsAnc: false, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: true, SupportsEqualizer: true, SupportsLowLatency: true, SupportsDualConnect: true,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.FreeArc] = new("HUAWEI FreeArc",
                SupportsAnc: false, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: true, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.Eyewear] = new("HUAWEI Eyewear",
                SupportsAnc: false, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: false,
                SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: false, UsesReportedEarbudAvailability: false),
            [HuaweiRoute.Eyewear2] = new("HUAWEI Eyewear 2",
                SupportsAnc: false, SupportsTransparency: false, SupportsAncStateReadback: false,
                SupportsDiscreteAncLevels: false, SupportsAncDirectionDial: false,
                SupportsRfcommBattery: true, SupportsGestureConfiguration: true,
                SupportsWearDetection: false, SupportsEqualizer: false, SupportsLowLatency: false, SupportsDualConnect: false,
                HasChargingCase: false, UsesReportedEarbudAvailability: false),
        };

    // 归一化 alias → 型号（normalizeDeviceName = lowercase + 仅字母数字）。
    private static readonly IReadOnlyDictionary<string, HuaweiRoute> AliasRoutes = BuildAliasRoutes();

    private static IReadOnlyDictionary<string, HuaweiRoute> BuildAliasRoutes()
    {
        var map = new Dictionary<string, HuaweiRoute>(StringComparer.Ordinal);
        void Add(HuaweiRoute route, string alias) => map[Normalize(alias)] = route;
        Add(HuaweiRoute.FreeBuds3, "HUAWEI FreeBuds 3");
        Add(HuaweiRoute.FreeBuds3, "FreeBuds 3");
        Add(HuaweiRoute.FreeBuds4E, "HUAWEI FreeBuds 4E");
        Add(HuaweiRoute.FreeBuds4E, "FreeBuds 4E");
        Add(HuaweiRoute.FreeBuds5, "HUAWEI FreeBuds 5");
        Add(HuaweiRoute.FreeBuds5, "FreeBuds 5");
        Add(HuaweiRoute.FreeBuds5I, "HUAWEI FreeBuds 5i");
        Add(HuaweiRoute.FreeBuds5I, "FreeBuds 5i");
        Add(HuaweiRoute.FreeBuds6I, "HUAWEI FreeBuds 6i");
        Add(HuaweiRoute.FreeBuds6I, "FreeBuds 6i");
        Add(HuaweiRoute.FreeBudsPro3, "HUAWEI FreeBuds Pro 3");
        Add(HuaweiRoute.FreeBudsPro3, "FreeBuds Pro 3");
        Add(HuaweiRoute.FreeBudsPro4, "HUAWEI FreeBuds Pro 4");
        Add(HuaweiRoute.FreeBudsPro4, "FreeBuds Pro 4");
        Add(HuaweiRoute.FreeBudsPro5, "HUAWEI FreeBuds Pro 5");
        Add(HuaweiRoute.FreeBudsPro5, "FreeBuds Pro 5");
        Add(HuaweiRoute.FreeBuds7I, "HUAWEI FreeBuds 7i");
        Add(HuaweiRoute.FreeBuds7I, "FreeBuds 7i");
        Add(HuaweiRoute.FreeClip, "HUAWEI FreeClip");
        Add(HuaweiRoute.FreeClip, "FreeClip");
        Add(HuaweiRoute.FreeClip2, "HUAWEI FreeClip 2");
        Add(HuaweiRoute.FreeClip2, "FreeClip 2");
        Add(HuaweiRoute.FreeArc, "HUAWEI FreeArc");
        Add(HuaweiRoute.FreeArc, "FreeArc");
        Add(HuaweiRoute.Eyewear, "HUAWEI Eyewear");
        Add(HuaweiRoute.Eyewear2, "HUAWEI Eyewear 2");
        Add(HuaweiRoute.Eyewear2, "Eyewear 2");
        return map;
    }

    public static HuaweiRoute DetectRoute(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return HuaweiRoute.Unsupported;
        var normalized = Normalize(deviceName);
        // 精确 alias 命中优先；其次按型号前缀兜底（如 "FreeBuds Pro 3 (XXXX)" 等带后缀命名）。
        if (AliasRoutes.TryGetValue(normalized, out var exact))
            return exact;
        foreach (var (alias, route) in AliasRoutes)
        {
            if (normalized.StartsWith(alias, StringComparison.Ordinal))
                return route;
        }
        return HuaweiRoute.Unsupported;
    }

    // 品牌候选识别：任何华为产品家族前缀都视为可能（ControlManager 会再按服务 UUID 验证）。
    public static bool IsFamilyName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return false;
        var normalized = Normalize(deviceName);
        return normalized.StartsWith("huaweifreebuds", StringComparison.Ordinal)
            || normalized.StartsWith("freebuds", StringComparison.Ordinal)
            || normalized.StartsWith("huaweifreeclip", StringComparison.Ordinal)
            || normalized.StartsWith("freeclip", StringComparison.Ordinal)
            || normalized.StartsWith("huaweifreearc", StringComparison.Ordinal)
            || normalized.StartsWith("freearc", StringComparison.Ordinal)
            || normalized.StartsWith("huaweieyewear", StringComparison.Ordinal)
            || normalized.StartsWith("eyewear", StringComparison.Ordinal)
            || normalized.StartsWith("huawei", StringComparison.Ordinal);
    }

    public static HuaweiCapabilities GetCapabilities(HuaweiRoute route)
        => Capabilities.TryGetValue(route, out var capabilities) ? capabilities : HuaweiCapabilities.Unknown;

    public static bool IsKnown(HuaweiRoute route) => route != HuaweiRoute.Unsupported && Capabilities.ContainsKey(route);

    // 小写 + 仅保留字母数字（与 normalizeDeviceName 对齐）。
    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        }
        return builder.ToString();
    }
}

// 降噪档位映射：同一直观档位在不同型号上协议值不同，必须按型号路由。
// 参考 HuaweiAncLevelProfile.kt 的四套映射（ADAPTIVE=智慧动态/均衡、LIGHT=轻度、BALANCED=均衡、DEEP=深度）。
public static class HuaweiAncLevels
{
    // 将项目 NoiseMode（Smart/Light/Medium/Deep）映射到该型号的协议子模式值。
    public static byte? MapToProtocol(HuaweiRoute route, NoiseMode mode)
    {
        if (!TryGetOptions(route, out var options))
            return null;
        var target = mode switch
        {
            NoiseMode.Smart => HuaweiAncLevel.Adaptive,
            NoiseMode.Light => HuaweiAncLevel.Light,
            NoiseMode.Medium => HuaweiAncLevel.Balanced,
            NoiseMode.Deep => HuaweiAncLevel.Deep,
            _ => (HuaweiAncLevel?)null
        };
        if (target is null)
            return null;
        foreach (var (level, protocol) in options)
        {
            if (level == target.Value)
                return protocol;
        }
        return null;
    }

    // 将协议子模式值映射回项目 NoiseMode（回读展示用）。
    public static NoiseMode? MapFromProtocol(HuaweiRoute route, byte value)
    {
        if (!TryGetOptions(route, out var options))
            return null;
        foreach (var (level, protocol) in options)
        {
            if (protocol == value)
                return level switch
                {
                    HuaweiAncLevel.Adaptive => NoiseMode.Smart,
                    HuaweiAncLevel.Light => NoiseMode.Light,
                    HuaweiAncLevel.Balanced => NoiseMode.Medium,
                    HuaweiAncLevel.Deep => NoiseMode.Deep,
                    _ => NoiseMode.Unknown
                };
        }
        return null;
    }

    // 型号默认 NC 子模式（无离散档位型号返回 null，由调用方用 AncSubModeDefault）。
    public static byte? DefaultSubMode(HuaweiRoute route)
    {
        if (!TryGetOptions(route, out var options) || options.Count == 0)
            return null;
        return options[0].Protocol;
    }

    public static bool HasDiscreteLevels(HuaweiRoute route) => TryGetOptions(route, out var options) && options.Count > 1;

    // 型号支持的离散档位列表（项目 NoiseMode + 协议子模式值），供 UI 构建降噪子模式选项。
    public static IReadOnlyList<(NoiseMode Mode, byte Protocol)> GetSupportedLevels(HuaweiRoute route)
    {
        if (!TryGetOptions(route, out var options))
            return [];
        var list = new List<(NoiseMode, byte)>(options.Count);
        foreach (var (level, protocol) in options)
        {
            var mode = level switch
            {
                HuaweiAncLevel.Adaptive => NoiseMode.Smart,
                HuaweiAncLevel.Light => NoiseMode.Light,
                HuaweiAncLevel.Balanced => NoiseMode.Medium,
                HuaweiAncLevel.Deep => NoiseMode.Deep,
                _ => NoiseMode.Unknown
            };
            if (mode != NoiseMode.Unknown)
                list.Add((mode, protocol));
        }
        return list;
    }

    private enum HuaweiAncLevel { Adaptive, Light, Balanced, Deep }

    private static bool TryGetOptions(HuaweiRoute route, out IReadOnlyList<(HuaweiAncLevel Level, byte Protocol)> options)
    {
        options = route switch
        {
            // 5i/6i 实机抓包确认：智慧动态=3、轻度=1、均衡=0、深度=2。
            HuaweiRoute.FreeBuds5I or HuaweiRoute.FreeBuds6I =>
                [(HuaweiAncLevel.Adaptive, (byte)0x03), (HuaweiAncLevel.Light, (byte)0x01),
                 (HuaweiAncLevel.Balanced, (byte)0x00), (HuaweiAncLevel.Deep, (byte)0x02)],
            // Pro 3 / 7i：智慧动态=1、轻度=0、均衡=2、深度=3。
            HuaweiRoute.FreeBudsPro3 or HuaweiRoute.FreeBuds7I =>
                [(HuaweiAncLevel.Adaptive, (byte)0x01), (HuaweiAncLevel.Light, (byte)0x00),
                 (HuaweiAncLevel.Balanced, (byte)0x02), (HuaweiAncLevel.Deep, (byte)0x03)],
            // FreeBuds 5：智慧动态=3、轻度=1、均衡=0。
            HuaweiRoute.FreeBuds5 =>
                [(HuaweiAncLevel.Adaptive, (byte)0x03), (HuaweiAncLevel.Light, (byte)0x01),
                 (HuaweiAncLevel.Balanced, (byte)0x00)],
            // FreeBuds 4E 实机确认仅 轻度=1、均衡=0。
            HuaweiRoute.FreeBuds4E =>
                [(HuaweiAncLevel.Light, (byte)0x01), (HuaweiAncLevel.Balanced, (byte)0x00)],
            _ => []
        };
        return options.Count > 0;
    }
}
