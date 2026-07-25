namespace OppoPodsManager.Core.Results;

public sealed record OperationResult(bool Succeeded, string? Error = null, string? Code = null)
{
    public static OperationResult Success() => new(true);

    public static OperationResult Failure(string error) => new(false, error);

    public static OperationResult Failure(CommandFailure failure) =>
        new(false, failure.Message, failure.Code);
}
