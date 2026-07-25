namespace OppoPodsManager.Core.Results;

/// <summary>
/// Structured failure for brand/session commands. UI can show <see cref="Message"/>;
/// diagnostics can use <see cref="Code"/>.
/// </summary>
public sealed record CommandFailure(string Message, string? Code = null)
{
    public static CommandFailure NotConnected() =>
        new("当前没有连接的耳机。", "not_connected");

    public static CommandFailure Unsupported(string feature) =>
        new($"设备不支持功能：{feature}", "unsupported");

    public static CommandFailure Protocol(string detail) =>
        new(detail, "protocol");
}