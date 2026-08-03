using OppoPodsManager.Control.Oppo.Models;

namespace OppoPodsManager.Control.Oppo.Features;

// 解析空间音频模式响应。
public sealed class SpatialAudio
{
    private readonly BusinessState _state;

    public SpatialAudio(BusinessState state)
    {
        _state = state;
    }

    // 将界面模式标签转换为设备协议使用的空间音频枚举。
    public static SpatialAudioMode ParseMode(string value)
        => value switch
        {
            "Fixed" or "固定" => SpatialAudioMode.Fixed,
            "HeadTracking" or "头部跟踪" => SpatialAudioMode.HeadTracking,
            _ => SpatialAudioMode.Off
        };

    public void Apply(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
            return;

        var offset = payload.Length > 1 && payload[0] == 0 ? 1 : 0;
        if (payload.Length <= offset)
            return;

        var mode = payload[offset] switch
        {
            0 => SpatialAudioMode.Off,
            1 => SpatialAudioMode.Fixed,
            2 => SpatialAudioMode.HeadTracking,
            _ => SpatialAudioMode.Unknown
        };
        _state.SetSpatialAudio(new SpatialAudioSnapshot(mode));
    }
}
