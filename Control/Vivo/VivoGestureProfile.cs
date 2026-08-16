using System.Collections.Generic;
using System.Linq;

namespace OppoPodsManager.Control.Gestures;

/// <summary>
/// vivo 触控能力档案。vivo 协议层面只有「双击」(0x0102) 与「长按/三击」(0x0131) 两种可配置手势，
/// 单击与滑动无对应命令字，故 GetSupportedGestures(Touch) 仅含 Double / LongPress（三击复用 0x0131 type6，后续可拆出）。
/// 动作码体系：左耳 0x00~0x06、右耳 0x10~0x16（来自 VivoConstants.TapLeftCodes/RightCodes）；
/// 长按功能码 = 噪声模式码（来自 TWS-Pods-PC/vivo/vivo_protocol.py：NOISE_ALL=0x0B、EXCLUDE_OFF=0x0A、
/// EXCLUDE_TRANS=0x08、EXCLUDE_ANC=0x09、NONE=0xFF）。**绝非** 0x01/0x02/0x03（那是旧工程误写，
/// 会导致设备存储非法值、官方 App 读回空状态）。
/// </summary>
public sealed class VivoGestureProfile : IGestureProfile
{
    // 双击动作码 → 逻辑动作（左耳）
    private static readonly Dictionary<byte, GestureActionKind> LeftTapMap = new()
    {
        [0x00] = GestureActionKind.VoiceAssistant,
        [0x01] = GestureActionKind.PlayPause,
        [0x02] = GestureActionKind.Previous,
        [0x03] = GestureActionKind.Next,
        [0x05] = GestureActionKind.Translate,
        [0x06] = GestureActionKind.None,
    };

    // 双击动作码 → 逻辑动作（右耳）
    private static readonly Dictionary<byte, GestureActionKind> RightTapMap = new()
    {
        [0x10] = GestureActionKind.VoiceAssistant,
        [0x11] = GestureActionKind.PlayPause,
        [0x12] = GestureActionKind.Previous,
        [0x13] = GestureActionKind.Next,
        [0x15] = GestureActionKind.Translate,
        [0x16] = GestureActionKind.None,
    };

    // 长按功能码 → 逻辑动作（解码方向，供设备上报回填下拉）。
    // 长按功能码即噪声模式码：0xFF=无；0x0B/0x0A/0x08/0x09 均为「切换噪声控制」的不同循环集合
    // （全场景/排除关闭/排除通透/排除降噪），官方 App 长按左右耳下拉只给「无 / 切换噪声控制」两项，
    // 故解码侧把四个噪声码都归到 NoiseControlToggle，避免设备存了某预设时下拉显示空白。
    private static readonly Dictionary<byte, GestureActionKind> LongPressMap = new()
    {
        [0xFF] = GestureActionKind.None,
        [0x0B] = GestureActionKind.NoiseControlToggle,
        [0x0A] = GestureActionKind.NoiseControlToggle,
        [0x08] = GestureActionKind.NoiseControlToggle,
        [0x09] = GestureActionKind.NoiseControlToggle,
    };

    private static readonly Dictionary<GestureActionKind, byte> LeftTapInverse = Invert(LeftTapMap);
    private static readonly Dictionary<GestureActionKind, byte> RightTapInverse = Invert(RightTapMap);
    // 下发方向手动构建：切换噪声控制 → NOISE_ALL(0x0B)（出厂基线循环）；无 → NOISE_NONE(0xFF)。
    // 不用 Invert(LongPressMap)，否则重复值会被后者覆盖、下发错码。
    private static readonly Dictionary<GestureActionKind, byte> LongPressInverse = new()
    {
        [GestureActionKind.None] = 0xFF,
        [GestureActionKind.NoiseControlToggle] = 0x0B,
    };

    private static Dictionary<GestureActionKind, byte> Invert(IReadOnlyDictionary<byte, GestureActionKind> map)
    {
        var d = new Dictionary<GestureActionKind, byte>();
        foreach (var kv in map)
            d[kv.Value] = kv.Key;
        return d;
    }

    public IReadOnlyList<GestureSource> SupportedSources { get; } = new[] { GestureSource.Touch };

    public IReadOnlyList<TapKind> GetSupportedGestures(GestureSource source)
        => source == GestureSource.Touch ? new[] { TapKind.Double, TapKind.LongPress } : [];

    public bool IsGestureConfigurable(TapKind kind, GestureSource source) => true;

    public IReadOnlyList<GestureActionOption> GetActionOptions(TapKind kind, EarSide ear, GestureSource source)
    {
        IReadOnlyDictionary<byte, GestureActionKind> map = kind == TapKind.LongPress
            ? LongPressMap
            : (ear == EarSide.Left ? LeftTapMap : RightTapMap);

        return map.Values
            .Distinct()
            .Select(a => new GestureActionOption(a, GestureDisplay.KeyFor(a)))
            .ToList();
    }

    public byte[]? EncodeSet(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, byte? otherEarRaw = null)
    {
        if (kind == TapKind.LongPress)
        {
            // SET 0x0131 payload = [type, leftCode, rightCode]（type=5 长按 / 6 三击；APK m37720W(5,a,b)）。
            // 长按功能码 = 噪声模式码（无=0xFF、切换噪声控制=0x0B）。未设置的耳用 0xFF(无) 兜底；
            // 若另一耳传来的原始码非法（如历史误写的 0x01/0x02），也兜底为 0xFF，避免把坏值写回设备。
            if (!LongPressInverse.TryGetValue(action, out var func))
                return null;
            var otherRaw = otherEarRaw.HasValue && LongPressMap.ContainsKey(otherEarRaw.Value)
                ? otherEarRaw.Value
                : (byte)0xFF;
            var leftCode = ear == EarSide.Left ? func : otherRaw;
            var rightCode = ear == EarSide.Right ? func : otherRaw;
            return new byte[] { 0x05, leftCode, rightCode };
        }

        if (kind == TapKind.Double)
        {
            // SET 0x0102 payload = [动作码]（单字节；左右耳编码区间不同：0x00~0x06 / 0x10~0x16）
            var inverse = ear == EarSide.Left ? LeftTapInverse : RightTapInverse;
            return inverse.TryGetValue(action, out var code) ? new byte[] { code } : null;
        }

        return null;
    }

    /// <summary>将耳机上报的原始动作码翻译为逻辑动作（双击用）。</summary>
    public GestureActionKind? DecodeTap(EarSide ear, byte raw)
    {
        var map = ear == EarSide.Left ? LeftTapMap : RightTapMap;
        return map.TryGetValue(raw, out var a) ? a : null;
    }

    /// <summary>将耳机上报的原始功能码翻译为逻辑动作（长按用）。</summary>
    public GestureActionKind? DecodeLongPress(byte raw)
        => LongPressMap.TryGetValue(raw, out var a) ? a : null;
}
