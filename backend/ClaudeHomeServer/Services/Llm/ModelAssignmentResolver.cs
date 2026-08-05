namespace ClaudeHomeServer.Services.Llm;

// Резолвер модели для АГЕНТНЫХ мест каталога (группа «Чаты и персоны»: новый чат, чат
// персоны, исполнитель задач, сабагенты, LLM-канал модулей). Единая точка, где пустая
// модель превращается в конкретную по цепочке:
//
//   явная модель → назначение админа (конкретная модель | слот) → per-user слот → глобальный слот инстанса → null (CLI)
//
// Отличие от фоновых действий: у агентных мест нет цепочки «локаль → claude"
// (CheapTextRunner) — «local» и direct:-модели в назначении игнорируются как непригодные
// (агентной сессии нужны инструменты CLI), место уходит на свой слот по каталогу.
public sealed class ModelAssignmentResolver(
    AppSettingsService appSettings,
    LocalActionOverridesStore? store = null,
    UserModelTierResolver? userTiers = null,
    SpecialtySettingsStore? specialty = null)
{
    /// <summary>
    /// Модель персоны как «явная» для места: своя модель сильнее её уровня, уровень —
    /// сильнее специальности, специальность — сильнее назначения места (ADR
    /// model-resolution-and-fallback, раздел 1). Уровень и «tier:*» из специальности
    /// разворачиваем сразу в модель: маркер наружу не отдаём, иначе он осел бы
    /// в Session.Model. null — ни один шаг не сработал: решает место.
    /// </summary>
    public string? PersonaModel(Models.Persona? persona, string? ownerId = null)
    {
        if (!string.IsNullOrWhiteSpace(persona?.Model)) return persona.Model;
        if (persona?.ModelTier is { } tier)
        {
            var model = userTiers?.ModelFor(tier, ownerId) ?? appSettings.TierModel(tier);
            if (!string.IsNullOrWhiteSpace(model)) return model;
        }
        // Шаг 3 ADR: специальность персоны. Маршрут стора может быть либо конкретной
        // моделью, либо «tier:*» — разворачиваем через userTiers (единственная точка
        // склейки слотов, дубль логики личного/глобального слотов тут не заводим).
        // ownerId может быть null: ResolveRoute при этом всё равно работает (берёт только
        // глобальные пресеты — личные защищены по per-owner ключу стора).
        if (persona is not null && persona.Specialty != Models.PersonaSpecialty.None
            && specialty is not null)
        {
            var route = specialty.ResolveRoute(ownerId ?? "", persona.Specialty);
            if (!string.IsNullOrWhiteSpace(route))
            {
                if (LocalActionOverridesStore.ParseTierRoute(route) is { } routeTier)
                {
                    var m = userTiers?.ModelFor(routeTier, ownerId) ?? appSettings.TierModel(routeTier);
                    if (!string.IsNullOrWhiteSpace(m)) return m;
                    // Слот по тиру пуст — не выдаём маркер наружу, идём дальше
                }
                else
                {
                    return route;
                }
            }
        }
        return null;
    }

    public string? Resolve(string usageKey, string? explicitModel = null, string? ownerId = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitModel))
        {
            // Вместо имени модели вызывающий мог задать УРОВЕНЬ («tier:strong» — уровень задачи
            // или персоны-исполнителя). Разворачиваем здесь: склейка слотов остаётся в одной
            // точке (UserModelTierResolver), а маркер не оседает в сессии и не течёт наружу.
            if (LocalActionOverridesStore.ParseTierRoute(explicitModel.Trim()) is not { } explicitTier)
                return explicitModel;
            var explicitTierModel = userTiers?.ModelFor(explicitTier, ownerId) ?? appSettings.TierModel(explicitTier);
            if (!string.IsNullOrWhiteSpace(explicitTierModel)) return explicitTierModel;
            // Слот пуст — маркер наружу не отдаём (CLI его не поймёт): место идёт своим назначением
        }

        var action = LocalActionCatalog.Find(usageKey);
        if (store?.TryGet(usageKey) is { } assigned && !string.IsNullOrWhiteSpace(assigned))
        {
            var v = assigned.Trim();
            // Легаси-значения v1: оба означали «обычная модель, не локаль» → слот «средняя"
            if (v is LocalActionOverridesStore.ClaudeRoute or LocalActionOverridesStore.DefaultRoute)
                return userTiers?.ModelFor(ModelTier.Medium, ownerId) ?? appSettings.TierModel(ModelTier.Medium);
            if (LocalActionOverridesStore.ParseTierRoute(v) is { } tier)
                return userTiers?.ModelFor(tier, ownerId) ?? appSettings.TierModel(tier);
            // Конкретная модель. Локаль и direct: агентному месту непригодны — идём на слот.
            if (v != LocalActionOverridesStore.LocalRoute && !CloudCheapClient.IsDirectRoute(v))
                return v;
        }

        return userTiers?.ModelFor(action is null
            ? ModelTier.Medium
            : LocalActionCatalog.EffectiveDefaultTier(action), ownerId)
            ?? appSettings.TierModel(action is null
                ? ModelTier.Medium
                : LocalActionCatalog.EffectiveDefaultTier(action));
    }
}
