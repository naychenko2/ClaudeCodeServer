using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

[ApiController]
[Authorize]
[Route("api/models")]
public class ModelsController(ModelCatalogService catalog, LlmProviderRegistry providers,
    ModelAssignmentResolver assignments, PersonaManager personas,
    SpecialtySettingsStore specialty, UserStore users,
    AppSettingsService appSettings, LocalActionOverridesStore localActions,
    SessionManager sessions) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);

    // Актуальный список моделей (Claude — из CLI с кэшем; CLI-провайдеры — из конфига
    // LlmProviders, только при ApiKey) + возможности провайдеров, чтобы UI скрывал недоступное.
    // Провайдеры отдаются ВСЕ (включая ненастроенные): у каждого флаг Configured (ключ задан ≠ пустой)
    // — для плитки «Подключённые модели» («Активны: … · N не настроены»). Модели ненастроенного
    // провайдера в каталог не попадают, так что ModelPicker его группу не покажет.
    // assignments — УЖЕ РЕЗОЛВНУТЫЕ модели агентных мест (null = решает CLI): по ним пикеры
    // подписывают пункт «По умолчанию (<модель>)», не зная про слоты и оверрайды.
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var caps = new Dictionary<string, LlmCapabilities>
        {
            [LlmCapabilitiesCatalog.Claude.Provider] = LlmCapabilitiesCatalog.Claude,
        };
        foreach (var p in providers.All)
            caps[p.Key] = LlmProviderRegistry.CapabilitiesOf(p);

        var resolved = new Dictionary<string, string?>
        {
            [LocalActionCatalog.ChatNew] = assignments.Resolve(LocalActionCatalog.ChatNew, ownerId: UserId),
            [LocalActionCatalog.ChatPersona] = assignments.Resolve(LocalActionCatalog.ChatPersona, ownerId: UserId),
            [LocalActionCatalog.TasksExecutor] = assignments.Resolve(LocalActionCatalog.TasksExecutor, ownerId: UserId),
        };

        return Ok(new { models = await catalog.GetModelsAsync(ct), providers = caps, assignments = resolved });
    }

    // Эффективный резолв модели для показа «Сейчас пойдёт» (спека блок 4, ADR-007 §5 п.5):
    // по контексту места выбора (place / personaId / specialty / tier) возвращает модель,
    // откуда она взялась, эффективный уровень и раскрытие пресета. Считается ТОЙ ЖЕ кодовой
    // дорогой, что запуск хода (ModelAssignmentResolver.Preview), — без второй точки истины.
    //
    // Параметры (query):
    //   place      — ключ места каталога (chat-persona, chat-new, tasks-executor, …);
    //   personaId  — id персоны (модель/уровень/матрица персоны — самое узкое);
    //   specialty  — ключ специальности (если без персоны: матрица специальности);
    //   tier       — strong|medium|weak, уровень сверху (задача); сильнее персоны/специальности/места.
    //   sessionId  — сессия чата: вместе с personaId добавляет в ответ subagentChip —
    //                готовые label/hint/kind чипа модели на карточке персоны-сабагента
    //                (считается ModelAssignmentResolver.SubagentChipFor от пары персона+сессия,
    //                фронт логику не пересобирает).
    //
    // Ответ: { model, source, tier, tierOrigin, preset:{id,name,steps,broken}|null, chain[],
    //          subagentChip:{kind,label,hint}|null — только при sessionId+personaId }.
    //   model       — первая модель хода (развёрнутая) либо null (пустой резолв / битый пресет);
    //   source      — persona-model|persona-cell|specialty-cell|owner-slot|instance-slot|
    //                  place-assignment|explicit (ГДЕ выбрано значение);
    //   tier        — эффективный уровень (strong|medium|weak|null) — для подписи «уровень …»;
    //   tierOrigin  — кто задал уровень: task|persona|specialty|place|null;
    //   preset      — раскрытие, если значение было preset:{id}: {id,name,steps,broken};
    //   chain       — план фолбэка (развёрнутые модели) для подсказки наведения.
    [HttpGet("preview")]
    public IActionResult Preview(string? place, string? personaId, string? specialty, string? tier,
        string? sessionId)
    {
        var ownerId = UserId ?? string.Empty;
        var persona = !string.IsNullOrEmpty(personaId) ? personas.Get(personaId, ownerId) : null;

        PersonaSpecialty spec = PersonaSpecialty.None;
        if (!string.IsNullOrWhiteSpace(specialty) && SpecialtyCatalog.TryGetByKey(specialty, out var entry))
            spec = entry.Specialty;
        else if (persona is not null) spec = persona.Specialty;

        ModelTier? overrideTier = ModelTiers.TryParse(tier, out var ot) ? ot : null;
        var d = assignments.Preview(place, persona, spec, ownerId, overrideTier);

        // Чип модели сабагента: нужна пара (персона, сессия) — по модели сессии видно,
        // применяется ли пин персоны (Claude-чат) или ход уходит на слоты провайдера
        ModelAssignmentResolver.SubagentModelChip? chip = null;
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (persona is null)
                return BadRequest(new { error = "subagentChip требует существующего personaId" });
            var session = sessions.GetById(sessionId);
            // Чужая/неизвестная сессия — 404, не раскрывая существование
            if (session is null || (session.OwnerId is not null && session.OwnerId != ownerId))
                return NotFound(new { error = "Сессия не найдена" });
            chip = assignments.SubagentChipFor(persona, session.Model, ownerId);
        }

        return Ok(new
        {
            model = d.Model,
            source = d.Source,
            tier = d.EffectiveTier?.ToString().ToLowerInvariant(),
            tierOrigin = d.TierOrigin,
            preset = d.Preset is null ? null : new
            {
                id = d.Preset.Id,
                name = d.Preset.Name,
                steps = d.Preset.Steps,
                broken = d.Preset.Broken,
            },
            chain = d.Chain,
            subagentChip = chip is null ? null : new { kind = chip.Kind, label = chip.Label, hint = chip.Hint },
        });
    }

    // Места, где пресет выбран значением (для диалога удаления, спека блок 6): «Пресет выбран
    // в N местах». Считается на лету — постоянного обратного индекса ссылок нет (ADR-007 §3).
    // Обход: слоты инстанса и владельцев, ячейки специальностей (общие + личные), явная модель
    // и ячейки персон, места каталога. Ответ: { presetId, count, usages:[{kind,label,ownerId}] }.
    //   kind   — instance-slot|owner-slot|specialty-cell|persona-model|persona-cell|place;
    //   label  — человекочитаемое описание места (для текста диалога);
    //   ownerId— null для общих мест (инстанс/общая специальность/место каталога), иначе id владельца.
    [HttpGet("presets/{id}/usage")]
    public IActionResult PresetUsage(string id)
    {
        var usages = new List<ModelPresetUsage>();

        // 1. Слоты инстанса (общие «Модели по умолчанию»)
        var app = appSettings.Get();
        AddSlot(usages, app.ModelTierStrong, "Сильная", "Модели по умолчанию", null, id);
        AddSlot(usages, app.ModelTierMedium, "Средняя", "Модели по умолчанию", null, id);
        AddSlot(usages, app.ModelTierWeak, "Слабая", "Модели по умолчанию", null, id);

        // 2. Личные слоты владельцев
        foreach (var u in users.GetAll())
        {
            var whose = string.IsNullOrEmpty(UserId) || u.Id == UserId
                ? "Мои модели" : $"Модели · {u.DisplayName ?? u.Username}";
            AddSlot(usages, u.ModelTierStrong, "Сильная", whose, u.Id, id);
            AddSlot(usages, u.ModelTierMedium, "Средняя", whose, u.Id, id);
            AddSlot(usages, u.ModelTierWeak, "Слабая", whose, u.Id, id);
        }

        // 3. Ячейки специальностей (общий слой + личные слои)
        var file = specialty.Snapshot;
        ScanSpecialtyLayer(usages, file.Global, null, id);
        foreach (var (oid, layer) in file.Owners)
            ScanSpecialtyLayer(usages, layer, oid, id);

        // 4. Явная модель и ячейки персон
        foreach (var p in personas.GetAllInternal())
        {
            if (RefersTo(p.Model, id))
                usages.Add(new ModelPresetUsage("persona-model", $"Персона «{p.Name}» — всегда работает", p.OwnerId));
            AddTierCells(usages, p.TierStrong, p.TierMedium, p.TierWeak,
                $"Персона «{p.Name}»", "persona-cell", p.OwnerId, id);
        }

        // 5. Места каталога («Кто что выполняет») — общие назначения
        foreach (var (key, route) in localActions.All)
        {
            if (!RefersTo(route, id)) continue;
            var title = LocalActionCatalog.Find(key)?.Title ?? key;
            usages.Add(new ModelPresetUsage("place", $"Место «{title}»", null));
        }

        return Ok(new { presetId = id, count = usages.Count, usages });
    }

    internal sealed record ModelPresetUsage(string Kind, string Label, string? OwnerId);

    private static void AddSlot(List<ModelPresetUsage> usages, string? value, string tierLabel,
        string whose, string? ownerId, string presetId)
    {
        if (RefersTo(value, presetId))
            usages.Add(new ModelPresetUsage(ownerId is null ? "instance-slot" : "owner-slot",
                $"{whose} · {tierLabel}", ownerId));
    }

    private static void AddTierCells(List<ModelPresetUsage> usages,
        string? strong, string? medium, string? weak,
        string baseLabel, string kind, string? ownerId, string presetId)
    {
        if (RefersTo(strong, presetId)) usages.Add(new ModelPresetUsage(kind, $"{baseLabel} · Сильная", ownerId));
        if (RefersTo(medium, presetId)) usages.Add(new ModelPresetUsage(kind, $"{baseLabel} · Средняя", ownerId));
        if (RefersTo(weak, presetId)) usages.Add(new ModelPresetUsage(kind, $"{baseLabel} · Слабая", ownerId));
    }

    private static void ScanSpecialtyLayer(List<ModelPresetUsage> usages, SpecialtySettingsLayer layer,
        string? ownerId, string presetId)
    {
        foreach (var (key, tmpl) in layer.Specialties)
        {
            var label = SpecialtyCatalog.TryGetByKey(key, out var e) ? e.Label : key;
            AddTierCells(usages, tmpl.TierStrong, tmpl.TierMedium, tmpl.TierWeak,
                $"Специальность «{label}»", "specialty-cell", ownerId, presetId);
        }
        if (layer.DefaultSpecialty is { } ds)
            AddTierCells(usages, ds.TierStrong, ds.TierMedium, ds.TierWeak,
                "Любая специальность", "specialty-cell", ownerId, presetId);
    }

    private static bool RefersTo(string? value, string presetId) =>
        LocalActionOverridesStore.IsPresetRoute(value)
        && string.Equals(LocalActionOverridesStore.ParsePresetRoute(value), presetId, StringComparison.OrdinalIgnoreCase);
}
