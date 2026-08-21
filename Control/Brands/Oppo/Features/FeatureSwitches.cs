using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Core.Transport;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 维护通用功能开关的协议编号、查询负载和状态解析。
public sealed class FeatureSwitches
{
    public const byte WearDetection = global::OppoPodsManager.Control.Brands.Oppo.Features.WearDetection.FeatureId;
    public const byte LegacyGameMode = GameMode.LegacyFeatureId;
    public const byte VoiceEnhancement = global::OppoPodsManager.Control.Brands.Oppo.Features.VoiceEnhancement.FeatureId;
    public const byte HearingEnhancement = global::OppoPodsManager.Control.Brands.Oppo.Features.HearingEnhancement.FeatureId;
    public const byte DualDevice = global::OppoPodsManager.Control.Brands.Oppo.Features.DualDevice.FeatureId;
    public const byte LongBattery = global::OppoPodsManager.Control.Brands.Oppo.Features.LongBattery.FeatureId;
    public const byte SpatialSound = global::OppoPodsManager.Control.Brands.Oppo.Features.SpatialSound.FeatureId;
    public const byte BassEngine = global::OppoPodsManager.Control.Brands.Oppo.Features.BassEngine.FeatureId;
    public const byte SpineHealth = global::OppoPodsManager.Control.Brands.Oppo.Features.SpineHealth.FeatureId;
    public const byte GameSound = 0x27;
    public const byte ModernGameMode = GameMode.ModernFeatureId;
    private readonly BusinessState _state;

    public FeatureSwitches(BusinessState state)
    {
        _state = state;
    }

    public void Apply(ReadOnlySpan<byte> payload)
    {
        var values = new Dictionary<byte, bool>();
        // 响应头包含状态字节和功能对数量，功能数据从第二个字节之后开始。
        var pairCount = payload.Length >= 2 ? payload[1] : 0;
        var dataOffset = 2;
        for (var pair = 0; pair < pairCount && dataOffset + 1 < payload.Length; pair++, dataOffset += 2)
            values[payload[dataOffset]] = payload[dataOffset + 1] != 0;

        ApplicationLog.Current?.Debug(
            "Capability",
            $"0x810D 功能状态：raw={Convert.ToHexString(payload)}，featureIds={string.Join(',', values.Keys.Select(value => $"0x{value:X2}"))}。");

        _state.SetFeatureStates(new FeatureStateSnapshot(values));
        if (GameMode.TryRead(_state.Snapshot().FeatureStates, out var gameMode))
            _state.SetGame(_state.Snapshot().Game with { IsEnabled = gameMode });
    }

    // 设备已响应功能探针时只记录诊断信息，不用开关当前值改变功能显隐。
    public static DeviceCapability RefineCapability(DeviceCapability capability, FeatureStateSnapshot states)
    {
        var reported = states.Values.Keys.ToHashSet();
        ApplicationLog.Current?.Debug(
            "Capability",
            $"0x810D 状态回报：reported={string.Join(',', reported.OrderBy(value => value).Select(value => $"0x{value:X2}"))}，显隐保持能力位图与白名单交集。 ");
        return capability;
    }

    // 按官方优先级选择游戏模式协议：带游戏音效的新型号优先 0x28，否则使用旧版 0x06。
    public static byte? ResolveGameModeFeature(DeviceCapability capability, FeatureStateSnapshot states)
        => GameMode.ResolveFeatureId(capability, states);

    // 判断通用开关是否同时满足白名单、协议能力和设备实际回报条件。
    public static bool IsControlAvailable(
        DeviceCapability capability,
        FeatureStateSnapshot states,
        string featureName,
        byte featureId)
        => SupportsProtocol(capability, featureName)
            && states.Values.ContainsKey(featureId);

    // 集中判断每个控件实际使用的协议入口，避免专用功能退化成 0x0403。
    private static bool SupportsProtocol(DeviceCapability capability, string featureName)
        => featureName switch
        {
            "dual-device" => global::OppoPodsManager.Control.Brands.Oppo.Features.DualDevice.IsSupported(capability),
            "bass-engine" => global::OppoPodsManager.Control.Brands.Oppo.Features.BassEngine.IsSupported(capability),
            "voice-enhancement" => global::OppoPodsManager.Control.Brands.Oppo.Features.VoiceEnhancement.IsSupported(capability),
            "hearing-enhancement" => global::OppoPodsManager.Control.Brands.Oppo.Features.HearingEnhancement.IsSupported(capability),
            "long-battery" => global::OppoPodsManager.Control.Brands.Oppo.Features.LongBattery.IsSupported(capability),
            "wear-detection" => global::OppoPodsManager.Control.Brands.Oppo.Features.WearDetection.IsSupported(capability),
            "spine-health" => global::OppoPodsManager.Control.Brands.Oppo.Features.SpineHealth.IsSupported(capability),
            "spatial-sound" => global::OppoPodsManager.Control.Brands.Oppo.Features.SpatialSound.IsSupported(capability),
            _ => capability.SupportsCommand(CommandId.SetFeature)
                && capability.SupportsFeature(featureName)
        };

    // 汇总当前设备最终可显示的功能控件，只使用白名单和能力位图交集。
    public static IReadOnlySet<string> ResolveVisibleControls(
        DeviceCapability capability,
        FeatureStateSnapshot states)
    {
        var visible = new HashSet<string>(StringComparer.Ordinal);
        var controls = new (string Name, byte Id)[]
        {
            ("dual-device", DualDevice),
            ("bass-engine", BassEngine),
            ("voice-enhancement", VoiceEnhancement),
            ("hearing-enhancement", HearingEnhancement),
            ("long-battery", LongBattery),
            ("wear-detection", WearDetection),
            ("spine-health", SpineHealth),
            ("spatial-sound", SpatialSound)
        };

        foreach (var control in controls)
        {
            if (SupportsProtocol(capability, control.Name))
                visible.Add(control.Name);
        }

        if (capability.SupportsFeature("game-sound")
            && capability.SupportsCommand(CommandId.SetGameSound))
            visible.Add("game-sound");

        if (capability.SupportsFeature("game-mode")
            && capability.SupportsCommand(CommandId.SetFeature))
            visible.Add("game-mode");

        if (FindDevice.IsSupported(capability))
            visible.Add("find-device");

        return visible;
    }

    // 将设备功能编号状态转换为界面可消费的业务名称和值。
    public static IReadOnlyDictionary<string, bool> ResolveControlStates(FeatureStateSnapshot states)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        AddState(result, states, "dual-device", DualDevice);
        AddState(result, states, "bass-engine", BassEngine);
        AddState(result, states, "voice-enhancement", VoiceEnhancement);
        AddState(result, states, "hearing-enhancement", HearingEnhancement);
        AddState(result, states, "long-battery", LongBattery);
        AddState(result, states, "wear-detection", WearDetection);
        AddState(result, states, "spine-health", SpineHealth);
        AddState(result, states, "spatial-sound", SpatialSound);

        if (GameMode.TryRead(states, out var gameModeEnabled))
            result["game-mode"] = gameModeEnabled;
        return result;
    }

    // 根据设备互斥能力和当前状态计算控件是否允许操作。
    public static IReadOnlyDictionary<string, bool> ResolveControlEnabledStates(
        DeviceCapability capability,
        FeatureStateSnapshot states,
        GameSnapshot game)
    {
        var visible = ResolveVisibleControls(capability, states);
        var spatialSoundEnabled = states.TryGetValue(SpatialSound, out var spatialEnabled) && spatialEnabled;
        var gameSoundEnabled = game.SoundType is > 0;
        var blocksSpatialSound = capability.GameSoundBlocksSpatialSound;
        var blocksEqualizer = capability.GameSoundBlocksEqualizer;
        var equalizerAvailable = capability.SupportsCustomEqualizer
            || capability.EqualizerPresets.Count > 0
                && capability.SupportsCommand(CommandId.SetEqualizer)
                && capability.SupportsCommand(CommandId.CurrentEqualizer);

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var name in new[]
        {
            "dual-device",
            "bass-engine",
            "voice-enhancement",
            "hearing-enhancement",
            "long-battery",
            "wear-detection",
            "spine-health",
            "spatial-sound",
            "game-sound",
            "game-mode",
            "find-device"
        })
            result[name] = visible.Contains(name);

        result["equalizer"] = equalizerAvailable;
        result["game-sound"] = result["game-sound"]
            && (!spatialSoundEnabled || !blocksSpatialSound);
        result["spatial-sound"] = result["spatial-sound"]
            && (!gameSoundEnabled || !blocksSpatialSound);
        result["equalizer"] = result["equalizer"]
            && (!gameSoundEnabled || !blocksEqualizer);
        return result;
    }

    private static void AddState(
        IDictionary<string, bool> result,
        FeatureStateSnapshot states,
        string name,
        byte featureId)
    {
        if (states.TryGetValue(featureId, out var value))
            result[name] = value;
    }

    public static byte[] BuildQuery(DeviceCapability capability)
    {
        var features = new List<byte> { 0x05, LegacyGameMode, ModernGameMode };
        AddWhenSupported(features, capability, "wear-detection", WearDetection);
        AddWhenSupported(features, capability, "voice-enhancement", VoiceEnhancement);
        AddWhenSupported(features, capability, "hearing-enhancement", HearingEnhancement);
        AddWhenSupported(features, capability, "dual-device", DualDevice);
        AddWhenSupported(features, capability, "long-battery", LongBattery);
        AddWhenSupported(features, capability, "spatial-sound", SpatialSound);
        AddWhenSupported(features, capability, "bass-engine", BassEngine);
        AddWhenSupported(features, capability, "spine-health", SpineHealth);
        AddWhenSupported(features, capability, "game-sound", GameSound);

        var payload = new byte[features.Count + 1];
        // 首字节是后续功能对数量，0x05 也是实际查询的功能编号，必须计入数量。
        payload[0] = (byte)features.Count;
        features.CopyTo(payload, 1);
        ApplicationLog.Current?.Debug(
            "Capability",
            $"0x010D 功能查询：count={payload[0]}，featureIds={string.Join(',', features.Select(value => $"0x{value:X2}"))}。");
        return payload;
    }

    // 按功能编号调用对应功能类构造通用开关负载。
    public static byte[] BuildPayload(byte featureId, bool enabled)
        => featureId switch
        {
            DualDevice => global::OppoPodsManager.Control.Brands.Oppo.Features.DualDevice.BuildPayload(enabled),
            VoiceEnhancement => global::OppoPodsManager.Control.Brands.Oppo.Features.VoiceEnhancement.BuildPayload(enabled),
            HearingEnhancement => global::OppoPodsManager.Control.Brands.Oppo.Features.HearingEnhancement.BuildFeaturePayload(enabled),
            LongBattery => global::OppoPodsManager.Control.Brands.Oppo.Features.LongBattery.BuildPayload(enabled),
            WearDetection => global::OppoPodsManager.Control.Brands.Oppo.Features.WearDetection.BuildPayload(enabled),
            SpatialSound => global::OppoPodsManager.Control.Brands.Oppo.Features.SpatialSound.BuildPayload(enabled),
            SpineHealth => global::OppoPodsManager.Control.Brands.Oppo.Features.SpineHealth.BuildPayload(enabled),
            _ => [featureId, enabled ? (byte)1 : (byte)0]
        };

    private static void AddWhenSupported(List<byte> features, DeviceCapability capability, string name, byte featureId)
    {
        if (capability.SupportsFeature(name))
            features.Add(featureId);
    }

}
