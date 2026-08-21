namespace OppoPodsManager.Control.Brands.Vivo;

// vivo / iQOO 设备能力白名单（对应 vivo App 反编译的 EarbudFeatures.FeatureID）。
//
// 每个 vivo 设备在上报能力时携带一份 feature_id 清单；清单里有某个 id，即代表该型号
// 支持对应功能——这正是 OPPO 侧 CapabilityLoader（型号能力表）的同一思路，界面可见控件
// 必须按此能力决定，绝不能硬编码开关。
//
// 数据来源：resources/assets/tws_config.json（官方 vivo TWS App 反编译出的逐型号能力矩阵，
// 共 87 条 model 配置 → 归一化后 41 个型号）。本文件据此生成「型号 → 支持功能 id 集合」的正向表，
// 取代原先手维护的 KnownUnsupported 反向表。
//   * 已知型号：仅显示其能力集合内的功能；矩阵中明确缺失的功能直接隐藏。注意：此前依据 btsnoop
//     抓包把 0x18/0x20 命令族推断为 game/spatial 并为 vivotws3e 补回这两项——该推断已被推翻
//     （真机实测 TWS 3e 对 0x0220/0x0218 查询超时，且用户确认 TWS 3e 无此二功能），故 vivotws3e
//     已回退为仅 降噪+查找。0x18/0x20 的真实语义待从「确有此功能的机型」抓包确认，目前不对外暴露。
//   * 未知型号：开发 / 测试期仍乐观显示全部相关控件，便于在真机上逐项验证命令实现；
//     一旦某型号实测不支持某项功能，运行期超时探测（VivoManager._runtimeUnsupported）会按会话隐藏。
//
// 注：tws_config.json 提供的是「能力声明」，并非 GAIA 命令字。2026-08-08 HCI 抓包已实证
// find=0x31 族（0x0231/0x8231）；抓包中另见 0x18/0x20 两族命令在 TWS 3e 上有响应，但经真机验证
// 这二者并非 game/spatial（TWS 3e 无此功能，且查询 0x0220/0x0218 超时），其真实语义未知，故不映射
// 为 game/spatial 控件（见 VivoConstants 的 0x18/0x20 备注）。旧占位 0x0132/0x0133 已证伪。
internal static class VivoFeatureMatrix
{
    // 与 EarbudFeatures.FeatureID 对齐（仅列出本项目关心的功能）。
    public const int NoiseReduction = 2;
    public const int FindEarphone = 9;
    public const int SpatialAudio = 12;
    public const int DualConnection = 17;
    public const int LowLatencyGaming = 19;
    // 内部映射键：与 OPPO 的 0x04 无关，vivo 自有 EarbudFeatures.FeatureID；正式值待与
    // tws_config.json 对齐确认（当前仅用于本矩阵的 KnownSupported 内部比对，不影响协议帧）。
    public const int WearDetection = 1;

    // feature id → 界面控件 key（与 OPPO FeatureSwitches.ResolveVisibleControls 的命名一致）。
    private static readonly IReadOnlyDictionary<int, string> FeatureControlKeys = new Dictionary<int, string>(5)
    {
        [FindEarphone] = "find-device",
        [LowLatencyGaming] = "game-mode",
        [SpatialAudio] = "spatial-sound",
        [WearDetection] = "wear-detection",
        [DualConnection] = "dual-device",
    };

    // 型号归一化名 → 该型号声明支持的功能 id 集合。
    // 取自官方 vivo TWS App 的 tws_config.json：feature.noise_reduction=2、feature.find_earphone=9、
    // feature.spatial_audio / spatial_audio_3d=12、feature.dual_connection=17、feature.low_latency_gaming=19。
    // 矩阵中明确不含某 id 的型号即视为不支持，界面隐藏对应控件。
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> KnownSupported = new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal)
    {
        ["iqooheadphones"] = new HashSet<int> { NoiseReduction, FindEarphone, SpatialAudio, DualConnection, LowLatencyGaming },
        ["iqootws1"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["iqootws1e"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["iqootws1i"] = new HashSet<int> { FindEarphone },
        ["iqootws2"] = new HashSet<int> { NoiseReduction, FindEarphone, LowLatencyGaming },
        ["iqootws5"] = new HashSet<int> { NoiseReduction, FindEarphone, SpatialAudio, DualConnection, LowLatencyGaming },
        ["iqootws5e"] = new HashSet<int> { NoiseReduction, FindEarphone, DualConnection, LowLatencyGaming },
        ["iqootws5i"] = new HashSet<int> { FindEarphone, DualConnection, LowLatencyGaming },
        ["iqootwsair"] = new HashSet<int> { FindEarphone },
        ["iqootwsair2"] = new HashSet<int> { FindEarphone },
        ["iqootwsair3"] = new HashSet<int> { FindEarphone, LowLatencyGaming },
        ["iqootwsair3pro"] = new HashSet<int> { NoiseReduction, FindEarphone, LowLatencyGaming },
        ["iqootwsairpro"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["vivoheadphones"] = new HashSet<int> { NoiseReduction, FindEarphone, SpatialAudio, DualConnection, LowLatencyGaming },
        ["vivotws1"] = new HashSet<int>(),
        ["vivotws2"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["vivotws2e"] = new HashSet<int> { FindEarphone },
        ["vivotws3"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["vivotws3e"] = new HashSet<int> { NoiseReduction, FindEarphone, WearDetection, DualConnection },
        ["vivotws3i"] = new HashSet<int> { FindEarphone },
        ["vivotws3pro"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["vivotws4"] = new HashSet<int> { NoiseReduction, FindEarphone, LowLatencyGaming },
        ["vivotws4hifi"] = new HashSet<int> { NoiseReduction, FindEarphone, LowLatencyGaming },
        ["vivotws5"] = new HashSet<int> { NoiseReduction, FindEarphone, SpatialAudio, DualConnection, LowLatencyGaming },
        ["vivotws5e"] = new HashSet<int> { NoiseReduction, FindEarphone, DualConnection, LowLatencyGaming },
        ["vivotws5hifi"] = new HashSet<int> { NoiseReduction, FindEarphone, SpatialAudio, DualConnection, LowLatencyGaming },
        ["vivotws5i"] = new HashSet<int> { FindEarphone, DualConnection, LowLatencyGaming },
        ["vivotws5pro"] = new HashSet<int> { NoiseReduction, FindEarphone, SpatialAudio, DualConnection, LowLatencyGaming },
        ["vivotwsa1"] = new HashSet<int> { FindEarphone },
        ["vivotwsa1pro"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["vivotwsa2"] = new HashSet<int> { FindEarphone },
        ["vivotwsa3"] = new HashSet<int> { FindEarphone },
        ["vivotwsa4"] = new HashSet<int> { FindEarphone, LowLatencyGaming },
        ["vivotwsa5"] = new HashSet<int> { FindEarphone, DualConnection, LowLatencyGaming },
        ["vivotwsair"] = new HashSet<int> { FindEarphone },
        ["vivotwsair2"] = new HashSet<int> { FindEarphone },
        ["vivotwsair3"] = new HashSet<int> { FindEarphone, LowLatencyGaming },
        ["vivotwsair3pro"] = new HashSet<int> { NoiseReduction, FindEarphone, LowLatencyGaming },
        ["vivotwsairpro"] = new HashSet<int> { NoiseReduction, FindEarphone },
        ["vivotwsneo"] = new HashSet<int> { FindEarphone },
        ["vivotwsx1"] = new HashSet<int> { NoiseReduction, FindEarphone },
    };

    // 强制开启（不可切换、无对应设置命令）的功能：硬件支持该能力，但"始终开启"，耳机既不响应
    // 0x014C 开关下发、0x0249 列表查询也超时。此类功能对用户无意义（点了报错、且无法关闭），
    // 故不暴露对应控件、也不发起查询/下发，直接按型号隐藏。
    // 首个已知型号 vivo TWS 3e：用户实测确认其双设备连接为强制开启、不可关闭（0x014C/0x0249 均超时）。
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> KnownForced = new Dictionary<string, IReadOnlySet<int>>(StringComparer.Ordinal)
    {
        ["vivotws3e"] = new HashSet<int> { DualConnection },
    };

    // 判断某型号某功能是否为"强制开启/不可切换"（硬件支持但无用户开关）。
    public static bool IsFeatureForced(string? deviceName, int featureId)
    {
        var normalized = VivoModels.Normalize(deviceName ?? string.Empty);
        return KnownForced.TryGetValue(normalized, out var forced) && forced.Contains(featureId);
    }

    // 判断某型号是否支持指定功能 id。
    // 已知型号：仅当其能力集合包含该 id 时为 true；未知型号乐观返回 true（便于测试，
    // 实际不支持的功能由 VivoManager 运行期超时探测隐藏）。
    public static bool IsFeatureSupported(string? deviceName, int featureId)
    {
        var normalized = VivoModels.Normalize(deviceName ?? string.Empty);
        if (KnownSupported.TryGetValue(normalized, out var supported))
            return supported.Contains(featureId);

        return true;
    }

    // 按连接设备型号解析当前应显示的 T1 控件集合（find-device / game-mode / spatial-sound）。
    public static IReadOnlySet<string> ResolveVisibleControls(string? deviceName)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (featureId, key) in FeatureControlKeys)
            if (IsFeatureSupported(deviceName, featureId) && !IsFeatureForced(deviceName, featureId))
                visible.Add(key);

        return visible;
    }
}
