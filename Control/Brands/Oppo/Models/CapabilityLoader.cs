using OppoPodsManager.Control.Core.Transport;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Brands.Oppo.Models;

// 合并产品 ID、本地型号库和动态命令表得到最终能力。
public sealed class CapabilityLoader
{
    private readonly ModelCatalog _catalog;

    public CapabilityLoader(ModelCatalog catalog)
    {
        _catalog = catalog;
    }

    // 优先按产品标识匹配，未识别型号时不猜测任何功能能力。
    public DeviceCapability Load(
        string? productId,
        string? deviceName,
        IReadOnlySet<ushort> supportedCommands,
        string? manualModel = null)
    {
        var model = !string.IsNullOrWhiteSpace(manualModel)
            ? _catalog.Find(null, manualModel)
            : _catalog.Find(productId, deviceName);

        if (model is null)
            return new DeviceCapability(
                productId ?? string.Empty,
                string.IsNullOrWhiteSpace(productId)
                    ? deviceName ?? string.Empty
                    : string.Empty,
                false,
                supportedCommands,
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<byte, NoiseMode>(),
                [],
                [],
                [],
                0,
                0,
                null,
                new HashSet<int>());

        var features = IntersectWhitelistFeatures(model.Features, supportedCommands);
        ResolveSpatialProtocol(features, supportedCommands);
        ApplicationLog.Current?.Debug(
            "Capability",
            $"型号能力交集：model={model.DisplayName}，commands={string.Join(',', supportedCommands.OrderBy(command => command).Select(command => $"0x{command:X4}"))}，features={string.Join(',', features.OrderBy(feature => feature))}。");
        return new DeviceCapability(
            model.ProductId,
            model.DisplayName,
            true,
            supportedCommands,
            features,
            model.NoiseModes,
            model.NoiseGroups,
            model.EqualizerPresets,
            model.CustomEqFrequencies,
            model.CustomEqMaxPresets,
            model.CustomEqUiVersion,
            model.PreferredGameSoundType,
            model.GameSoundMutexes,
            model.BatteryLayout);
    }

    // 设备最终只暴露“官方白名单声明且能力位图实际支持”的功能。
    private static HashSet<string> IntersectWhitelistFeatures(
        IReadOnlySet<string> whitelist,
        IReadOnlySet<ushort> commands)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in whitelist)
        {
            var supported = feature switch
            {
                "noise-cancellation" => HasAny(commands, CommandId.NoiseCancellation, CommandId.SetNoiseCancellation),
                "equalizer" => HasAny(commands, CommandId.CurrentEqualizer, CommandId.SetEqualizer),
                // 自定义 EQ：与 main 分支对齐——仅依据 JSON 白名单声明（customEqualizer 标志 + customEqFrequency 非空），
                // 不强制要求 0x0122/0x0418 出现在设备能力位图中。许多 OPPO 设备支持自定义 EQ 却不在位图声明这些命令。
                // 命令可用性在调用点由 CanUseCommand() 保底校验。
                "custom-equalizer" => true,
                "find-device" => commands.Contains(CommandId.SetFindDevice),
                "game-sound" => HasAny(commands, CommandId.GameSound, CommandId.SetGameSound),
                "spatial-configured" => HasAny(commands, CommandId.SpatialAudio, CommandId.SetSpatialAudio, CommandId.SetFeature),
                "multi-device" => HasAny(commands, CommandId.MultiDeviceInformation, CommandId.OperateMultiDevice),
                "dual-device" or "wear-detection" or "voice-enhancement"
                    or "long-battery" or "spatial-sound" or "game-mode"
                    => commands.Contains(CommandId.SetFeature),
                // JADX/实验项目确认：低音引擎使用 0x041B 专用设置命令。
                "bass-engine" => commands.Contains(CommandId.SetBassEngine),
                // 「柄」按压/按捏：白名单 supportPinch 声明 + 设备支持手势表命令。
                // 与主触控区共用 KeyFunction 0x0108/0x0408，命令任一存在即视为可显示。
                "stem" => HasAny(commands, CommandId.KeyFunction, CommandId.SetKeyFunction),
                // 听力增强必须同时有数据查询和检测流程命令。
                "hearing-enhancement" => commands.Contains(CommandId.HearingEnhancement)
                    && commands.Contains(CommandId.SetHearingEnhancement),
                // 官方桌面端没有可用的脊柱健康普通开关实现。
                "spine-health" => false,
                _ => false
            };

            if (supported)
                features.Add(feature);
        }

        return features;
    }

    private static bool HasAny(IReadOnlySet<ushort> commands, params ushort[] candidates)
        => candidates.Any(commands.Contains);

    // 官方白名单只声明空间能力，具体使用旧开关还是新三模式协议由运行时命令位图决定。
    private static void ResolveSpatialProtocol(ISet<string> features, IReadOnlySet<ushort> commands)
    {
        if (!features.Remove("spatial-configured"))
            return;

        if (commands.Contains(CommandId.SpatialAudio) && commands.Contains(CommandId.SetSpatialAudio))
            features.Add("spatial-audio");
        else if (commands.Contains(CommandId.SetFeature))
            features.Add("spatial-sound");
    }
}
