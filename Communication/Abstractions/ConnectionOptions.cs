namespace OppoPodsManager.Communication.Abstractions;

public sealed record ConnectionOptions(
    string Transport,
    Guid? ServiceId,
    int Priority,
    IReadOnlyDictionary<string, string>? Parameters = null);
