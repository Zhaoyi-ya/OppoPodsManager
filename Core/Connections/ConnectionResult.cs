namespace OppoPodsManager.Core.Connections;

public sealed record ConnectionResult(bool Succeeded, string? Error = null)
{
    public static ConnectionResult Success() => new(true);

    public static ConnectionResult Failure(string error) => new(false, error);
}
