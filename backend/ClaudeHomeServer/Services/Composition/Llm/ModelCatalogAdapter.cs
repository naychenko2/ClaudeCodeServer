namespace ClaudeHomeServer.Services.Composition.Llm;

// Реализация IModelCatalog (Core) поверх ModelCatalogService (Main).
// Тонкая обёртка: делегирует в GetModelsAsync и мапит nested-тип ModelInfo
// в ModelCatalogEntry (Core). Создана ради выноса Llm в отдельный .csproj —
// Llm больше не имеет прямого доступа к ModelCatalogService.
public sealed class ModelCatalogAdapter(ModelCatalogService catalog) : IModelCatalog
{
    public async Task<IReadOnlyList<ModelCatalogEntry>> GetModelsAsync(CancellationToken ct = default)
    {
        var models = await catalog.GetModelsAsync(ct);
        var result = new List<ModelCatalogEntry>(models.Count);
        foreach (var m in models)
            result.Add(new ModelCatalogEntry(m.Value, m.DisplayName, m.Description,
                m.Provider, m.ContextWindow, m.IsCurated));
        return result;
    }
}
