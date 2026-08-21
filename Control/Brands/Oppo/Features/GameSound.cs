using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.Control.Brands.Oppo.Features;

// 解析游戏音效类型响应。
public sealed class GameSound
{
    private readonly BusinessState _state;

    public GameSound(BusinessState state)
    {
        _state = state;
    }

    public bool Apply(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2 || payload[0] != 0)
            return false;

        var type = payload[1];
        _state.SetGame(_state.Snapshot().Game with { SoundType = type });
        ApplicationLog.Current?.Debug(
            "GameSound.Protocol",
            $"解析游戏音效状态：raw={Convert.ToHexString(payload)}，type={type}，enabled={type != 0}。 ");
        return true;
    }
}
