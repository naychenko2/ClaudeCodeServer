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
    LlmProviderRegistry providers, ModelCatalogService models,
    LocalActionPresetService presets, SpecialtySettingsStore specialty) : ControllerBase
{
    // route: "local" | "tier:strong|medium|weak" (слоты тиров) | id модели провайдера;
    // легаси "claude"/"default" принимаются и трактуются как tier:medium (LocalActionRouter.Parse)
    public record RouteRequest(string Route);

    // preset: "recommended" | "free" | "local" | "balanced" | "tiers" | "tiers-local"
    public record PresetRequest(string Preset);

    // Массово подобрать исполнителя всем действиям по пресету. Возвращает применённый пресет и
    // число затронутых действий; актуальный список маршрутов фронт перечитывает из GET /api/usage.
    [HttpPost("preset")]
    public async Task<IActionResult> ApplyPreset([FromBody] PresetRequest req, CancellationToken ct)
    {
        var preset = (req.Preset ?? "").Trim().ToLowerInvariant() switch
        {
            "recommended" => ActionPreset.Recommended,
            "free" => ActionPreset.FreeOnly,
            "local" => ActionPreset.LocalFirst,
            "balanced" => ActionPreset.Balanced,
            "tiers" => ActionPreset.Tiers,
            "tiers-local" => ActionPreset.TiersLocal,
            _ => (ActionPreset?)null,
        };
        if (preset is null)
            return BadRequest(new { error = $"Неизвестный пресет «{req.Preset}»" });

        // Пресеты с бесплатной облачной моделью требуют настроенного агрегатора — иначе
        // «бесплатный» выбор молча выродился бы в Claude, что вводит в заблуждение.
        if (preset is ActionPreset.FreeOnly && !await presets.FreeAvailableAsync(ct))
            return BadRequest(new { error = "Бесплатные модели OpenRouter не настроены — пресет недоступен" });

        var count = await presets.ApplyAsync(preset.Value, ct);
        return Ok(new { preset = req.Preset, count });
    }

    [HttpPut("{key}")]
    public async Task<IActionResult> Set(string key, [FromBody] RouteRequest req, CancellationToken ct)
    {
        if (LocalActionCatalog.Find(key) is null)
            return NotFound(new { error = $"Неизвестное действие «{key}»" });

        var route = (req.Route ?? "").Trim();
        if (route.Length == 0)
            return BadRequest(new { error = "Не указан исполнитель действия" });

        // Агентным местам (группа «Чаты и персоны») локаль и direct:-модели непригодны —
        // отклоняем на входе, а не молча игнорируем при резолве
        var action = LocalActionCatalog.Find(key)!;
        if (action.Agentic && (route == LocalActionOverridesStore.LocalRoute
                || CloudCheapClient.IsDirectRoute(route)))
            return BadRequest(new { error = "Этому месту нужна агентная модель — локальная и direct-модели не подходят" });

        // preset:{id} — ссылка на пресет-цепочку (ADR-007 §3): допустимое значение места
        // каталога. Место назначения общее для всех, поэтому пресет обязан быть общим
        // (глобальным) — личный пресет у других пользователей стал бы битой ссылкой.
        if (LocalActionOverridesStore.IsPresetRoute(route))
        {
            var presetId = LocalActionOverridesStore.ParsePresetRoute(route);
            if (presetId is null || !specialty.Snapshot.Global.Presets
                    .Any(p => string.Equals(p.Id, presetId, StringComparison.OrdinalIgnoreCase)))
                return BadRequest(new { error = "Пресет не найден среди общих пресетов" });
        }
        else if (route is not (LocalActionOverridesStore.LocalRoute
                or LocalActionOverridesStore.ClaudeRoute
                or LocalActionOverridesStore.DefaultRoute)
            && LocalActionOverridesStore.ParseTierRoute(route) is null
            && await ValidateModelAsync(route, ct) is { } error)
            return BadRequest(new { error });

        if (!store.Set(key, route))
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Не удалось сохранить настройку на диск — проверьте путь и права data" });

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

    // null — модель годится. Классификация значения по каталогу (ADR-009 §1, форма 8):
    // обычная модель / модель без поставщика (есть только как direct:) / неизвестная.
    // Тексты ошибок — утверждённые, из docs/features/model-route-format-validation.md.
    // Провайдер должен быть настроен (иначе вызов упадёт в фолбэк и выбор был бы декорацией) —
    // но эту проверку делаем только для найденной обычной модели: для «без поставщика» и
    // «неизвестная» причина яснее из текста классификатора, а не из «провайдер выключен».
    private async Task<string?> ValidateModelAsync(string model, CancellationToken ct)
    {
        var known = await models.GetModelsAsync(ct);
        var verdict = LocalActionRouteValidator.ClassifyModelRoute(model, known.Select(m => m.Value));
        if (verdict is not null) return verdict;

        if (providers.ResolveByModel(model) is { } p && !p.Enabled)
            return $"Провайдер «{p.DisplayName}» не настроен — задайте LlmProviders:{p.Key}:ApiKey";

        return null;
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
                RouteKind.Tier => LocalActionOverridesStore.TierRoute(route.Tier ?? ModelTier.Medium),
                _ => route.Model,
            },
            source = route.Source.ToString().ToLowerInvariant(),
            // preset:{id} (ADR-007 §3) — ссылка на цепочку. Развёрнутый route/source выше не
            // ломаем — они для «Сейчас пойдёт» и обратной совместимости; по preset UI показывает
            // имя цепочки, а не маскирует его слотом первого шага. Место общее для всех → пресет
            // ищем только среди общих (global). Битая ссылка (id нет в общих) → name=null, steps=[].
            preset = DescribePreset(store.TryGet(key), specialty.Snapshot.Global.Presets),
        };
    }

    // Блок preset-ответа места каталога. name=null + пустые steps — признак битой ссылки.
    private sealed record CatalogPresetDescriptor(string Id, string? Name, IReadOnlyList<string> Steps);

    // Собирает preset-блок ответа места каталога. null — хранимое значение не является валидной
    // ссылкой preset:{id} (обычный маршрут: слот, модель, локаль, легаси). Валидная ссылка —
    // { id, name, steps }; битая (id не найден среди общих пресетов) — { id, name: null, steps: [] }.
    // Вынесено в internal static ради прямого unit-теста: через HTTP битую ссылку не смоделировать,
    // т.к. валидация Set отсекает несуществующий id ещё до записи в стор.
    internal static object? DescribePreset(string? stored, IReadOnlyList<ModelRoutePreset> globalPresets)
    {
        // ParsePresetRoute отсекает и не-ссылки, и «preset:» без id (невалидный маршрут,
        // которого в сторе не бывает) — для обоих случаев preset отсутствует.
        if (LocalActionOverridesStore.ParsePresetRoute(stored) is not { } id) return null;
        var preset = globalPresets.FirstOrDefault(
            p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        return preset is null
            ? new CatalogPresetDescriptor(id, null, Array.Empty<string>())
            : new CatalogPresetDescriptor(preset.Id, preset.Name, preset.Steps);
    }
}
