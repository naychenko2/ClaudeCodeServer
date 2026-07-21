using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Управление исполнителем фоновых действий в рантайме: локальная модель, Claude или
// конкретная модель настроенного провайдера. Дальше действие идёт по общей цепочке
// «выбранное → локаль → claude» (см. CheapTextRunner).
//
// Только для админов: настройка глобальная — влияет на фоновые вызовы всех пользователей.
// Чтение текущего состояния отдельного эндпоинта не имеет: список действий с маршрутом и
// источником значения уже приходит в блоке ollama ответа GET /api/usage.
[ApiController]
[Route("api/admin/local-actions")]
[Authorize(Roles = "admin")]
public class LocalActionsAdminController(
    LocalActionOverridesStore store, LocalActionRouter router,
    LlmProviderRegistry providers, ModelCatalogService models) : ControllerBase
{
    // route: "local" | "claude" | id модели любого настроенного провайдера
    public record RouteRequest(string Route);

    [HttpPut("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] RouteRequest req, CancellationToken ct)
    {
        if (LocalActionCatalog.Find(key) is null)
            return NotFound(new { error = $"Неизвестное действие «{key}»" });

        var route = (req.Route ?? "").Trim();
        if (route.Length == 0)
            return BadRequest(new { error = "Не указан исполнитель действия" });

        if (route is not (LocalActionOverridesStore.LocalRoute or LocalActionOverridesStore.ClaudeRoute)
            && await ValidateModelAsync(route, ct) is { } error)
            return BadRequest(new { error });

        if (!store.Set(key, route))
            return BadRequest(new { error = "Не удалось сохранить настройку" });

        return Ok(Describe(key));
    }

    // Сброс к значению из конфига/каталога
    [HttpDelete("{key}")]
    public IActionResult Reset(string key)
    {
        if (!store.Reset(key))
            return NotFound(new { error = $"Неизвестное действие «{key}»" });
        return Ok(Describe(key));
    }

    // null — модель годится. Провайдер должен быть настроен (иначе вызов гарантированно
    // упадёт в фолбэк и выбор был бы бессмысленной декорацией), а сама модель — существовать
    // в каталоге: опечатка в id иначе всплыла бы только при первом фоновом вызове.
    private async Task<string?> ValidateModelAsync(string model, CancellationToken ct)
    {
        if (providers.ResolveByModel(model) is { } p && !p.Enabled)
            return $"Провайдер «{p.DisplayName}» не настроен — задайте LlmProviders:{p.Key}:ApiKey";

        var known = await models.GetModelsAsync(ct);
        return known.Any(m => string.Equals(m.Value, model, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"Модель «{model}» отсутствует в каталоге";
    }

    private object Describe(string key)
    {
        var route = router.Resolve(key);
        return new
        {
            key,
            route = route.Kind switch
            {
                RouteKind.Local => LocalActionOverridesStore.LocalRoute,
                RouteKind.Claude => LocalActionOverridesStore.ClaudeRoute,
                _ => route.Model,
            },
            source = route.Source.ToString().ToLowerInvariant(),
        };
    }
}
