using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析当前均衡器预设并写入业务状态。
public sealed class Equalizer
{
    private readonly BusinessState _state;
    private readonly DeviceCapability _capability;

    public Equalizer(BusinessState state, DeviceCapability capability)
    {
        _state = state;
        _capability = capability;
    }

    public void ApplyCurrentPreset(ReadOnlySpan<byte> payload)
    {
        ApplicationLog.Current?.Debug("Equalizer.Protocol", $"解析当前 EQ：bytes={payload.Length}。");
        if (payload.Length == 0)
        {
            ApplicationLog.Current?.Error("Equalizer.Protocol", "当前 EQ 响应为空。");
            return;
        }

        var offset = payload.Length > 1 && payload[0] == 0 ? 1 : 0;
        if (payload.Length <= offset)
            return;

        var presetId = payload[offset];
        var presetName = presetId < _capability.EqualizerPresets.Count
            ? _capability.EqualizerPresets[presetId]
            : null;
        ApplicationLog.Current?.Info("Equalizer.Protocol", $"当前 EQ 解析完成：id={presetId}，name={presetName ?? "unknown"}。");
        _state.SetEqualizer(new EqualizerSnapshot(presetId, presetName));
    }
}
