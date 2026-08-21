using OppoPodsManager.Control.Core.Transport;
using System.Collections.Generic;
using System.Linq;
namespace OppoPodsManager.Control.Subsystems.Gestures;
/// <summary>
/// OPPO 触控能力档案（Enco Free4 真机抓包确认）。
///
/// 线格式（设备实测）：GET <see cref="CommandId.KeyFunction"/>=0x0108 / SET <see cref="CommandId.SetKeyFunction"/>=0x0408，
/// 负载 = <c>[count_hi, count_lo]</c> + count × 4 字节帧，每帧 <c>[deviceType, button, buttonAction, function]</c>，
/// 与官方 <c>KeyFunctionInfo.convertToData</c> 字节序一致：
///   deviceType  : 01=左耳 / 02=右耳（OPPO 用 1-based）
///   button      : 手势物理键（01=主触控区, 06=柄/长按键）
///   buttonAction: 同键下细分动作（01..06）
///   function    : 绑定功能字节
///
/// OPPO 的下发是「整表」：SET 一次写入全部 (耳, 键, 动作) 槽位，故管理器采用 读当前表 → 改一帧 → 整表回写 的方式。
/// </summary>
public sealed class OppoGestureProfile : IGestureProfile
{
    // OPPO 同时支持主触控区(Touch, button=01) 与 柄(Stem, button=06) 两套物理输入。
    public IReadOnlyList<GestureSource> SupportedSources { get; } = new[] { GestureSource.Touch, GestureSource.Stem };
    public IReadOnlyList<TapKind> GetSupportedGestures(GestureSource source)
    {
        var list = new List<TapKind>();
        foreach (var kv in Slot)
            if (kv.Key.Source == source)
                list.Add(kv.Key.Kind);
        return list;
    }
    public bool IsGestureConfigurable(TapKind kind, GestureSource source) => true;
    /// <summary>单条触控帧（4 字节，与官方 KeyFunctionInfo 一致）。</summary>
    public readonly struct KeyFunctionFrame
    {
        public readonly byte DeviceType;   // 01=左 / 02=右
        public readonly byte Button;        // 01=主触控区 / 06=柄
        public readonly byte ButtonAction;  // 01..06
        public readonly byte Function;      // 绑定功能
        public KeyFunctionFrame(byte deviceType, byte button, byte buttonAction, byte function)
            => (DeviceType, Button, ButtonAction, Function) = (deviceType, button, buttonAction, function);
        public byte[] ToBytes() => new[] { DeviceType, Button, ButtonAction, Function };
    }
    // (控制源, 手势) → (button, buttonAction) 映射。依据欢律(HeyMelody, com.heytap.headset) 逆向结果：
    //   · 官方 KeyFunctionInfo 帧 = (deviceType, deviceButton, buttonAction, function)，与本项目模型逐字段吻合；
    //   · App 手势引导资源分 click(单击)/press(按压·按捏)/slide(滑动) 三家族，对应 button=01(主触控区) 与 06(柄)；
    //   · 真机抓包(Enco Free4)：主触控区 6 槽 act1-6，柄 3 槽 act2/3/6。
    // 主触控区(click+slide 家族)：act1-5 = 单击/双击/三击/长按/滑动；act6 出厂默认 None 且 Enco Free4 触控区无「按压」手势，
    //   视为保留槽位不暴露（不归类为 Press）。柄(press 家族)：act2/3/6 = 两次按捏/三次按捏/按捏(长捏)，
    //   其中 act6 对应 App「按压手势」家族的柄主键。核对后仅改此一处即可全局生效。
    private static readonly Dictionary<(GestureSource Source, TapKind Kind), (byte Button, byte Action)> Slot = new()
    {
        [(GestureSource.Touch, TapKind.Single)] = (0x01, 0x01),
        [(GestureSource.Touch, TapKind.Double)] = (0x01, 0x02),
        [(GestureSource.Touch, TapKind.Triple)] = (0x01, 0x03),
        [(GestureSource.Touch, TapKind.LongPress)] = (0x01, 0x04),
        [(GestureSource.Touch, TapKind.Slide)] = (0x01, 0x05),
        [(GestureSource.Stem, TapKind.Double)] = (0x06, 0x02),
        [(GestureSource.Stem, TapKind.Triple)] = (0x06, 0x03),
        [(GestureSource.Stem, TapKind.Press)] = (0x06, 0x06),
    };
    private static readonly Dictionary<(byte Button, byte Action), (GestureSource Source, TapKind Kind)> SlotInverse = Invert(Slot);
    // 逻辑动作 → function 字节。真机回读 00/01/05/07/08 已与逻辑动作对齐；1C/1D 为柄键(button=06)噪声控制变体（Enco Free4 实测）。
    private static readonly Dictionary<GestureActionKind, byte> FunctionCode = new()
    {
        [GestureActionKind.None] = 0x00,
        [GestureActionKind.PlayPause] = 0x01,
        [GestureActionKind.Previous] = 0x02,
        [GestureActionKind.Next] = 0x03,
        [GestureActionKind.VoiceAssistant] = 0x04,
        [GestureActionKind.VolumeUp] = 0x05,
        [GestureActionKind.VolumeDown] = 0x06,
        [GestureActionKind.NoiseControlToggle] = 0x07,
        [GestureActionKind.AmbientToggle] = 0x08,
        [GestureActionKind.GameMode] = 0x11,
        // 柄键（button=06）专用噪声控制功能（Enco Free4 实测 1C/1D）。
        [GestureActionKind.QuickAttention] = 0x1C,
        [GestureActionKind.Translate] = 0x1D,
    };
    private static readonly Dictionary<byte, GestureActionKind> FunctionInverse = Invert(FunctionCode);
    private static Dictionary<TVal, TKey> Invert<TKey, TVal>(IReadOnlyDictionary<TKey, TVal> map)
        where TKey : notnull
        where TVal : notnull
    {
        var d = new Dictionary<TVal, TKey>();
        foreach (var kv in map)
            d[kv.Value] = kv.Key;
        return d;
    }
    // 柄(Stem) 可触发的动作集合：实测出厂默认含 翻译(1D)/快速聆听(1C)，故单独给出较宽集合。
    private static readonly GestureActionKind[] StemOptions = new[]
    {
        GestureActionKind.None,
        GestureActionKind.PlayPause,
        GestureActionKind.Previous,
        GestureActionKind.Next,
        GestureActionKind.VoiceAssistant,
        GestureActionKind.NoiseControlToggle,
        GestureActionKind.AmbientToggle,
        GestureActionKind.GameMode,
        GestureActionKind.QuickAttention,
        GestureActionKind.Translate,
    };
    public IReadOnlyList<GestureActionOption> GetActionOptions(TapKind kind, EarSide ear, GestureSource source)
    {
        // 与各手势支持的动作集合一致（与 FunctionCode 包含的键保持一致，保证编码可映射）。
        if (source == GestureSource.Stem)
        {
            return StemOptions
                .Select(a => new GestureActionOption(a, GestureDisplay.KeyFor(a)))
                .ToList();
        }
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
                // 官方 App 滑动选项：无 / 音量调节 / 歌曲切换（整体动作，方向隐式：上=音量+/下一曲，下=音量-/上一曲）。
                // 注意：后端编码（TryEncodeFunction）暂未实现 VolumeControl/SongSwitch，选后不下发，待协议核对后补齐。
                GestureActionKind.None, GestureActionKind.VolumeControl, GestureActionKind.SongSwitch,
            },
            TapKind.LongPress => new[]
            {
                // 官方 App 长按选项：无 / 切换噪声控制（通透模式不是长按动作，已移除）。
                GestureActionKind.None, GestureActionKind.NoiseControlToggle,
            },
            _ => new[] { GestureActionKind.None },
        };
        return actions
            .Select(a => new GestureActionOption(a, GestureDisplay.KeyFor(a)))
            .ToList();
    }
    /// <summary>解析 GET 0x0108 整表：支持 [count_hi,count_lo]+N×4 帧；若长度不符则容错为纯 N×4 帧。</summary>
    public static bool DecodeTable(byte[] payload, out List<KeyFunctionFrame> frames)
    {
        frames = new List<KeyFunctionFrame>();
        if (payload is not { Length: >= 2 })
            return false;
        int count = (payload[0] << 8) | payload[1];
        int offset;
        if (count * 4 + 2 == payload.Length)
        {
            offset = 2;
        }
        else
        {
            count = payload.Length / 4; // 无计数头：直接按 4 字节分帧
            offset = 0;
        }
        for (int i = 0; i < count && offset + 4 <= payload.Length; i++)
        {
            frames.Add(new KeyFunctionFrame(payload[offset], payload[offset + 1], payload[offset + 2], payload[offset + 3]));
            offset += 4;
        }
        return frames.Count > 0;
    }
    /// <summary>编码整表为下发负载（含 2 字节计数头）。</summary>
    public static byte[] EncodeTable(IReadOnlyList<KeyFunctionFrame> frames)
    {
        var outBytes = new List<byte>(frames.Count * 4 + 2)
        {
            (byte)(frames.Count >> 8),
            (byte)(frames.Count & 0xFF),
        };
        foreach (var f in frames)
            outBytes.AddRange(f.ToBytes());
        return outBytes.ToArray();
    }
    /// <summary>在整表中定位 (耳, 控制源, 手势) 对应的帧下标；找不到返回 false（映射待核对）。</summary>
    public static bool TryFindSlot(IReadOnlyList<KeyFunctionFrame> frames, EarSide ear, GestureSource source, TapKind kind, out int index)
    {
        index = -1;
        if (!Slot.TryGetValue((source, kind), out var slot))
            return false;
        byte deviceType = ear == EarSide.Left ? (byte)0x01 : (byte)0x02;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].DeviceType == deviceType && frames[i].Button == slot.Button && frames[i].ButtonAction == slot.Action)
            {
                index = i;
                return true;
            }
        }
        return false;
    }
    /// <summary>由帧解析 (耳, 控制源, 手势, 逻辑动作)；任一无法识别时对应字段为 null（不再误判为 Single）。</summary>
    public static bool DecodeFrame(KeyFunctionFrame frame, out EarSide? ear, out GestureSource? source, out TapKind? kind, out GestureActionKind? action)
    {
        ear = frame.DeviceType == 0x01 ? EarSide.Left : EarSide.Right;
        // 关键修复：未命中时 out 参数保持 null，避免 SlotInverse 默认回退到 default(TapKind)=Single 造成误显。
        if (SlotInverse.TryGetValue((frame.Button, frame.ButtonAction), out var sk))
        {
            source = sk.Source;
            kind = sk.Kind;
        }
        else
        {
            source = null;
            kind = null;
        }
        FunctionInverse.TryGetValue(frame.Function, out var a);
        action = a;
        return kind.HasValue && action.HasValue;
    }
    /// <summary>function 字节 → 逻辑动作（GET 回读用）。</summary>
    public static bool TryResolveFunction(byte function, out GestureActionKind action)
        => FunctionInverse.TryGetValue(function, out action);
    /// <summary>逻辑动作 → function 字节（SET 下发用）。</summary>
    public static bool TryEncodeFunction(GestureActionKind action, out byte function)
        => FunctionCode.TryGetValue(action, out function);
    // ---- IGestureProfile 兼容：单手势编码（管理器改用整表方式；此方法保留供参考 / 单帧诊断）----
    public byte[]? EncodeSet(EarSide ear, TapKind kind, GestureActionKind action, GestureSource source, byte? otherEarRaw = null)
    {
        if (!Slot.TryGetValue((source, kind), out var slot))
            return null;
        if (!FunctionCode.TryGetValue(action, out var function))
            return null;
        byte deviceType = ear == EarSide.Left ? (byte)0x01 : (byte)0x02;
        return new[] { deviceType, slot.Button, slot.Action, function };
    }
}
