namespace OppoPodsManager.Core.Devices;

/// <summary>
/// Hierarchical ANC option used by the desktop UI.
/// Parent modes may be non-sendable containers of child modes.
/// </summary>
public sealed class AncOption
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public byte ProtocolIndex { get; init; }
    public bool Sendable { get; init; } = true;
    public IReadOnlyList<AncOption> Children { get; init; } = [];
}
