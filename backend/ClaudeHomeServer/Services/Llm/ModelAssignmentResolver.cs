namespace ClaudeHomeServer.Services.Llm;

// Резолвер модели для АГЕНТНЫХ мест каталога (группа «Чаты и персоны»: новый чат, чат
// персоны, исполнитель задач, сабагенты, LLM-канал модулей). Единая точка, где пустая
// модель превращается в конкретную по цепочке:
//
//   явная модель → назначение админа (конкретная модель | слот) → слот каталога → null (CLI)
//
// Отличие от фоновых действий: у агентных мест нет цепочки «локаль → claude»
// (CheapTextRunner) — «local» и direct:-модели в назначении игнорируются как непригодные
// (агентной сессии нужны инструменты CLI), место уходит на свой слот по каталогу.
public sealed class ModelAssignmentResolver(
    AppSettingsService appSettings, LocalActionOverridesStore? store = null)
{
    public string? Resolve(string usageKey, string? explicitModel = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel)) return explicitModel;

        var action = LocalActionCatalog.Find(usageKey);
        if (store?.TryGet(usageKey) is { } assigned && !string.IsNullOrWhiteSpace(assigned))
        {
            var v = assigned.Trim();
            // Легаси-значения v1: оба означали «обычная модель, не локаль» → слот «средняя»
            if (v is LocalActionOverridesStore.ClaudeRoute or LocalActionOverridesStore.DefaultRoute)
                return appSettings.TierModel(ModelTier.Medium);
            if (LocalActionOverridesStore.ParseTierRoute(v) is { } tier)
                return appSettings.TierModel(tier);
            // Конкретная модель. Локаль и direct: агентному месту непригодны — идём на слот.
            if (v != LocalActionOverridesStore.LocalRoute && !CloudCheapClient.IsDirectRoute(v))
                return v;
        }

        return appSettings.TierModel(action is null
            ? ModelTier.Medium
            : LocalActionCatalog.EffectiveDefaultTier(action));
    }
}
