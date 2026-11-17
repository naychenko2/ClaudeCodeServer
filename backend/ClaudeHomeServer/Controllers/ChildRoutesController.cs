using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Т2f детского режима («Предметы и репетиторы», ред. 2 ADR-041, разрез §5): список допустимых
// маршрутов «поставщик → модель» для пикера модели в редакторе детского профиля. Сервер отдаёт
// готовые пары: пикер не повторяет правила валидатора и не даёт выбрать то, что не пройдёт
// при сохранении (Т2a) и при запуске хода (Т1c). Сейчас только записи пула с API-ключом
// (kind="pool"); Т5a добавит сюда "cloud"/"local", priced, requiresAck — поле заложено сразу,
// чтобы контракт не менялся под ногами фронта.
[ApiController]
[Route("api/admin/child-routes")]
[Authorize(Roles = "admin")]
[SubsystemUsage("llm")]
public class ChildRoutesController(ClaudeSubscriptionPool? pool, ModelCatalogService catalog) : ControllerBase
{
    // GET /api/admin/child-routes → { routes: [{ providerKey, kind, label, models: [{ id, label }] }] }
    // Порядок записей — порядок конфига пула (тот же, что показывает экран «Расход»). Список
    // моделей одинаков для всех записей: их обслуживает один и тот же каталог Claude.
    // Каталог — из кэша ModelCatalogService (та же цена, что у ModelsController.Get: CLI не ждём
    // дольше, чем он).
    [HttpGet]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        // Пула нет (хост без вертикали llm) — пустой список: допустимых детских маршрутов нет,
        // пикер не показывает ничего. fail-closed, как отказ ChildApiKeyRoute при запуске.
        if (pool is null) return Ok(new { routes = Array.Empty<object>() });

        // Модели — Claude-модели каталога (тот же источник, что ModelsController.Get): алиасы
        // семейств. Отсечено: "default"/"claude" — легаси-значения маршрута (ModelCellRules),
        // не модели; "tier:*"/"preset:*" — ссылки на слоты и цепочки, пикер показывает только
        // конкретные модели (инвариант шага).
        var models = (await catalog.GetModelsAsync(ct))
            .Where(m => string.Equals(m.Provider, LlmCapabilitiesCatalog.Claude.Provider, StringComparison.Ordinal))
            .Where(m => !IsNotModelValue(m.Value))
            .Select(m => (object)new { id = m.Value, label = m.DisplayName })
            .ToList();

        var routes = new List<object>();
        foreach (var sub in pool.All)
        {
            // Запись детского маршрута — чистый API-ключ (И2, ADR-041 §4): тот же критерий,
            // что у боевого запуска (ChildApiKeyRoute.Resolve: ключ есть, токена нет; запись с
            // OAuthToken — подписка даже при заданном ApiKey). Ход ребёнка по подписке — отказ,
            // поэтому подписка не может быть и выбором в пикере.
            if (string.IsNullOrWhiteSpace(sub.ApiKey) || !string.IsNullOrWhiteSpace(sub.OAuthToken)) continue;
            routes.Add(new
            {
                providerKey = sub.Key,
                kind = "pool",
                // Подпись — DisplayName записи (тот же, что показывает блок подписок экрана
                // «Расход»); не задан — ключ, он и так человекочитаем (имя в конфиге).
                label = string.IsNullOrWhiteSpace(sub.DisplayName) ? sub.Key : sub.DisplayName,
                models,
            });
        }
        return Ok(new { routes });
    }

    // Не значение модели: пустое, "default"/"claude" (легаси тир-маршрутов), "tier:*" — ссылки
    // на слоты тиров, "preset:*" — цепочки. Всё это не может быть пунктом пикера.
    private static bool IsNotModelValue(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value.Trim().Equals(LlmProviderRegistry.DefaultClaudeModel, StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals(LocalActionOverridesStore.ClaudeRoute, StringComparison.OrdinalIgnoreCase)
        || value.Trim().StartsWith(ModelCellRules.TierPrefix, StringComparison.OrdinalIgnoreCase)
        || value.Trim().StartsWith(LocalActionOverridesStore.PresetPrefix, StringComparison.OrdinalIgnoreCase);
}
