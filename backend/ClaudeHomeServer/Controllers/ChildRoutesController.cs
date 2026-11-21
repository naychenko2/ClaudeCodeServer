using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Auth;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Т2f/Т5a детского режима («Предметы и репетиторы», ред. 2 ADR-041, разрез §3/§5): список допустимых
// маршрутов «поставщик → модель» для пикера модели в редакторе детского профиля. Сервер отдаёт
// готовые пары: пикер не повторяет правила валидатора и не даёт выбрать то, что не пройдёт
// при сохранении и при запуске хода. Отбор — ОДНА функция ChildRoute.Evaluate (её же зовёт
// валидатор и сверка запуска): правил отбора в двух местах нет.
[ApiController]
[Route("api/admin/child-routes")]
[Authorize(Roles = "admin")]
[SubsystemUsage("llm")]
public class ChildRoutesController(ClaudeSubscriptionPool? pool, LlmProviderRegistry providers,
    ModelCatalogService catalog) : ControllerBase
{
    // GET /api/admin/child-routes →
    //   { routes: [{ providerKey, kind: "pool"|"cloud"|"local", label, priced, requiresAck, models: [{ id, label }] }] }
    // pool — запись пула с API-ключом (модели Anthropic); cloud — LlmProviders
    // IsLocal=false (только модели с ценой: без неё бюджет И3 не ограничивает);
    // local — LlmProviders IsLocal=true (priced=false: «бюджет не тратится»).
    // По И2″ (ADR-041 ред. 4) согласие профиля нужно ЛЮБОМУ маршруту (requiresAck=true у всех), отказ без него
    // даёт ChildModeGate при запуске. Это список для пикера, а не запуск хода, поэтому отбор идёт с acknowledged: true.
    // Порядок: записи пула в порядке конфига (как экран «Расход»), затем провайдеры. Каталог Claude —
    // из кэша ModelCatalogService (та же цена, что у ModelsController.Get).
    [HttpGet]
    [Authorize(Policy = AdminByStoreRequirement.PolicyName)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var routes = new List<object>();

        // Пула нет (хост без вертикали llm) — записей пула в списке нет: fail-closed, как отказ ChildRoute.
        if (pool is not null)
        {
            // Модели — Claude-модели каталога: алиасы семейств. Маркеры/«default»/«claude» отсекает сам
            // Evaluate (ModelNotConcrete), пикер показывает только конкретные модели.
            var claudeModels = (await catalog.GetModelsAsync(ct))
                .Where(m => string.Equals(m.Provider, LlmCapabilitiesCatalog.Claude.Provider, StringComparison.Ordinal))
                .ToList();
            foreach (var sub in pool.All)
            {
                var models = claudeModels
                    .Where(m => ChildRoute.Evaluate(sub.Key, m.Value, acknowledged: true, pool, providers) is
                        { Kind: ChildRouteKind.Pool })
                    .Select(m => (object)new { id = m.Value, label = m.DisplayName })
                    .ToList();
                // Подписка (нет ключа или есть токен) не даёт ни одной пары — в список не попадает.
                if (models.Count == 0) continue;
                routes.Add(new
                {
                    providerKey = sub.Key,
                    kind = "pool",
                    label = string.IsNullOrWhiteSpace(sub.DisplayName) ? sub.Key : sub.DisplayName,
                    priced = true,
                    requiresAck = true,
                    models,
                });
            }
        }

        foreach (var p in providers.All)
        {
                        var models = p.Models
                .Where(m => ChildRoute.Evaluate(p.Key, m.Id, acknowledged: true, pool, providers) is
                    { Kind: ChildRouteKind.Provider })
                .Select(m => (object)new { id = m.Id, label = string.IsNullOrWhiteSpace(m.DisplayName) ? m.Id : m.DisplayName })
                .ToList();
            if (models.Count == 0) continue;
            routes.Add(new
            {
                providerKey = p.Key,
                kind = p.IsLocal ? "local" : "cloud",
                label = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Key : p.DisplayName,
                priced = !p.IsLocal,
                requiresAck = true,
                models,
            });
        }
        return Ok(new { routes });
    }
}
