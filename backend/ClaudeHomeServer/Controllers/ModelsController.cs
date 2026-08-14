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
    SessionManager sessions, TaskManager tasks) : ControllerBase
{
    private string? UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub);
    private bool IsAdmin => User.IsInRole("admin");

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
    //   taskId     — контекст задачи: резолв по формуле боевого запуска исполнителя
    //                (ExecutorModel: уровень задачи → персона → её специальность и матрицы).
    //                Задаёт personaId (из задачи) и tier (уровень задачи); явные personaId/
    //                tier в запросе при этом игнорируются — источник правды задача.
    //   sessionId  — контекст чата (без personaId): модель следующего хода этого чата
    //                по формуле боевого резолва сессии. Вместе с personaId вместо контекста
    //                чата добавляет в ответ subagentChip — готовые label/hint/kind чипа
    //                модели на карточке персоны-сабагента (считается
    //                ModelAssignmentResolver.SubagentChipFor от пары персона+сессия,
    //                фронт логику не пересобирает).
    //
    // Ответ: { model, source, tier, tierOrigin, preset:{id,name,steps,broken}|null, chain[],
    //          frozen, subagentChip:{kind,label,hint}|null — только при sessionId+personaId }.
    //   model       — первая модель хода (развёрнутая) либо null (пустой резолв / битый пресет);
    //   source      — persona-model|persona-cell|specialty-cell|owner-slot|instance-slot|
    //                  place-assignment|explicit (ГДЕ выбрано значение);
    //   tier        — эффективный уровень (strong|medium|weak|null) — для подписи «уровень …»;
    //   tierOrigin  — кто задал уровень: task|persona|specialty|place|null;
    //   preset      — раскрытие, если значение было preset:{id}: {id,name,steps,broken};
    //   chain       — путь развёрнутой цепочки хода (модель + план фолбэка, ADR-007 §4) —
    //                 для подсказки на пометке подмены и наведения «Дальше в цепочке: …»;
    //   frozen      — только для контекста чата: true — модель чата заморожена при создании
    //                 (ADR-007 §9.1), смена настроек подложится только новым чатом.
    [HttpGet("preview")]
    public IActionResult Preview(string? place, string? personaId, string? specialty, string? tier,
        string? sessionId, string? taskId)
    {
        var ownerId = UserId ?? string.Empty;

        // Контекст задачи (дефект A1): превью считает той же формулой, что боевой запуск
        // исполнителя (TaskExecutionService → ModelAssignmentResolver.ExecutorModel) —
        // уровень задачи разворачивается по матрицам ПЕРСОНЫ-ИСПОЛНИТЕЛЯ и её специальности,
        // а не по слотам владельца.
        if (!string.IsNullOrEmpty(taskId))
        {
            var task = tasks.GetById(taskId);
            if (task is null || task.OwnerId != ownerId)
                return NotFound(new { error = "Задача не найдена" });
            var td = TaskPreview(task);
            // Цепочка хода — как у боевой сессии исполнителя: ExecutorModel замораживается
            // в Session.Model, дальше ClaudeSession.EffectiveTurnChain = ResolveChain места
            // по этой модели (с хвостом тира, ADR-007 §4.1). Битый пресет не дотягиваем —
            // его показ («пресет удалён») и есть ответ строки-итога.
            var chain = td.PresetBroken
                ? td.Chain
                : assignments.ResolveChain(LocalActionCatalog.TasksExecutor, td.Model, ownerId);
            return Ok(PreviewResponse(td with { Chain = chain }, frozen: null, chip: null));
        }

        var persona = !string.IsNullOrEmpty(personaId) ? personas.Get(personaId, ownerId) : null;

        PersonaSpecialty spec = PersonaSpecialty.None;
        if (!string.IsNullOrWhiteSpace(specialty) && SpecialtyCatalog.TryGetByKey(specialty, out var entry))
            spec = entry.Specialty;
        else if (persona is not null) spec = persona.Specialty;

        ModelTier? overrideTier = ModelTiers.TryParse(tier, out var ot) ? ot : null;
        var d = assignments.Preview(place, persona, spec, ownerId, overrideTier);

        // Контекст чата (C1): модель следующего хода существующего чата. Резолв — ровно
        // боевой (ClaudeSession.EffectiveTurnChain → ResolveChain(usageKey, session.Model)):
        // место по признакам сессии, поверх замороженной модели чата.
        if (!string.IsNullOrEmpty(sessionId) && persona is null)
        {
            var session = sessions.GetById(sessionId);
            // Чужая/неизвестная сессия — 404, не раскрывая существование
            if (session is null || sessions.ResolveOwnerId(session) != ownerId)
                return NotFound(new { error = "Сессия не найдена" });
            var usageKey = UsageKeyFor(session);
            var chain = assignments.ResolveChain(usageKey, session.Model, ownerId);
            // Замороженность — свойство сессии: чат с непустой Model не перечитывает
            // настройки (ADR-007 §9.1, смена подложится только новым чатом); с пустой —
            // живёт по назначению места каждым ходом.
            var frozen = !string.IsNullOrWhiteSpace(session.Model);
            var cd = frozen
                // Замороженная модель — явная для места: источник explicit, уровень — той же
                // точкой, что достройка хвоста в бою (TierOfModel по слотам владельца)
                ? new ModelAssignmentResolver.ModelSourceDetail(session.Model!.Trim(),
                    ModelAssignmentResolver.ModelSource.ExplicitModel,
                    assignments.TierOfModel(session.Model, ownerId), null, null, chain)
                : assignments.Preview(usageKey, null, PersonaSpecialty.None, ownerId, null) with { Chain = chain };
            return Ok(PreviewResponse(cd, frozen, chip: null));
        }

        // Чип модели сабагента: нужна пара (персона, сессия) — по модели сессии видно,
        // применяется ли пин персоны (Claude-чат) или ход уходит на слоты провайдера
        ModelAssignmentResolver.SubagentModelChip? chip = null;
        if (!string.IsNullOrEmpty(sessionId))
        {
            if (persona is null)
                return BadRequest(new { error = "subagentChip требует существующего personaId" });
            var session = sessions.GetById(sessionId);
            // Чужая/неизвестная сессия — 404, не раскрывая существование. Проверка та же,
            // что у ветки чата выше (ResolveOwnerId): проектная сессия принадлежит
            // владельцу проекта и при пустом OwnerId чип недоступен посторонним.
            if (session is null || sessions.ResolveOwnerId(session) != ownerId)
                return NotFound(new { error = "Сессия не найдена" });
            chip = assignments.SubagentChipFor(persona, session.Model, ownerId);
        }

        return Ok(PreviewResponse(d, frozen: null, chip: chip));
    }

    // Превью задачи: формула боевого ExecutorModel (ADR-007 §5.3) с фиксацией источника —
    //   уровень задачи задан: матрицы персоны-исполнителя (её специальность) → слоты; ЯВНАЯ
    //     модель персоны не участвует (уровень сильнее) — потому она затирается;
    //   уровня нет: модель персоны → её уровень → матрицы (дефолт места tasks-executor);
    //   персоны нет: пустая заглушка — уровень задачи/места разворачивается по слотам,
    //     как в бою (ExecutorModel с пустым списком матриц).
    // Живой объект персоны не мутируется — снимок с нужными полями.
    private ModelAssignmentResolver.ModelSourceDetail TaskPreview(TaskItem task)
    {
        var ownerId = task.OwnerId!;
        var persona = task.PersonaId is null ? null : personas.Get(task.PersonaId, ownerId);
        var effective = persona is null ? new Persona() : new Persona
        {
            Model = task.ModelTier is null ? persona.Model : null,
            ModelTier = persona.ModelTier,
            Specialty = persona.Specialty,
            TierStrong = persona.TierStrong,
            TierMedium = persona.TierMedium,
            TierWeak = persona.TierWeak,
        };
        return assignments.Preview(LocalActionCatalog.TasksExecutor, effective, effective.Specialty,
            ownerId, task.ModelTier);
    }

    // Место применения по признакам сессии — та же формула, что SessionManager.UsageKeyFor
    // и ClaudeSession.UsageKey (исполнитель задач → персона → новый чат).
    private static string UsageKeyFor(Session s) =>
        s.TaskExecution || s.TaskId is not null ? LocalActionCatalog.TasksExecutor
        : !string.IsNullOrWhiteSpace(s.PersonaId) ? LocalActionCatalog.ChatPersona
        : LocalActionCatalog.ChatNew;

    // Тело ответа превью: одна точка сериализации для всех контекстов. chip — только
    // для пары (personaId, sessionId); frozen — только для контекста чата.
    private static object PreviewResponse(ModelAssignmentResolver.ModelSourceDetail d,
        bool? frozen, ModelAssignmentResolver.SubagentModelChip? chip) => new
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
        frozen,
        subagentChip = chip is null ? null : new { kind = chip.Kind, label = chip.Label, hint = chip.Hint },
    };

    // Места, где пресет выбран значением (для диалога удаления, спека блок 6): «Пресет выбран
    // в N местах». Считается на лету — постоянного обратного индекса ссылок нет (ADR-007 §3).
    // Обход: слоты инстанса и владельцев, ячейки специальностей (общие + личные), явная модель
    // и ячейки персон, места каталога. Ответ: { presetId, count, usages:[{kind,label,ownerId}] }.
    //   kind   — instance-slot|owner-slot|specialty-cell|persona-model|persona-cell|place;
    //   label  — человекочитаемое описание места (для текста диалога);
    //   ownerId— null для общих мест (инстанс/общая специальность/место каталога), иначе id владельца.
    // Per-owner изоляция: пресет обязан быть виден вызывающему (Find по его эффективному
    // списку — иначе usage по угаданному id раскрывал бы чужие настройки), не-админу
    // отдаются только его места и общие — см. PresetUsageList.
    [HttpGet("presets/{id}/usage")]
    public IActionResult PresetUsage(string id)
    {
        var ownerId = UserId ?? "";
        if (PresetStore.Find(specialty, ownerId, id) is null)
            return NotFound(new { error = "Пресет не найден" });
        var usages = PresetUsageList(id);
        return Ok(new { presetId = id, count = usages.Count, usages });
    }

    public record RenamePresetRequest(string Name);

    // Переименовать пресет (спец. блок 6): правится тот экземпляр, который резолвится
    // по id у вызывающего (личный раньше глобального — порядок SpecialtySettingsStore.
    // EffectivePresets). Не-админ правит ТОЛЬКО свой личный пресет: общий (Global) и
    // назначенный ему админом (User, B9) не трогает — имя видно владельцам слоя.
    // Идущие ходы не роняет: цепочка разворачивается на границе запуска хода, имя
    // в ней не участвует.
    [HttpPut("presets/{id}/name")]
    public IActionResult RenamePreset(string id, [FromBody] RenamePresetRequest req)
    {
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name))
            return BadRequest(new { error = "Имя пресета не может быть пустым" });

        var ownerId = UserId ?? "";
        var located = PresetStore.Find(specialty, ownerId, id);
        if (located is null)
            return NotFound(new { error = "Пресет не найден" });
        if (located.Scope != PresetScope.Owner && !IsAdmin)
            return Forbid();

        if (PresetStore.Rename(specialty, ownerId, id, name) is { } error)
            return BadRequest(new { error });
        return Ok(new { id, name });
    }

    // Удалить пресет (спец. блок 6). Как и переименование: не-админ удаляет только свой
    // личный пресет; общий (Global) и назначенный ему админом (User, B9) — только админ.
    // Перед удалением фронт показывает места использования (GET presets/{id}/usage);
    // сам запрос — безусловный: по ADR-007 §3 ссылки не блокируют удаление, осиротевшие
    // preset:{id} — fail-open вниз (битая ячейка = «спроси следующую матрицу», битое
    // место = «решает CLI»). Ответ отдаёт УДАЛЁННЫЙ пресет и места, где он был выбран
    // на момент удаления (по роли вызывающего — см. PresetUsageList), — фронт рисует
    // итоговый диалог («Цепочка «…» удалена. Была выбрана в N местах»). Идущие ходы
    // не роняет: цепочка хода разворачивается при его запуске и в память процесса уже
    // материализована (FallbackLlmSessionAdapter), имя/id пресета в рантайме не читаются.
    [HttpDelete("presets/{id}")]
    public IActionResult DeletePreset(string id)
    {
        var ownerId = UserId ?? "";
        var located = PresetStore.Find(specialty, ownerId, id);
        if (located is null)
            return NotFound(new { error = "Пресет не найден" });
        if (located.Scope != PresetScope.Owner && !IsAdmin)
            return Forbid();

        // Места считаем ДО удаления: после него RefersTo уже не найдёт ссылок в этом слое
        var usages = PresetUsageList(id);
        PresetStore.Delete(specialty, ownerId, id);
        return Ok(new
        {
            preset = new { id = located.Preset.Id, name = located.Preset.Name },
            scope = located.Scope switch
            {
                PresetScope.Global => "global",
                PresetScope.User => "user",
                _ => "owner",
            },
            count = usages.Count,
            usages,
        });
    }

    // Список мест использования пресета — общая часть usage-эндпоинта и удаления.
    // Per-owner изоляция: не-админ получает только места СВОЕГО слоя (личные слоты,
    // свои персоны, матрицы личного и назначенного ему user-слоя) и общие места
    // инстанса (слоты, глобальные специальности, места каталога) — без имён и id
    // других пользователей; полный список (все владельцы, все назначения B9, все
    // персоны) — только админу: чьё где выбрано — админская информация.
    private List<ModelPresetUsage> PresetUsageList(string id)
    {
        var ownerId = UserId ?? "";
        var isAdmin = IsAdmin;
        var usages = new List<ModelPresetUsage>();
        var app = appSettings.Get();
        AddSlot(usages, app.ModelTierStrong, "Сильная", "Модели по умолчанию", null, id);
        AddSlot(usages, app.ModelTierMedium, "Средняя", "Модели по умолчанию", null, id);
        AddSlot(usages, app.ModelTierWeak, "Слабая", "Модели по умолчанию", null, id);
        foreach (var u in users.GetAll())
        {
            // Чужие слоты — только админу: «Модели · {имя}» раскрывает коллег и их настройки
            if (!isAdmin && u.Id != ownerId) continue;
            var whose = u.Id == ownerId ? "Мои модели" : $"Модели · {u.DisplayName ?? u.Username}";
            AddSlot(usages, u.ModelTierStrong, "Сильная", whose, u.Id, id);
            AddSlot(usages, u.ModelTierMedium, "Средняя", whose, u.Id, id);
            AddSlot(usages, u.ModelTierWeak, "Слабая", whose, u.Id, id);
        }
        var file = specialty.Snapshot;
        ScanSpecialtyLayer(usages, file.Global, null, id);
        if (isAdmin)
        {
            foreach (var (oid, layer) in file.Owners)
                ScanSpecialtyLayer(usages, layer, oid, id);
            foreach (var (uid, layer) in file.Users)
                ScanSpecialtyLayer(usages, layer, uid, id);
        }
        else
        {
            // Свой личный слой и назначенный ему (B9) — подписи без чужих имён
            if (file.Owners.TryGetValue(ownerId, out var own))
                ScanSpecialtyLayer(usages, own, ownerId, id);
            if (file.Users.TryGetValue(ownerId, out var user))
                ScanSpecialtyLayer(usages, user, ownerId, id);
        }
        foreach (var p in personas.GetAllInternal())
        {
            // Чужие персоны — только админу: имена и настройки персон per-owner
            if (!isAdmin && p.OwnerId != ownerId) continue;
            if (RefersTo(p.Model, id))
                usages.Add(new ModelPresetUsage("persona-model", $"Персона «{p.Name}» — всегда работает", p.OwnerId));
            AddTierCells(usages, p.TierStrong, p.TierMedium, p.TierWeak,
                $"Персона «{p.Name}»", "persona-cell", p.OwnerId, id);
        }
        foreach (var (key, route) in localActions.All)
        {
            if (!RefersTo(route, id)) continue;
            var title = LocalActionCatalog.Find(key)?.Title ?? key;
            usages.Add(new ModelPresetUsage("place", $"Место «{title}»", null));
        }
        return usages;
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
