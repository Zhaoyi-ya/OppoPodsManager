using OppoPodsManager.Core.Brands;

namespace OppoPodsManager.Application.Brands;

public sealed class StaticBrandRegistry : IBrandRegistry
{
    public StaticBrandRegistry(IEnumerable<IBrandConnector> connectors)
    {
        Connectors = connectors?.ToArray()
            ?? throw new ArgumentNullException(nameof(connectors));
    }

    public IReadOnlyList<IBrandConnector> Connectors { get; }
}
