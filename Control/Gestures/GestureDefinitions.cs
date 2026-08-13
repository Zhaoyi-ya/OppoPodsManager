namespace OppoPodsManager.Control.Gestures;

/// <summary>触控手势种类。各品牌 Profile 声明自己支持哪些，UI 据此动态渲染行。</summary>
public enum TapKind
{
    Single,
    Double,
    Triple,
    Slide,
    LongPress,
}

/// <summary>耳侧。</summary>
public enum EarSide
{
    Left,
    Right,
}

/// <summary>长按手势在 UI 上的渲染形态。</summary>
public enum LongPressRenderMode
{
    /// <summary>单个 ComboBox 选择一项循环功能（OPPO/vivo 长按选「切换噪声控制」等）。</summary>
    CycleSet,
    /// <summary>多个独立 CheckBox（如 ANC/通透/关 各自开关）。</summary>
    MultiCheckbox,
    /// <summary>固定功能，UI 只读显示，不可配置。</summary>
    Fixed,
}

/// <summary>
/// 跨品牌共享的逻辑动作语义。UI 只认此枚举，具体协议字节由各品牌 Profile 翻译。
/// 未来华为/索尼接入时复用同一枚举，无需新增 UI 代码。
/// </summary>
public enum GestureActionKind
{
    None,
    PlayPause,
    Previous,
    Next,
    VoiceAssistant,
    GameMode,
    NoiseControlToggle,
    AmbientToggle,
    VolumeUp,
        VolumeDown,
        Translate,
        QuickAttention,
}

/// <summary>单个可选动作：逻辑语义 + 本地化 key（UI 显示文字）。</summary>
public sealed record GestureActionOption(GestureActionKind Kind, string DisplayKey);

/// <summary>触控面板上的一行：手势 + 耳侧 + 是否可配置 + 渲染形态 + 可选项 + 当前选中。</summary>
public sealed record GestureEntry(
    TapKind Kind,
    EarSide Ear,
    bool IsConfigurable,
    LongPressRenderMode LongPressMode,
    IReadOnlyList<GestureActionOption> Options,
    GestureActionKind Current);

/// <summary>
/// 品牌触控能力档案。声明支持哪些手势、每个手势可选哪些逻辑动作、以及如何编码下发。
/// UI 完全不知道品牌差异，只消费 <see cref="GetActionOptions"/> 返回的列表。
/// </summary>
public interface IGestureProfile
{
    /// <summary>该品牌支持的手势集合（决定 UI 渲染哪些行）。</summary>
    IReadOnlyList<TapKind> SupportedGestures { get; }

    /// <summary>某手势在某耳侧可配置的逻辑动作列表。</summary>
    IReadOnlyList<GestureActionOption> GetActionOptions(TapKind kind, EarSide ear);

    /// <summary>将 (耳, 手势, 逻辑动作) 编码为下发字节负载；不支持或命令字未实现时返回 null。
    /// <paramref name="otherEarRaw"/> 为另一耳当前原始值（长按等需要左右耳一同下发的命令使用）。</summary>
    byte[]? EncodeSet(EarSide ear, TapKind kind, GestureActionKind action, byte? otherEarRaw = null);

    /// <summary>该手势是否可配置（固定功能返回 false，UI 渲染为只读）。默认 true。</summary>
    bool IsGestureConfigurable(TapKind kind) => true;
}

/// <summary>逻辑动作 → 本地化 key 的共享映射（OPPO/vivo/华为 UI 共用同一套显示名）。</summary>
public static class GestureDisplay
{
    public static string KeyFor(GestureActionKind kind) => kind switch
    {
        GestureActionKind.None => "Gesture_None",
        GestureActionKind.PlayPause => "DeviceInfo_PlayPause",
        GestureActionKind.Previous => "DeviceInfo_Prev",
        GestureActionKind.Next => "DeviceInfo_Next",
        GestureActionKind.VoiceAssistant => "DeviceInfo_VoiceAssistant",
        GestureActionKind.GameMode => "Feature_GameMode",
        GestureActionKind.NoiseControlToggle => "Gesture_NoiseControlToggle",
        GestureActionKind.AmbientToggle => "Gesture_AmbientToggle",
        GestureActionKind.VolumeUp => "Gesture_VolumeUp",
        GestureActionKind.VolumeDown => "Gesture_VolumeDown",
        GestureActionKind.Translate => "Gesture_Translate",
        GestureActionKind.QuickAttention => "Gesture_QuickAttention",
        _ => "Gesture_" + kind,
    };
}
