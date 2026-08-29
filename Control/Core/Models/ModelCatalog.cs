using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.Control.Core.Models;

public sealed class ModelCatalog
{
    private readonly IReadOnlyList<ModelDefinition> _models;
    private readonly IReadOnlyDictionary<string, ModelDefinition> _byProductId;

    public ModelCatalog(IEnumerable<ModelDefinition> models)
    {
        // 官方目录同一产品 ID 可能同时包含设备信息和功能白名单条目，优先保留功能更完整的一条。
        _models = models
            .GroupBy(model => string.IsNullOrWhiteSpace(model.ProductId)
                ? $"name:{model.DisplayName}"
                : $"id:{model.ProductId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(model => model.Features.Count)
                .ThenByDescending(model => model.NoiseModes.Count)
                .ThenByDescending(model => model.EqualizerPresets.Count)
                .First())
            .ToArray();
        _byProductId = _models
            .Where(model => !string.IsNullOrWhiteSpace(model.ProductId))
            .GroupBy(model => model.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public ModelDefinition? Find(string? productId, string? deviceName)
    {
        if (!string.IsNullOrWhiteSpace(productId) && _byProductId.TryGetValue(productId, out var byProductId))
            return byProductId;

        if (string.IsNullOrWhiteSpace(deviceName))
            return null;

        return _models
            .SelectMany(model => model.Names.Select(name => (Model: model, Name: name)))
            .Where(entry => deviceName.Contains(entry.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.Name.Length)
            .Select(entry => entry.Model)
            .FirstOrDefault();
    }

    // 根据显示名称定位官方型号目录中的品牌和系列。
    public ModelCatalogLocation? FindLocation(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var model = _models.FirstOrDefault(candidate =>
            string.Equals(candidate.DisplayName, displayName, StringComparison.Ordinal));
        return model is null ? null : new ModelCatalogLocation(model.Brand, model.Series);
    }

    // 提供稳定排序后的型号清单，供桌面端手动型号覆盖界面使用。
    public IReadOnlyList<ModelDefinition> Models => _models
        .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // 返回品牌、系列和型号的稳定树，用于保留原设置页的三级筛选体验。
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> BrandTree => _models
        .GroupBy(model => model.Brand, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>)group
                .GroupBy(model => model.Series, StringComparer.OrdinalIgnoreCase)
                .OrderBy(series => series.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(series => series.Key, series => (IReadOnlyList<ModelDefinition>)series.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
}
