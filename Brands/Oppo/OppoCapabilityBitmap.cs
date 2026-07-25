using System.Diagnostics;
using OppoPodsManager.Core.Devices;

namespace OppoPodsManager.Brands.Oppo;

/// <summary>
/// Melody 0x0100 能力位图的<strong>唯一权威入口</strong>：解析 → 命令集 → 功能门控 → 与白名单求交。
/// <list type="bullet">
/// <item>位表：jadx <c>e7.C1024a.f22959a</c>（67 位，与 legacy <c>OppoProtocol.CapabilityCommands</c> 一致）</item>
/// <item>常驻命令：<c>f22960b</c></item>
/// <item>解析：Melody <c>PollCommandManager.C</c></item>
/// <item>功能裁定：专用命令优先；禁止用 0x0403 冒充 Bass/Hearing 等</item>
/// </list>
/// 其它类型（Resolver/Session/UI）只应调用本类，勿再散落命令号判断。
/// </summary>
public sealed class OppoCapabilityBitmap
{
    /// <summary>
    /// bit → 协议命令号（十进制，与 APK / legacy 完全一致）。
    /// </summary>
    private static readonly ushort[][] CommandMap =
    [
        /*  0 */ [261],
        /*  1 */ [262],
        /*  2 */ [263],
        /*  3 */ [264, 1025, 1046],
        /*  4 */ [265],
        /*  5 */ [1024],
        /*  6 */ [1026],
        /*  7 */ [1027],
        /*  8 */ [268, 1028],
        /*  9 */ [1029],
        /* 10 */ [1030, 271],
        /* 11 */ [1031],
        /* 12 */ [],
        /* 13 */ [1032],
        /* 14 */ [1033],
        /* 15 */ [],
        /* 16 */ [],
        /* 17 */ [276],
        /* 18 */ [],
        /* 19 */ [1038, 1037, 277, 278],       // 0x40E,0x40D,0x115,0x116 — 听力数据路径
        /* 20 */ [1039],
        /* 21 */ [1040, 281],
        /* 22 */ [517],                        // 0x0205 register multi
        /* 23 */ [3840],
        /* 24 */ [],
        /* 25 */ [280, 1041],
        /* 26 */ [282, 1042],
        /* 27 */ [284, 1043],
        /* 28 */ [],
        /* 29 */ [274, 1035],                  // 0x112 multi + 0x40B
        /* 30 */ [286, 287, 1045],
        /* 31 */ [1037],                       // 0x40D hearing detect process only
        /* 32 */ [],
        /* 33 */ [289, 1047],
        /* 34 */ [290, 1048],                  // EQ details
        /* 35 */ [],
        /* 36 */ [285, 1044],
        /* 37 */ [291, 1050],
        /* 38 */ [292, 1051],                  // 0x124 + 0x41B bass engine
        /* 39 */ [293, 1052, 295, 1053, 1055],
        /* 40 */ [1057, 35, 36, 34, 294, 297], // spine-related
        /* 41 */ [61185],
        /* 42 */ [61186],
        /* 43 */ [61187, 1054],
        /* 44 */ [1056],
        /* 45 */ [28],
        /* 46 */ [],
        /* 47 */ [1058, 298],                  // 0x422 + 0x12A spatial V2
        /* 48 */ [61188],
        /* 49 */ [1059, 299],                  // 0x423 + 0x12B game sound
        /* 50 */ [],
        /* 51 */ [1060],
        /* 52 */ [61190],
        /* 53 */ [],
        /* 54 */ [],
        /* 55 */ [1061, 302, 1062],
        /* 56 */ [303],
        /* 57 */ [1063, 304],
        /* 58 */ [305, 1064],
        /* 59 */ [1065, 306],                  // 0x429 + 0x132 multi operate/priority
        /* 60 */ [20],
        /* 61 */ [1069, 307],
        /* 62 */ [1070],
        /* 63 */ [61191],
        /* 64 */ [61192],
        /* 65 */ [61193],
        /* 66 */ [1073, 308],
    ];

    /// <summary>Melody <c>f22960b</c> — 不依赖位图的常驻命令。</summary>
    public static readonly IReadOnlySet<ushort> AlwaysSupportedCommands =
        new HashSet<ushort>
        {
            256,  // 0x0100
            257,  // 0x0101
            258,  // 0x0102
            259,  // 0x0103
            260,  // 0x0104
            267,  // 0x010B
            3840, // 0x0F00
            3844, // 0x0F04
            269,  // 0x010D feature batch
            3843, // 0x0F03
            262,  // 0x0106 battery
        };

    public const int MaxBits = 67;

    // ---- 解析结果 ----
    public byte Status { get; private init; }
    public IReadOnlyList<byte> BitmapBytes { get; private init; } = [];
    public IReadOnlySet<ushort> SupportedCommands { get; private init; } = new HashSet<ushort>();
    public IReadOnlySet<int> SetBits { get; private init; } = new HashSet<int>();
    public bool IsValid => Status == 0 && BitmapBytes.Count > 0;

    public bool Supports(ushort command) => SupportedCommands.Contains(command);

    public bool SupportsAll(params ushort[] commands)
    {
        foreach (var c in commands)
        {
            if (!SupportedCommands.Contains(c))
                return false;
        }
        return true;
    }

    public bool SupportsAny(params ushort[] commands)
    {
        foreach (var c in commands)
        {
            if (SupportedCommands.Contains(c))
                return true;
        }
        return false;
    }

    // ---- 工厂 ----

    public static OppoCapabilityBitmap Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return Empty(0xFF);

        var status = payload[0];
        if (status != 0 || payload.Length <= 1)
            return Empty(status);

        var bytes = payload[1..].ToArray();
        var commands = new HashSet<ushort>(AlwaysSupportedCommands);
        var setBits = new HashSet<int>();
        var bitCount = Math.Min(Math.Min(bytes.Length * 8, MaxBits), CommandMap.Length);

        for (var bit = 0; bit < bitCount; bit++)
        {
            if ((bytes[bit / 8] & (1 << (bit % 8))) == 0)
                continue;
            setBits.Add(bit);
            foreach (var cmd in CommandMap[bit])
                commands.Add(cmd);
        }

        return new OppoCapabilityBitmap
        {
            Status = status,
            BitmapBytes = bytes,
            SupportedCommands = commands,
            SetBits = setBits,
        };
    }

    public static OppoCapabilityBitmap FromCommands(IReadOnlySet<ushort> commands)
    {
        var merged = new HashSet<ushort>(AlwaysSupportedCommands);
        foreach (var c in commands)
            merged.Add(c);
        return new OppoCapabilityBitmap
        {
            Status = 0,
            BitmapBytes = [],
            SupportedCommands = merged,
            SetBits = new HashSet<int>(),
        };
    }

    private static OppoCapabilityBitmap Empty(byte status) => new()
    {
        Status = status,
        BitmapBytes = [],
        SupportedCommands = new HashSet<ushort>(AlwaysSupportedCommands),
        SetBits = new HashSet<int>(),
    };

    // ---- 协议层功能门控（全程只在这里定义）----

    /// <summary>
    /// 位图/常驻命令是否具备该功能的协议入口。
    /// 不读 JSON 白名单；白名单求交见 <see cref="Resolve"/>。
    /// </summary>
    public bool ProtocolAllows(DeviceFeature feature) => feature switch
    {
        DeviceFeature.Battery => Supports(OppoCommandIds.QueryBattery),
        DeviceFeature.Anc => SupportsAll(OppoCommandIds.QueryAnc, OppoCommandIds.SetAnc),
        DeviceFeature.Equalizer => SupportsAll(OppoCommandIds.QueryEq, OppoCommandIds.SetEq),

        // 空间：V2 命令 或 通用开关（V1 还需白名单 SpatialTypes，在 Resolve 里处理）
        DeviceFeature.SpatialAudio =>
            Supports(OppoCommandIds.QuerySpatialAudio)
            || Supports(OppoCommandIds.SetSpatialAudio)
            || Supports(OppoCommandIds.SetFeature),

        DeviceFeature.Gaming => Supports(OppoCommandIds.SetFeature),
        DeviceFeature.GameSound =>
            Supports(OppoCommandIds.QueryGameSound) || Supports(OppoCommandIds.SetGameSound),

        DeviceFeature.MultiDevice => Supports(OppoCommandIds.QueryMultiDevice),
        DeviceFeature.DualDevice => Supports(OppoCommandIds.SetFeature),
        DeviceFeature.FindDevice => Supports(OppoCommandIds.SetFindDevice),

        // 专用命令 — 禁止 0x0403 回退
        DeviceFeature.BassEngine => Supports(OppoCommandIds.SetBassEngine), // 0x041B
        DeviceFeature.HearingEnhance =>
            Supports(OppoCommandIds.QueryHearingEnhance) // 0x0115 数据
            && Supports(OppoCommandIds.SetHearingDetect), // 0x040D 检测

        DeviceFeature.VocalEnhance
            or DeviceFeature.LongPowerMode
            or DeviceFeature.WearDetection
            or DeviceFeature.TouchControls =>
            Supports(OppoCommandIds.SetFeature),

        // 脊柱：legacy 默认关；即使位图有相关命令也不开 UI
        DeviceFeature.SpineHealth => false,

        DeviceFeature.FirmwareUpdate =>
            Supports(OppoCommandIds.QueryVersion) || Supports(OppoCommandIds.SetFeature),

        _ => false,
    };

    // ---- 白名单 ∩ 位图 → DeviceCapabilities（全程只在这里做）----

    /// <summary>
    /// 用本解析结果与静态白名单求交，产出最终 UI/协议能力。
    /// </summary>
    public DeviceCapabilities Resolve(DeviceCapabilities whitelist)
    {
        var staticFeatures = whitelist.Features;
        var features = new HashSet<DeviceFeature>();

        void Take(DeviceFeature f)
        {
            if (staticFeatures.Contains(f) && ProtocolAllows(f))
                features.Add(f);
        }

        Take(DeviceFeature.Battery);
        Take(DeviceFeature.Anc);
        Take(DeviceFeature.Equalizer);
        Take(DeviceFeature.Gaming);
        Take(DeviceFeature.GameSound);
        Take(DeviceFeature.MultiDevice);
        Take(DeviceFeature.FindDevice);
        Take(DeviceFeature.BassEngine);
        Take(DeviceFeature.HearingEnhance);
        Take(DeviceFeature.VocalEnhance);
        Take(DeviceFeature.LongPowerMode);
        Take(DeviceFeature.WearDetection);
        Take(DeviceFeature.TouchControls);
        Take(DeviceFeature.FirmwareUpdate);
        // SpineHealth: ProtocolAllows 恒 false

        // 双设备开关：白名单 Dual/Multi/Connect 版本 + SetFeature
        var dualWhitelisted = staticFeatures.Contains(DeviceFeature.DualDevice)
            || staticFeatures.Contains(DeviceFeature.MultiDevice)
            || whitelist.MultiDevicesConnect >= 1;
        if (dualWhitelisted && ProtocolAllows(DeviceFeature.DualDevice))
            features.Add(DeviceFeature.DualDevice);

        // 空间：白名单 SpatialTypes + 协议入口 → V2 或 V1
        var spatial = whitelist.WithSpatialProtocol(SupportedCommands);
        if (staticFeatures.Contains(DeviceFeature.SpatialAudio))
        {
            if (spatial.HasSpatialAudio || spatial.HasSpatialSound)
            {
                features.Add(DeviceFeature.SpatialAudio);
            }
            else if (whitelist.SpatialTypes.Count > 0 && Supports(OppoCommandIds.SetFeature))
            {
                // 有类型列表、无 0x012A → 旧版开关
                features.Add(DeviceFeature.SpatialAudio);
                spatial = whitelist with { HasSpatialSound = true, HasSpatialAudio = false };
            }
        }

        var supportsCustom = whitelist.SupportsCustomEqualizer
            && features.Contains(DeviceFeature.Equalizer)
            && Supports(OppoCommandIds.QueryEqualizerDetails);

        var supportsMulti = whitelist.SupportsMultiDevice
            && features.Contains(DeviceFeature.MultiDevice)
            && (Supports(OppoCommandIds.OperateMultiDevice)
                || whitelist.MultiDevicesConnect >= 1
                || Supports(OppoCommandIds.QueryMultiDevice));

        return new DeviceCapabilities(
            features,
            SupportsCustomEqualizer: supportsCustom,
            SupportsMultiDevice: supportsMulti,
            EqualizerBandCount: features.Contains(DeviceFeature.Equalizer)
                ? whitelist.EqualizerBandCount
                : null,
            EqualizerPresets: features.Contains(DeviceFeature.Equalizer)
                ? whitelist.EqualizerPresets
                : null,
            AncModes: features.Contains(DeviceFeature.Anc) ? whitelist.AncModes : null,
            SpatialModes: features.Contains(DeviceFeature.SpatialAudio)
                ? whitelist.SpatialModes
                : null)
        {
            ModelName = whitelist.ModelName,
            ModelId = whitelist.ModelId,
            DeviceName = whitelist.DeviceName,
            IsSupported = whitelist.IsSupported,
            ProtocolType = whitelist.ProtocolType,
            SupportSpp = whitelist.SupportSpp,
            GameSoundType = whitelist.GameSoundType,
            GameModeFeature = whitelist.GameModeFeature,
            GameSoundMutexes = whitelist.GameSoundMutexes,
            CustomEqFrequencies = supportsCustom ? whitelist.CustomEqFrequencies : [],
            CustomEqMaxPresets = supportsCustom ? whitelist.CustomEqMaxPresets : 0,
            CustomEqUiVersion = whitelist.CustomEqUiVersion,
            SpatialTypes = whitelist.SpatialTypes,
            HasSpatialAudio = spatial.HasSpatialAudio,
            HasSpatialSound = spatial.HasSpatialSound,
            MultiDevicesConnect = whitelist.MultiDevicesConnect,
            HasMultiConnectManage = supportsMulti && whitelist.HasMultiConnectManage,
            IsLegacyAnc = whitelist.IsLegacyAnc,
            HasAdaptiveAnc = whitelist.HasAdaptiveAnc,
            AncOptions = features.Contains(DeviceFeature.Anc) ? whitelist.AncOptions : [],
            AncIndexToName = features.Contains(DeviceFeature.Anc)
                ? whitelist.AncIndexToName
                : new Dictionary<byte, string>(),
        };
    }

    // ---- 诊断 ----

    public string BitsString()
    {
        if (BitmapBytes.Count == 0)
            return "";
        var bits = new char[BitmapBytes.Count * 8];
        for (var bit = 0; bit < bits.Length; bit++)
            bits[bit] = (BitmapBytes[bit / 8] & (1 << (bit % 8))) != 0 ? '1' : '0';
        return new string(bits);
    }

    public string BitmapHex() =>
        BitmapBytes.Count == 0
            ? "(empty)"
            : string.Join("-", BitmapBytes.Select(b => b.ToString("X2")));

    public void TraceLog()
    {
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 能力位图 status={Status} bits={SetBits.Count}/{MaxBits} hex={BitmapHex()}");
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 能力位串(bit0→)={BitsString()}");
        Trace.WriteLine(
            $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 能力命令 {SupportedCommands.Count} 条: " +
            string.Join(",", SupportedCommands.OrderBy(c => c).Select(c => $"0x{c:X4}")));
        if (SetBits.Count > 0)
        {
            Trace.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff} [RFCOMM] 置位 bit=[{string.Join(",", SetBits.OrderBy(b => b))}]");
        }
    }

    public static string BitmapBits(ReadOnlySpan<byte> bitmapWithoutStatus) =>
        Parse([0x00, .. bitmapWithoutStatus.ToArray()]).BitsString();
}
