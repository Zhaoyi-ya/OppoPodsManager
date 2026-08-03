using OppoPodsManager.Control.Oppo.Commands;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control.Oppo.Models;

// 合并产品 ID、本地型号库和动态命令表得到最终能力。
public sealed class CapabilityLoader
{
    private readonly ModelCatalog _catalog;

    public CapabilityLoader(ModelCatalog catalog)
    {
        _catalog = catalog;
    }

    // 让品牌管理器复用已加载的官方型号目录。
    public ModelCatalog Catalog => _catalog;

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
            model.GameSoundMutexes);
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
                "custom-equalizer" => HasAny(commands, CommandId.EqualizerEntries, CommandId.SetEqualizerEntry),
                "find-device" => commands.Contains(CommandId.SetFindDevice),
                "game-sound" => HasAny(commands, CommandId.GameSound, CommandId.SetGameSound),
                "spatial-configured" => HasAny(commands, CommandId.SpatialAudio, CommandId.SetSpatialAudio, CommandId.SetFeature),
                "multi-device" => HasAny(commands, CommandId.MultiDeviceInformation, CommandId.OperateMultiDevice),
                "dual-device" or "wear-detection" or "voice-enhancement"
                    or "long-battery" or "spatial-sound" or "game-mode"
                    => commands.Contains(CommandId.SetFeature),
                // JADX/实验项目确认：低音引擎使用 0x041B 专用设置命令。
                "bass-engine" => commands.Contains(CommandId.SetBassEngine),
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
