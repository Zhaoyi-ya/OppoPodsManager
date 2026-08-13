using System.Collections.Generic;
using System.Linq;

namespace OppoPodsManager.Control.Gestures;

/// <summary>
/// OPPO 触控能力档案。OPPO 官方 App 支持单击/双击/三击/滑动/长按五种手势，故 SupportedGestures 含全部。
/// 各手势动作集按 OPPO 惯例声明（左右耳相同集合，实际以真机为准）。
///
/// ⚠ EncodeSet 当前返回 null：OPPO 触控命令字（0x0102 系双击/长按 SET）尚未在项目内或参考资料中逆向到，
/// 待补命令字后实现真实下发。UI 仍会正确显示 OPPO 五手势与动作，但保存暂不生效。
/// </summary>
public sealed class OppoGestureProfile : IGestureProfile
{
    public IReadOnlyList<TapKind> SupportedGestures { get; } = new[]
    {
        TapKind.Single,
        TapKind.Double,
        TapKind.Triple,
        TapKind.Slide,
        TapKind.LongPress,
    };

    public bool IsGestureConfigurable(TapKind kind) => true;

    public IReadOnlyList<GestureActionOption> GetActionOptions(TapKind kind, EarSide ear)
    {
        GestureActionKind[] actions = kind switch
        {
            TapKind.Single => new[] { GestureActionKind.None, GestureActionKind.PlayPause },
            TapKind.Double => new[]
            {
                GestureActionKind.None, GestureActionKind.PlayPause, GestureActionKind.Previous,
                GestureActionKind.Next, GestureActionKind.VoiceAssistant, GestureActionKind.GameMode,
            },
            TapKind.Triple => new[]
            {
                GestureActionKind.Previous, GestureActionKind.Next,
                GestureActionKind.VoiceAssistant, GestureActionKind.GameMode,
            },
            TapKind.Slide => new[]
            {
                GestureActionKind.None, GestureActionKind.VolumeUp, GestureActionKind.VolumeDown,
            },
            TapKind.LongPress => new[]
            {
                GestureActionKind.NoiseControlToggle, GestureActionKind.AmbientToggle,
            },
            _ => new[] { GestureActionKind.None },
        };

        return actions
            .Select(a => new GestureActionOption(a, GestureDisplay.KeyFor(a)))
            .ToList();
    }

    public byte[]? EncodeSet(EarSide ear, TapKind kind, GestureActionKind action, byte? otherEarRaw = null)
    {
        // TODO: OPPO 触控命令字未逆向（项目内 CommandId.cs 无 tap/gesture；TWS-Pods-PC 也无 OPPO 触控资料）。
        // 待补 0x0102 系 SET 命令字后，将 (ear, kind, action) 翻译为 OPPO 字节下发。
        return null;
    }
}
