using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Core.Features;

// 表示已完成协议分组的降噪界面选项，窗口只负责渲染和转发模式键。
public sealed record NoiseOptionModel(
    string Key,
    NoiseMode Mode,
    byte ProtocolIndex,
    IReadOnlyList<NoiseOptionModel> Children);
