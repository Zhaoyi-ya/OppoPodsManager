namespace OppoPodsManager.Core.Brands;

public interface IBrandRegistry
{
    IReadOnlyList<IBrandConnector> Connectors { get; }
}
