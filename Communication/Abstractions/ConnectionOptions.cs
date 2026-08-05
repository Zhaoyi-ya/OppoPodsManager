namespace OppoPodsManager.Communication.Abstractions;

public sealed record ConnectionOptions(
    string Transport,
    Guid? ServiceId,
    int Channel);
