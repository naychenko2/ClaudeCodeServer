using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Personas;
using ClaudeHomeServer.Services.TriggerSources;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services;

// Оркестрация CRUD персон, вынесенная из PersonasController (ADR-012, фаза 2 волна 2):
// тяжёлые сценарии (создание/правка/удаление/дефолт, AI-команда, подбор привязок,
// аватар, автоматизации) нужны ДВУМ потребителям — REST-контроллеру и http-тулсету
// PersonasToolset — и потому обязаны жить одной копией. Тела перенесены из контроллера
// дословно: UserId → параметр userId, заголовок X-Caller-Session-Id → callerSessionId,
// HttpContext.RequestAborted → ct; статусы и тексты ответов прежние байт-в-байт —
// на этих путях стоят тесты контроллера. MVC-гейт [DenyOnDelegatedTurn] НЕ переносится:
// он остаётся атрибутом на REST-экшенах, а http-тулсет зовёт DelegatedTurnGate сам.
public sealed class PersonasCrudService(
    PersonaManager personas,
    ProjectManager projects,
    SessionManager sessions,
    UserStore users,
    PersonaMemoryService memory,
    PersonaBindingsService bindings,
    NotesService notes,
    SkillsService skills,
    Services.Images.ImageGenerationService images,
    Services.Images.ImageBackfillService imageBackfill,
    Services.Llm.OneShotClaudeRunner oneShot,
    Services.Llm.ICheapTextRunner cheap,
    SpecialtyTemplatesService specialtyTemplates,
    SpecialtySettingsStore specialtySettings,
    IConfiguration config,
    ILogger<PersonasCrudService> log,
    IHubContext<SessionHub> hub)
{
    // Провайдеров генерации несколько (fal.ai, glif) — про конкретный ключ конфига не пишем
    private const string ImageGenerationOffError =
        "Генерация изображений не настроена: ни один провайдер (fal.ai, glif) не подключён";

    // MVC-хелперы с именами ControllerBase: сервис — не контроллер, а тела перенесённых
    // экшенов обязаны остаться копией 1:1 (включая формы ответов)
    private static ObjectResult BadRequest(object value) => new BadRequestObjectResult(value);
    private static NotFoundResult NotFound() => new();
    private static NotFoundObjectResult NotFound(object value) => new(value);
    private static ObjectResult StatusCode(int statusCode, object value) =>
        new(value) { StatusCode = statusCode };
    private static OkObjectResult Ok(object value) => new(value);
    private static NoContentResult NoContent() => new();

    private Task Broadcast(string userId, string action, string? personaId = null) =>
        hub.Clients.Group("user_" + userId)
            .SendAsync("message", new PersonasChangedMessage(action, personaId));

    // --- Создание / правка / удаление / дефолт (тела POST/PUT/DELETE/make-default) ---

    public async Task<ActionResult<Persona>> CreateAsync(string userId, CreatePersonaRequest req,
        string? callerSessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest("Не задано имя персоны");

        var scope = req.Scope ?? PersonaScope.Global;
        if (scope == PersonaScope.Project && !ValidProject(userId, req.ProjectId))
            return BadRequest("Для проектной персоны нужен корректный projectId");
        if (!TryParseAccess(req.Access, out var access))
            return BadRequest("Неверный профиль доступа (ожидается full | readOnly | custom)");
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });
        if (!IsValidTierCell(req.TierStrong) || !IsValidTierCell(req.TierMedium) || !IsValidTierCell(req.TierWeak))
            return BadRequest(new { error = "Модель уровня — это id модели или preset:{id}; tier:* здесь нельзя" });
        if (PersonaManager.ExceedsContractLimit(req.Contract, req.SystemPrompt, 0, out var tooBig))
            return BadRequest(new { error = tooBig });

        // Серверный предохранитель знакомства (план 2.9): в личном знакомстве (сессия с
        // OnboardingKind == "user") нельзя создавать НОВУЮ персону — интервью дорабатывает
        // заготовку через personas_update. Срабатывает ТОЛЬКО когда вызов пришёл из
        // user-онбординг-сессии И AssistantPersonaId резолвится в ЖИВУЮ персону. Резолв, а не
        // проверка на непустоту: иначе висячий id запирал бы знакомство навсегда (создать нельзя,
        // обновить некого). Проектное знакомство создаёт руководителя штатно — его не трогаем.
        if (callerSessionId is { Length: > 0 } guardCsid
            && sessions.GetOwned(guardCsid, userId) is { OnboardingKind: OnboardingKinds.User }
            && users.GetById(userId)?.AssistantPersonaId is { } liveAssistantId
            && personas.Get(liveAssistantId, userId) is not null)
        {
            return BadRequest(new
            {
                error = $"В знакомстве нельзя создавать новую персону — дорабатывай существующего " +
                    $"ассистента через personas_update (id: {liveAssistantId})",
            });
        }

        // Явные привязки валидируем ДО создания персоны — ошибка не оставляет полусозданную.
        // Персона ещё не существует — для само-проверки ProjectPersonas/ProjectTasks передаём
        // лёгкую заглушку с планируемыми Scope/ProjectId (полноценная персона не нужна).
        var draftOwner = new Persona { Scope = scope, ProjectId = req.ProjectId };
        var bindingList = new List<PersonaBinding>();
        if (req.Bindings is { Count: > 0 })
        {
            foreach (var b in req.Bindings)
            {
                var (binding, parseError) = ParseBinding(b);
                if (binding is null) return BadRequest(new { error = parseError });
                var err = await bindings.ValidateAsync(userId, binding, bindingList, draftOwner);
                if (err is not null) return BadRequest(new { error = err });
                bindingList.Add(binding);
            }
        }

        // Шаблон специальности: при выборе специальности неподставленные
        // access/tools/disallowedTools берутся из эффективного шаблона;
        // явные поля запроса всегда побеждают, после создания поля правятся вручную.
        var createSpecialty = req.Specialty ?? PersonaSpecialty.None;
        var templated = specialtyTemplates.Apply(userId, createSpecialty, currentSpecialty: null,
            access, req.Tools, req.DisallowedTools);

        Persona persona;
        try
        {
            persona = personas.Create(userId, req.Name, req.Role, req.Description, req.SystemPrompt,
                req.Model, req.Effort, scope, req.ProjectId, req.Color, req.Greeting,
                req.MemoryEnabled ?? true, templated.Tools, req.Contract,
                templated.Access ?? PersonaAccess.Full, templated.DisallowedTools, createSpecialty,
                req.AllProjectsAccess ?? false, req.Handle, req.ModelTier,
                req.TierStrong, req.TierMedium, req.TierWeak);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        if (bindingList.Count > 0)
            persona = personas.UpdateBindings(persona.Id, userId, bindingList);
        // Проектной персоне — сразу дефолтные привязки к данным её проекта (файлы/заметки/знания)
        persona = bindings.SeedProjectDefaults(userId, persona);
        // Типовые умения специальности: профиль роли (EffectiveDefaultBindings, дефолт —
        // SpecialtyPromptPresets) материализуется в личные привязки персоны. Модель «копия
        // при создании»: смена дефолта роли существующих персон не трогает. Профиль — более
        // конкретная форма авто-подбора: когда он есть, общий autoBindings не нужен.
        // Сбой материализации не роняет создание персоны (как и авто-подбор ниже):
        // умения добавит кнопка «Применить типовые».
        var (withDefaults, defaultsApplied) = (persona, 0);
        try
        {
            (withDefaults, defaultsApplied) = await MaterializeDefaultBindingsAsync(userId, persona, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "типовые умения: материализация при создании {Persona} не удалась", persona.Id);
        }
        persona = withDefaults;
        // Авто-подбор привязок (autoBindings) — best-effort:
        // сбой подбора не роняет создание, персона остаётся без привязок
        if (defaultsApplied == 0 && req.AutoBindings == true)
            persona = await TryAutoBindAsync(userId, persona, ct);
        // Фото-аватар (autoAvatar) — явный опт-ин для путей, где человек не выбирает
        // аватар сам (напр. пакетное создание команды из ai/team); ручное создание
        // через форму/мастер этот параметр не шлёт — там инициалы или явный выбор
        if (req.AutoAvatar == true)
            persona = await TryAutoGenerateAvatarAsync(userId, persona, req.AvatarPrompt);
        // Персона, созданная из онбординг-сессии (через MCP personas_create), запоминается на ней:
        // финализация досеет профиль дефолта ТОЛЬКО ей, а не выбранной существующей
        // (та прав не получает — молчаливая дозапись Access=Full+manage была бы эскалацией).
        if (callerSessionId is { Length: > 0 } csid)
        {
            var caller = sessions.GetOwned(csid, userId);
            if (caller?.OnboardingKind is not null)
                sessions.SetOnboardingCreatedPersona(csid, userId, persona.Id);
        }
        await Broadcast(userId, "created", persona.Id);
        return Ok(persona);
    }

    public async Task<ActionResult<Persona>> UpdateAsync(string userId, string id,
        UpdatePersonaRequest req, string? callerSessionId)
    {
        if (personas.Get(id, userId) is not { } current) return NotFound();
        if (req.Scope == PersonaScope.Project && !ValidProject(userId, req.ProjectId))
            return BadRequest("Для проектной персоны нужен корректный projectId");
        // Любой непустой projectId (в т.ч. при partial-update без scope) — только свой проект
        if (!string.IsNullOrEmpty(req.ProjectId) && !ValidProject(userId, req.ProjectId))
            return BadRequest("Проект не найден или недоступен");
        if (!TryParseAccess(req.Access, out var access))
            return BadRequest("Неверный профиль доступа (ожидается full | readOnly | custom)");
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });
        if (!IsValidTierCell(req.TierStrong) || !IsValidTierCell(req.TierMedium) || !IsValidTierCell(req.TierWeak))
            return BadRequest(new { error = "Модель уровня — это id модели или preset:{id}; tier:* здесь нельзя" });
        // Partial-update: null-поля не меняются, поэтому размер считаем по эффективному контракту.
        // Порог — только на рост: у раздутой персоны остаётся право сохранить сокращение
        if (req.Contract is not null || req.SystemPrompt is not null)
        {
            var currentSize = PersonaManager.ContractSize(current.Contract, current.SystemPrompt);
            if (PersonaManager.ExceedsContractLimit(req.Contract ?? current.Contract,
                    req.SystemPrompt ?? current.SystemPrompt, currentSize, out var tooBig))
                return BadRequest(new { error = tooBig });
        }

        // Шаблон специальности: применяется только при реальной СМЕНЕ специальности;
        // неподставленные access/tools/disallowedTools берутся из шаблона, явные поля
        // запроса побеждают. Та же специальность в запросе — поля не трогает.
        var templated = specialtyTemplates.Apply(userId, req.Specialty ?? current.Specialty, current.Specialty,
            access, req.Tools, req.DisallowedTools);

        // Статус заготовки снимает только ЧЕЛОВЕК из раздела «Персоны». Правка, пришедшая из
        // личного знакомства владельца, статус НЕ снимает: интервью само зовёт personas_update
        // на шаге доработки — задолго до финального personas_set_default. Сняв маркер там, мы
        // ломали бы три вещи разом: предохранитель Create переставал держать (модель могла
        // создать вторую персону), apply-transcript отвечал 404 ровно в сценарии «модель не
        // довела интервью до финала», а возврат к чату знакомства деградировал промпт к
        // «создай ассистента» поверх уже настроенного. Проверка — та же, что у предохранителя
        // в Create: вызов из user-онбординг-сессии этого владельца.
        var fromUserIntro = callerSessionId is { Length: > 0 } introCsid
            && sessions.GetOwned(introCsid, userId) is { OnboardingKind: OnboardingKinds.User };
        var isAssistantDraft = !fromUserIntro && users.GetById(userId)?.AssistantPersonaId == id;
        // Снимок характер-релевантных полей ДО мутации: personas.Get отдаёт живую ссылку
        // (не копию), а personas.Update правит персону in-place — current и persona ниже
        // оказались бы ОДНИМ объектом, и сравнение «после == после» никогда не показало бы
        // изменений (дефект нашли тесты AssistantStatusTests). Строки/объект контракта снимаем
        // заранее — сама строка/контракт не мутируется, его лишь переприсваивают новым значением.
        var beforeName = current.Name;
        var beforeRole = current.Role;
        var beforeGreeting = current.Greeting;
        var beforeContract = current.Contract;
        Persona persona;
        try
        {
            persona = personas.Update(id, userId, req.Name, req.Role, req.Description, req.SystemPrompt,
                req.Model, req.Effort, req.Scope, req.ProjectId, req.Color, req.Greeting,
                req.MemoryEnabled, templated.Tools, req.Contract, templated.Access, templated.DisallowedTools,
                req.Specialty, req.AllProjectsAccess, req.Handle, req.ModelTier,
                req.TierStrong, req.TierMedium, req.TierWeak);
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        // Ручная правка заготовки снимает её статус (план 2.8): если у нетронутой заготовки
        // меняется Name/Role/Greeting или любой слот характера — обнуляем AssistantPersonaId.
        // Иначе карточка-приглашение «ассистент пока стандартный» горела бы над персоной,
        // которую человек только что настроил руками, а «Познакомиться» предлагало бы интервью,
        // перезаписывающее его правки. Посторонние поля (цвет, модель, аватар, привязки) не снимают.
        if (isAssistantDraft && IsCharacterRelevantChange(beforeName, beforeRole, beforeGreeting, beforeContract, persona))
            users.SetAssistantPersona(userId, null);
        await Broadcast(userId, "updated", id);
        return Ok(persona);
    }

    public async Task<IActionResult> DeleteAsync(string userId, string id, string? successorId)
    {
        if (personas.Get(id, userId) is null) return NotFound();

        var me = users.GetById(userId);
        var isAssistantDraft = me?.AssistantPersonaId == id;

        // Заготовка-ассистент (план 2.7, решение §3в): AssistantPersonaId обнуляется ВСЕГДА при
        // удалении заготовки — иначе поле повисает на мёртвом id и запирает знакомство навсегда.
        // Если заготовка к тому же дефолт владельца — разрешаем удаление БЕЗ преемника и обнуляем
        // дефолт: у нового пользователя единственная глобальная персона — заготовка, и требование
        // преемника сделало бы её неудаляемой. Следующее создание чата заведёт нового ассистента
        // (рубеж 2.4). Преемник нужен только обычной дефолт-персоне (не заготовке).
        if (isAssistantDraft)
        {
            users.SetAssistantPersona(userId, null);
            if (me?.DefaultPersonaId == id)
            {
                users.SetDefaultPersona(userId, null);
                await Broadcast(userId, "default", null);
            }
        }
        else
        {
            // Дефолт-персона удаляется только с преемником по той же зоне — остаться без
            // дефолта нельзя (единственная точка каскада)
            var isUserDefault = me?.DefaultPersonaId == id;
            var defaultOfProjects = projects.GetByOwner(userId)
                .Where(p => p.DefaultPersonaId == id).ToList();
            if (isUserDefault || defaultOfProjects.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(successorId))
                    return BadRequest(new { error = "Это дефолт-персона: выберите преемника" });
                var successor = personas.Get(successorId, userId);
                if (successor is null || successor.Id == id)
                    return BadRequest(new { error = "Преемник не найден или совпадает с удаляемой персоной" });
                if (isUserDefault && successor.Scope != PersonaScope.Global)
                    return BadRequest(new { error = "Преемником личной дефолт-персоны может быть только глобальная персона" });
                foreach (var project in defaultOfProjects)
                    if (successor.Scope != PersonaScope.Project || successor.ProjectId != project.Id)
                        return BadRequest(new { error = $"Преемником руководителя проекта «{project.Name}» может быть только персона этого проекта" });

                if (isUserDefault) users.SetDefaultPersona(userId, successor.Id);
                foreach (var project in defaultOfProjects)
                    projects.SetDefaultPersona(project.Id, successor.Id);
                await Broadcast(userId, "default", successor.Id);
            }
        }

        if (!personas.Delete(id, userId)) return NotFound();
        // Чистим долгую память персоны: Dify-датасет + data/persona-memory.json (иначе осиротят)
        await memory.DeletePersonaAsync(id);
        await Broadcast(userId, "deleted", id);
        return NoContent();
    }

    // Назначить персону дефолтной: глобальную — личным дефолтом владельца
    // (User.DefaultPersonaId), проектную — дефолтом её проекта
    // (Project.DefaultPersonaId). REST из UI свободен; вызов из чата (callerSessionId
    // от MCP personas_set_default) разрешён ТОЛЬКО онбординг-сессии — иначе любая
    // manage-персона могла бы переназначить дефолт владельца из любого разговора.
    // Из онбординг-сессии назначение финализирует онбординг: досев профиля дефолта,
    // «просыпание» персоны в том же чате, событие onboarding_completed.
    public async Task<IActionResult> MakeDefaultAsync(string userId, string id, string? callerSessionId)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();

        // MCP-гейт: ход из чата обязан идти из онбординг-сессии (смена дефолта — в настройках)
        Session? onboarding = null;
        if (callerSessionId is { Length: > 0 } callerSessionIdValue)
        {
            onboarding = sessions.GetOwned(callerSessionIdValue, userId);
            if (onboarding?.OnboardingKind is null)
                return BadRequest(new
                {
                    error = "Назначение дефолт-персоны из чата доступно только сессии онбординга. "
                        + "Смена дефолта выполняется в настройках — попроси об этом пользователя.",
                });
        }

        if (persona.Scope == PersonaScope.Global)
        {
            users.SetDefaultPersona(userId, id);
        }
        else
        {
            var project = persona.ProjectId is null ? null : projects.GetById(persona.ProjectId);
            if (project is null || project.OwnerId != userId)
                return BadRequest(new { error = "Проект персоны не найден или недоступен" });
            projects.SetDefaultPersona(project.Id, id);
        }

        if (onboarding is not null)
            await FinalizeOnboardingAsync(userId, onboarding, persona);

        // Событие и для инвалидации кэша auth.me / DTO проекта на фронте:
        // смена дефолта видна резолверу аватаров без перезагрузки
        await Broadcast(userId, "default", id);
        return Ok(persona);
    }

    // Финализация онбординга (make-default пришёл из онбординг-сессии): досев профиля
    // дефолт-персоны, у пользовательского — «просыпание» персоны в этом же чате
    // (SetPersona: адаптер лениво пересоберётся, активный ход не рвётся) и очистка
    // User.OnboardingSessionId; у проектного — очистка Project.OnboardingSessionId,
    // но только когда каркас больше не предлагают (знакомство v2, п.5): пока
    // PresetKey == "pending", точка входа обязана возвращать ту же сессию — иначе повторный
    // start завёл бы вторую сессию «Знакомство с проектом» с новым kickoff поверх живой.
    private async Task FinalizeOnboardingAsync(string userId, Session onboarding, Persona persona)
    {
        // Повторная финализация из живой сессии — no-op: событие onboarding_completed и
        // телеметрия уже ушли, досев идемпотентен, но второй карточки в ленте быть не должно
        // (критерий приёмки п.5). Флаг ставится здесь первым и переживает рестарт (sessions.json).
        if (onboarding.OnboardingFinalized) return;
        sessions.SetOnboardingFinalized(onboarding.Id, userId);

        // Досев профиля дефолта (Coordinator+Full+manage) — ТОЛЬКО персоне, созданной в этом
        // онбординге (через personas_create). Выбранная существующая персона прав НЕ получает:
        // молчаливая дозапись Access=Full+manage была бы тихой эскалацией (как и ручная смена
        // дефолта из настроек) — пользователь назначает роль, а не соглашается расширить права.
        if (onboarding.OnboardingCreatedPersonaId is { Length: > 0 } seededId
            && seededId == persona.Id)
        {
            persona = bindings.SeedDefaultPersonaProfile(userId, persona);
        }
        if (onboarding.OnboardingKind == OnboardingKinds.User)
        {
            sessions.SetPersona(onboarding.Id, userId, persona.Id);
            // Финализация знакомства (план 2.6): фиксируем момент завершения и снимаем статус
            // заготовки — ассистент превратился в «своего». IntroCompletedAt гасит карточку-
            // приглашение, обнулённый AssistantPersonaId убирает метку и при будущей смене дефолта.
            users.SetIntroCompleted(userId, DateTime.UtcNow);
            users.SetAssistantPersona(userId, null);
            users.SetOnboardingSession(userId, null);
        }
        else if (onboarding.OnboardingKind == OnboardingKinds.Project && onboarding.ProjectId is { } pid)
        {
            var project = projects.GetById(pid);
            // PresetKey == "pending" → сессию не чистим: надстройка сценария живёт до
            // применения/отказа каркаса, и точка входа должна резюмить именно её
            if (project?.PresetKey != ProjectPreset.Pending)
                projects.SetOnboardingSession(pid, null);
        }
        // Гейт на фронте снимается не по этому событию mid-turn, а по концу хода (result)
        // или кнопке «Перейти в систему» — событие лишь помечает завершение
        await sessions.BroadcastSessionMessageAsync(onboarding.Id,
            new OnboardingCompletedMessage(onboarding.OnboardingKind!, persona.Id, onboarding.ProjectId));
        // Телеметрия знакомства (план 2.10): без разрезов по пользователю.
        Telemetry.ServerMetrics.RecordIntroCompleted();
    }

    // --- AI-команда ---

    // AI-формирование команды: по промпту + контексту проекта (CLAUDE.md) LLM предлагает набор
    // персон (роль/имя/характер/специальность) для создания в команде проекта. Возвращает
    // черновики — фронт показывает их для одобрения, затем создаёт через обычный POST /api/personas.
    public async Task<ActionResult> AiTeamAsync(string userId, string projectId, string prompt,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return BadRequest(new { error = "Опишите, какая команда нужна" });
        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != userId)
            return BadRequest(new { error = "Проект не найден" });

        var model = oneShot.NormalizeModel(config["Notes:AiModel"] ?? config["Tasks:AiModel"] ?? "haiku");
        try
        {
            var raw = await cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaAiTeam,
                BuildTeamPrompt(project, prompt), model, userId, jsonFormat: "json", ct: ct);
            var drafts = ParseTeamDrafts(raw);
            if (drafts is null || drafts.Count == 0)
            {
                log.LogWarning("ai/team: команда не распознана; сырой ответ: {Raw}",
                    raw.Length > 600 ? raw[..600] + "…" : raw);
                return StatusCode(502, new { error = "Модель не вернула состав команды — попробуйте уточнить промпт" });
            }
            return Ok(new { members = drafts });
        }
        catch (Exception ex)
        {
            return StatusCode(502, new { error = $"Не удалось сформировать команду: {ex.Message}" });
        }
    }

    private static string BuildTeamPrompt(Models.Project project, string userPrompt)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Ты помогаешь сформировать команду AI-ассистентов (персон) для проекта. " +
                      "Проанализируй проект и промпт пользователя и предложи сбалансированный состав " +
                      "из 3-6 персон, перекрывающих ключевые роли команды.");
        sb.AppendLine($"Проект: {project.Name}.");
        if (!string.IsNullOrWhiteSpace(project.SystemPrompt))
            sb.AppendLine($"Контекст проекта (CLAUDE.md):\n{project.SystemPrompt!.Trim()}");
        sb.AppendLine($"Запрос пользователя: {userPrompt.Trim()}");
        sb.AppendLine("\nВерни ТОЛЬКО JSON-массив (без пояснений и markdown) объектов с полями:");
        sb.AppendLine("  role — роль по-русски, 1-3 слова (напр. «Аналитик», «Исполнитель»);");
        sb.AppendLine("  name — русское имя-человека (одно слово);");
        sb.AppendLine("  description — кратко «кто это», 3-8 слов;");
        sb.AppendLine("  character — характер и стиль общения, обращение на «ты», 2-4 предложения;");
        sb.AppendLine("  tone — тон одной короткой фразой;");
        sb.AppendLine("  specialty — одна из: analyst, planner, reviewer, executor, secretary, coordinator, mentor, designer, consultant, librarian;");
        sb.AppendLine("  color — один из: yellow, orange, blue, green, purple, red, brown, cyan, pink;");
        sb.AppendLine("  greeting — первое приветствие персоны, 1-2 предложения;");
        sb.AppendLine("  avatarPrompt — описание внешности для фотопортрета, по-английски, 5-15 слов (пол, возраст, стиль, настроение, фон).");
        sb.AppendLine("По возможности включи роли для конвейера (аналитик/планировщик/ревьюер/исполнитель), если уместно проекту. Всё по-русски. НЕ упоминай имя модели.");
        return sb.ToString();
    }

    // Парс JSON-массива черновиков команды (устойчиво к преамбуле/markdown; fallback — одиночный объект)
    private static List<TeamMemberDraft>? ParseTeamDrafts(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        if (start < 0)
        {
            var single = PersonaDraftService.ParseJsonObject<TeamMemberDraft>(raw);
            return single is null ? null : [single];
        }
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<TeamMemberDraft>>(raw[start..(i + 1)],
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { return null; }
            }
        }
        return null;
    }

    // --- Аватар ---

    // Сгенерировать НЕСКОЛЬКО вариантов аватар-фото по описанию (для выбора); провайдера
    // и модель выбирает роутер по настройке инстанса. Кандидаты сохраняются во временную
    // папку, аватар персоны НЕ меняется до выбора.
    // prompt пуст → строим фото-промпт из имени/описания персоны.
    public async Task<ActionResult> GenerateAvatarAsync(string userId, string id, GenerateAvatarRequest req)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();

        var prompt = string.IsNullOrWhiteSpace(req.Prompt)
            ? BuildAvatarPrompt(persona)
            : $"Photorealistic portrait photo. {req.Prompt.Trim()}";
        var count = req.Count is >= 1 and <= 4 ? req.Count.Value : 4;

        // Очередь снимает заявку, как только у сущности есть картинка (Resolve → HasImage),
        // поэтому перерисовка УЖЕ стоящего аватара фоном не догоняется — обещать «появится
        // сам» здесь нельзя, отказ остаётся отказом.
        var canBackfill = persona.Avatar.Kind != PersonaAvatarKind.Image
            || string.IsNullOrEmpty(persona.Avatar.ImageFile);

        // Провайдера нет — отвечаем отказом, но аватар не теряем: заявка догонит его,
        // как только генерацию настроят
        if (!images.EnabledFor(Services.Images.ImagePlaces.PersonaAvatar))
        {
            if (canBackfill) imageBackfill.Enqueue(Services.Images.ImageBackfillKinds.PersonaAvatar, id, userId, prompt);
            return BadRequest(new { error = ImageGenerationOffError, queued = canBackfill });
        }

        var generated = await images.GenerateManyAsync(Services.Images.ImagePlaces.PersonaAvatar, prompt, count);
        // Провайдер не отдал картинок (таймаут, отказ) — аватар тоже не теряем: заявка
        // догонит персону фоном. queued в ответе — чтобы фронт сказал человеку честное
        // «появится сам», а не «генератор не ответил, аватар остался прежним».
        if (generated.Count == 0)
        {
            if (canBackfill) imageBackfill.Enqueue(Services.Images.ImageBackfillKinds.PersonaAvatar, id, userId, prompt);
            return StatusCode(502, new { error = "Не удалось сгенерировать изображение", queued = canBackfill });
        }

        // Свежая папка кандидатов (перезатираем прошлую генерацию)
        var candDir = Path.Combine(personas.AssetsDir, id, "candidates");
        try { if (Directory.Exists(candDir)) Directory.Delete(candDir, recursive: true); } catch { }
        Directory.CreateDirectory(candDir);

        var files = new List<string>();
        foreach (var img in generated)
        {
            var ext = ImageAssetHelper.ExtFor(img.ContentType);
            var name = $"cand-{Guid.NewGuid():N}{ext}";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(candDir, name), img.Bytes);
            files.Add(name);
        }
        return Ok(new { candidates = files });
    }

    // Выбрать кандидата как аватар персоны: делаем основным, чистим остальных кандидатов.
    public async Task<ActionResult<Persona>> SelectAvatarAsync(string userId, string id, SelectAvatarRequest req)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.File)) return BadRequest(new { error = "Не указан файл" });

        var dir = Path.Combine(personas.AssetsDir, id);
        var candPath = Path.Combine(dir, "candidates", Path.GetFileName(req.File));
        if (!System.IO.File.Exists(candPath)) return NotFound(new { error = "Кандидат не найден" });

        var ext = Path.GetExtension(candPath);
        var fileName = $"avatar-{Guid.NewGuid():N}{ext}";   // cache-busting
        System.IO.File.Copy(candPath, Path.Combine(dir, fileName), overwrite: true);

        // Удаляем прежний аватар и всю папку кандидатов
        if (!string.IsNullOrEmpty(persona.Avatar.ImageFile))
            try { System.IO.File.Delete(Path.Combine(dir, persona.Avatar.ImageFile)); } catch { }
        try { Directory.Delete(Path.Combine(dir, "candidates"), recursive: true); } catch { }

        var updated = personas.SetAvatarImage(id, userId, fileName);
        await Broadcast(userId, "updated", id);
        return Ok(updated);
    }

    // Фото-промпт аватара по умолчанию — из имени и описания персоны
    private static string BuildAvatarPrompt(Persona persona)
    {
        var who = string.IsNullOrWhiteSpace(persona.Description)
            ? persona.Name
            : $"{persona.Name}, {persona.Description}";
        return $"Photorealistic portrait photo of {who}. Head and shoulders, looking at camera, " +
               "clean solid background, soft studio lighting, natural skin, friendly expression, " +
               "high detail, sharp focus, square crop.";
    }

    // Фото-аватар автоматически, best-effort (создание персоны не срывается из-за картинки).
    // Общий путь для всех авто/LLM-сценариев создания, где человек не выбирает аватар сам —
    // quick-create одной персоны, пакетная команда из ai/team.
    // Не получилось (провайдера нет, отказ, сбой) — персона остаётся с инициалами, а аватар
    // догонит её из очереди, когда генерация заработает.
    public async Task<Persona> TryAutoGenerateAvatarAsync(string userId, Persona persona, string? avatarPrompt)
    {
        var prompt = string.IsNullOrWhiteSpace(avatarPrompt)
            ? BuildAvatarPrompt(persona)
            : $"Photorealistic portrait photo. {avatarPrompt.Trim()}";

        if (!images.EnabledFor(Services.Images.ImagePlaces.PersonaAvatar)) return EnqueueAvatarBackfill(userId, persona, prompt);
        try
        {
            var generated = await images.GenerateManyAsync(Services.Images.ImagePlaces.PersonaAvatar, prompt, 1);
            if (generated.Count == 0) return EnqueueAvatarBackfill(userId, persona, prompt);
            var dir = Path.Combine(personas.AssetsDir, persona.Id);
            Directory.CreateDirectory(dir);
            var fileName = $"avatar-{Guid.NewGuid():N}{ImageAssetHelper.ExtFor(generated[0].ContentType)}";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(dir, fileName), generated[0].Bytes);
            return personas.SetAvatarImage(persona.Id, persona.OwnerId, fileName);
        }
        catch
        {
            return EnqueueAvatarBackfill(userId, persona, prompt);
        }
    }

    private Persona EnqueueAvatarBackfill(string userId, Persona persona, string prompt)
    {
        imageBackfill.Enqueue(Services.Images.ImageBackfillKinds.PersonaAvatar,
            persona.Id, string.IsNullOrEmpty(persona.OwnerId) ? userId : persona.OwnerId, prompt);
        return persona;
    }

    // --- Привязки ---

    // Полная замена набора привязок (PUT-семантика; дёргается MCP personas_bindings_set)
    public async Task<ActionResult<IReadOnlyList<PersonaBinding>>> SetBindingsAsync(string userId,
        string id, List<PersonaBindingRequest>? bindingRequests)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();

        var list = new List<PersonaBinding>();
        foreach (var b in bindingRequests ?? [])
        {
            var (binding, parseError) = ParseBinding(b);
            if (binding is null) return BadRequest(new { error = parseError });
            var err = await bindings.ValidateAsync(userId, binding, list, persona);
            if (err is not null) return BadRequest(new { error = err });
            list.Add(binding);
        }
        var updated = personas.UpdateBindings(id, userId, list);
        await Broadcast(userId, "updated", id);
        return Ok(updated.Bindings ?? []);
    }

    // Авто-подбор и сохранение привязок для свежесозданной персоны (best-effort).
    // Подобранное ДОПОЛНЯЕТ существующие (явные из запроса и посевные): UpdateBindings
    // заменяет список целиком, отдавать ему только новых кандидатов — молчаливая потеря.
    public async Task<Persona> TryAutoBindAsync(string userId, Persona persona, CancellationToken ct = default)
    {
        try
        {
            var candidates = await SuggestBindingsAsync(userId, persona, ct: ct);
            if (candidates.Count > 0)
                return personas.UpdateBindings(persona.Id, userId,
                    (persona.Bindings ?? []).Concat(candidates).ToList());
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "autoBindings: подбор привязок для {Persona} не удался", persona.Id);
        }
        return persona;
    }

    // Типовые умения специальности → личные привязки персоны («копия при создании» и
    // кнопка «Применить типовые» для существующих). Скиллы материализуются напрямую из
    // каталога владельца (отсутствующие пропускаются молча — каталог у каждого свой),
    // остальные типы — one-shot AI-подбор конкретных целей. Дубликаты и недоступные цели
    // отбрасывает валидация. Возвращает персону и число ДОБАВЛЕННЫХ привязок (0 — профиль
    // пуст или ничего не подошло). Сбой (например, недоступность AI-подбора) пробрасывает
    // наружу: вызывающий сам решает семантику — создание персоны глотает его (best-effort),
    // а кнопка «Применить типовые» отдаёт ошибку, иначе её ветка «Не удалось применить»
    // недостижима (до записи привязок сбой ничего не меняет).
    public async Task<(Persona Persona, int Applied)> MaterializeDefaultBindingsAsync(string userId,
        Persona persona, CancellationToken ct = default)
    {
        if (persona.Specialty == PersonaSpecialty.None) return (persona, 0);
        var profile = specialtySettings.EffectiveDefaultBindings(userId, persona.Specialty);
        if (profile.Count == 0) return (persona, 0);

        var accepted = new List<PersonaBinding>(persona.Bindings ?? []);
        var added = new List<PersonaBinding>();

        // Скиллы — явная цель профиля, AI не нужен
        foreach (var entry in profile.Where(e => e.Type == PersonaBindingType.Skill))
        {
            var binding = new PersonaBinding
            {
                Type = PersonaBindingType.Skill,
                Target = entry.SkillName?.Trim() ?? "",
                Condition = entry.Condition,
                Mode = entry.Mode,
            };
            if (await bindings.ValidateAsync(userId, binding, accepted, persona) is not null) continue;
            accepted.Add(binding);
            added.Add(binding);
        }

        var aiEntries = profile.Where(e => e.Type != PersonaBindingType.Skill).ToList();
        if (aiEntries.Count > 0)
            added.AddRange(await SuggestBindingsAsync(userId, persona, profile: aiEntries, acceptedSeed: accepted, ct: ct));

        if (added.Count > 0)
            persona = personas.UpdateBindings(persona.Id, userId,
                (persona.Bindings ?? []).Concat(added).ToList());
        return (persona, added.Count);
    }

    // Подбор кандидатов-привязок: каталог целей владельца + профиль персоны → one-shot LLM
    // (строгий JSON-массив, ретрай как в quick-create), невалидные кандидаты отбрасываются.
    // userPrompt задан — генерация под свободный запрос пользователя, иначе подбор под роль.
    // profile задан — материализация типовых умений роли: AI подбирает только ЦЕЛИ типов
    // профиля (по одному на запись), условие и режим подставляются из профиля сервером.
    // acceptedSeed — уже подготовленные к добавлению привязки (например, скиллы профиля):
    // валидация дубликатов должна видеть их рядом с текущими привязками персоны.
    public async Task<List<PersonaBinding>> SuggestBindingsAsync(string userId, Persona persona,
        string? userPrompt = null, IReadOnlyList<SpecialtyDefaultBinding>? profile = null,
        List<PersonaBinding>? acceptedSeed = null, CancellationToken ct = default)
    {
        // Полный каталог знаний (датасеты проектов/заметок + прочие доступные Dify-датасеты) —
        // валидация всё равно принимает любой из KnowledgeTargetsAsync, каталог промпта не должен быть уже
        var datasets = await bindings.KnowledgeTargetsAsync(userId);
        var prompt = profile is not null
            ? BuildProfilePrompt(userId, persona, datasets, profile)
            : BuildSuggestPrompt(userId, persona, datasets, userPrompt);
        var model = oneShot.NormalizeModel(config["Notes:AiModel"] ?? config["Tasks:AiModel"] ?? "haiku");

        List<SuggestRaw>? raws = null;
        for (var attempt = 1; attempt <= 2 && raws is null; attempt++)
        {
            var raw = await cheap.RunAsync(Services.Llm.LocalActionCatalog.PersonaBindingsSuggest,
                prompt, model, userId, jsonFormat: "json", ct: ct);
            raws = ParseSuggestArray(raw);
            if (raws is null)
                log.LogWarning("suggest bindings: ответ не распознан (попытка {Attempt}); сырой ответ: {Raw}",
                    attempt, raw.Length > 600 ? raw[..600] + "…" : raw);
        }
        if (raws is null) return [];

        var accepted = new List<PersonaBinding>(acceptedSeed ?? persona.Bindings ?? []);
        var result = new List<PersonaBinding>();

        if (profile is not null)
        {
            // Слоты профиля: на каждую запись — максимум одна привязка её типа; чужие
            // типы AI (сверх профиля) отбрасываются. Условие и режим — из профиля роли,
            // их формулирует админ, а не модель.
            var slots = profile.Select(e => (Entry: e, Used: false)).ToList();
            foreach (var r in raws.Take(profile.Count))
            {
                var (binding, _) = ParseBinding(new PersonaBindingRequest(
                    r.Type ?? "", r.Target ?? "", r.Path, "", "auto"));
                if (binding is null) continue;
                var index = -1;
                for (var i = 0; i < slots.Count; i++)
                    if (!slots[i].Used && slots[i].Entry.Type == binding.Type) { index = i; break; }
                if (index < 0) continue;
                slots[index] = (slots[index].Entry, true);
                binding.Condition = slots[index].Entry.Condition;
                binding.Mode = slots[index].Entry.Mode;
                var err = await bindings.ValidateAsync(userId, binding, accepted, persona);
                if (err is not null) continue;
                accepted.Add(binding);
                result.Add(binding);
            }
            return result;
        }

        foreach (var r in raws.Take(5))
        {
            var (binding, _) = ParseBinding(new PersonaBindingRequest(
                r.Type ?? "", r.Target ?? "", r.Path, r.Condition, r.Mode ?? "auto"));
            if (binding is null) continue;
            var err = await bindings.ValidateAsync(userId, binding, accepted, persona);
            if (err is not null) continue;
            accepted.Add(binding);
            result.Add(binding);
        }
        return result;
    }

    private string BuildSuggestPrompt(string userId, Persona persona,
        IReadOnlyList<(string Id, string Label, string? ProjectId)> datasets, string? userPrompt = null)
    {
        var hasUserPrompt = !string.IsNullOrWhiteSpace(userPrompt);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(hasUserPrompt
            ? "Составь AI-персоне привязку(и) — источники знаний, навыки и инструменты — по запросу пользователя. " +
              "Выбирай ТОЛЬКО из каталога ниже (target — точный id из каталога)."
            : "Подбери AI-персоне источники знаний и правила («привязки») под её роль. " +
              "Выбирай ТОЛЬКО из каталога ниже (target — точный id из каталога).");
        sb.AppendLine($"\nПерсона: {persona.Role ?? "без роли"} ({persona.Name}).");
        if (!string.IsNullOrWhiteSpace(persona.Description))
            sb.AppendLine($"Кто это: {persona.Description.Trim()}");
        // Характер: у персон с контрактом (P1) источник правды — Contract.Character,
        // SystemPrompt — legacy-фолбэк
        var personaCharacter = persona.Contract?.Character ?? persona.SystemPrompt;
        if (!string.IsNullOrWhiteSpace(personaCharacter))
        {
            var character = personaCharacter.Trim();
            if (character.Length > 800) character = character[..800] + "…";
            sb.AppendLine($"Характер: {character}");
        }

        if (hasUserPrompt)
            sb.AppendLine($"\nЗапрос пользователя (главный ориентир — построй привязку(и) под него): {userPrompt!.Trim()}");

        AppendBindingCatalog(sb, userId, datasets);

        sb.AppendLine("\nВерни ТОЛЬКО JSON-массив (без пояснений и markdown) из НЕ БОЛЕЕ 5 объектов:");
        sb.AppendLine("[{\"type\":\"project|projectPath|knowledge|notes|tool|skill\",\"target\":\"id из каталога\"," +
                      "\"path\":\"папка (опционально; для projectPath обязательна)\"," +
                      "\"condition\":\"когда применять, 1-2 предложения по-русски\",\"mode\":\"auto\"}]");
        sb.AppendLine(hasUserPrompt
            ? "Построй привязку(и) под запрос пользователя, опираясь на каталог; " +
              "если запрос не покрывается ни одной целью каталога — верни []."
            : "Бери только цели, реально полезные роли персоны; если подходящих нет — верни [].");
        return sb.ToString();
    }

    // Каталог целей владельца — общий блок промптов подбора привязок (роль/запрос/профиль роли)
    private void AppendBindingCatalog(System.Text.StringBuilder sb, string userId,
        IReadOnlyList<(string Id, string Label, string? ProjectId)> datasets)
    {
        sb.AppendLine("\nКаталог целей:");
        var ownProjects = projects.GetByOwner(userId);
        if (ownProjects.Count > 0)
        {
            sb.AppendLine("Проекты (type \"project\", target = id; конкретная папка проекта — " +
                          "type \"projectPath\", target = id + обязательный path):");
            foreach (var p in ownProjects.Take(20)) sb.AppendLine($"- {p.Id} — {p.Name}");
        }
        if (datasets.Count > 0)
        {
            sb.AppendLine("Базы знаний (type \"knowledge\", target = id):");
            foreach (var d in datasets.Take(20)) sb.AppendLine($"- {d.Id} — {d.Label}");
        }
        var sources = notes.GetSources(userId);
        if (sources.Count > 0)
        {
            sb.AppendLine("Источники заметок (type \"notes\", target = key):");
            foreach (var s in sources.Take(20)) sb.AppendLine($"- {s.Key} — {s.Label}");
        }
        var globalSkills = skills.GetGlobalSkills();
        if (globalSkills.Count > 0)
        {
            sb.AppendLine("Скиллы (type \"skill\", target = имя):");
            foreach (var s in globalSkills.Take(20))
            {
                var desc = s.Description.Length > 120 ? s.Description[..120] + "…" : s.Description;
                sb.AppendLine($"- {s.Name} — {desc}");
            }
        }
        sb.AppendLine("Инструменты (type \"tool\", target = ключ):");
        foreach (var kv in bindings.ToolCatalogFor(userId))
            sb.AppendLine($"- {kv.Key} — {kv.Value.Label}: {kv.Value.Hint}");
    }

    // Промпт материализации типовых умений роли: AI подбирает КОНКРЕТНУЮ цель каждого
    // типа из профиля; условие и режим модель не формулирует — их подставит сервер из
    // профиля. Сверх профиля брать нечего: список типов закрыт.
    private string BuildProfilePrompt(string userId, Persona persona,
        IReadOnlyList<(string Id, string Label, string? ProjectId)> datasets,
        IReadOnlyList<SpecialtyDefaultBinding> profile)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Материализуй типовые привязки AI-персоны: для каждого типа из профиля подбери " +
                      "ОДНУ конкретную цель из каталога ниже (target — точный id из каталога).");
        sb.AppendLine($"\nПерсона: {persona.Role ?? "без роли"} ({persona.Name}).");
        if (!string.IsNullOrWhiteSpace(persona.Description))
            sb.AppendLine($"Кто это: {persona.Description.Trim()}");
        if (persona.Scope == PersonaScope.Project && !string.IsNullOrEmpty(persona.ProjectId))
            sb.AppendLine($"Персона проектная (её проект: {persona.ProjectId}) — Project/ProjectPath к другим проектам не подбирай.");

        sb.AppendLine("\nПрофиль роли (по одной цели на строку):");
        foreach (var entry in profile)
        {
            var line = $"- {WireBindingType(entry.Type)}";
            if (!string.IsNullOrWhiteSpace(entry.Condition))
                line += $" — когда: {entry.Condition.Trim()}";
            sb.AppendLine(line);
        }

        AppendBindingCatalog(sb, userId, datasets);

        sb.AppendLine("\nВерни ТОЛЬКО JSON-массив (без пояснений и markdown):");
        sb.AppendLine("[{\"type\":\"project|projectPath|knowledge|notes|tool|skill|projectPersonas|projectTasks\"," +
                      "\"target\":\"id из каталога\",\"path\":\"папка (для projectPath обязательна)\"}]");
        sb.AppendLine("Если для какого-то типа в каталоге нет подходящей цели — пропусти его. " +
                      "Ничего сверх профиля не добавляй.");
        return sb.ToString();
    }

    // Wire-имя типа привязки (camelCase, как в конвертере персон) — для промптов подбора
    private static string WireBindingType(Models.PersonaBindingType type) =>
        JsonNamingPolicy.CamelCase.ConvertName(type.ToString());

    // Парс JSON-массива из ответа модели (устойчиво к преамбуле/markdown-fence)
    private static List<SuggestRaw>? ParseSuggestArray(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('[');
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == '[') depth++;
            else if (c == ']' && --depth == 0)
            {
                try
                {
                    return JsonSerializer.Deserialize<List<SuggestRaw>>(raw[start..(i + 1)],
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (JsonException) { return null; }
            }
        }
        return null;
    }

    private sealed record SuggestRaw(string? Type, string? Target, string? Path, string? Condition, string? Mode);

    // --- Автоматизации (тела POST/PUT/DELETE /automation) ---

    // Добавить правило (мгновенное сохранение)
    public async Task<ActionResult<PersonaAutomationRule>> AddAutomationRuleAsync(string userId,
        string id, AutomationRuleRequest req)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();
        // Валидация projectId в triggerArgs
        var error = ValidateTriggerProjectId(persona, req);
        if (error is not null) return BadRequest(new { error });
        var rule = ParseRule(req);
        // Идемпотентность: повторный вызов с теми же параметрами (ретрай MCP-инструмента,
        // автопродолжение цикла, повтор хода после сбоя) не плодит дубль — если правило с той
        // же сигнатурой уже есть, возвращаем существующее вместо создания второго.
        var dup = persona.AutomationRules?.FirstOrDefault(r => AutomationSignature(r) == AutomationSignature(rule));
        if (dup is not null) return Ok(dup);
        var list = new List<PersonaAutomationRule>(persona.AutomationRules ?? []) { rule };
        personas.UpdateRules(id, userId, list);
        await Broadcast(userId, "updated", id);
        return Ok(rule);
    }

    // Изменить одно правило (partial-merge: null-поля наследуются от текущего)
    public async Task<ActionResult<PersonaAutomationRule>> UpdateAutomationRuleAsync(string userId,
        string id, string ruleId, AutomationRuleRequest req)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();
        var current = persona.AutomationRules?.FirstOrDefault(r => r.Id == ruleId);
        if (current is null) return NotFound(new { error = "Правило не найдено" });

        // Валидация projectId — учитываем как новые, так и текущие параметры
        var error = ValidateTriggerProjectId(persona, req, current);
        if (error is not null) return BadRequest(new { error });

        var merged = ParseRule(req, current);
        var list = (persona.AutomationRules ?? []).Select(r => r.Id == ruleId ? merged : r).ToList();
        personas.UpdateRules(id, userId, list);
        await Broadcast(userId, "updated", id);
        return Ok(merged);
    }

    public async Task<IActionResult> DeleteAutomationRule(string userId, string id, string ruleId)
    {
        var persona = personas.Get(id, userId);
        if (persona is null) return NotFound();
        var list = persona.AutomationRules?.Where(r => r.Id != ruleId).ToList();
        if (list is null || list.Count == (persona.AutomationRules?.Count ?? 0))
            return NotFound(new { error = "Правило не найдено" });
        personas.UpdateRules(id, userId, list);
        await Broadcast(userId, "updated", id);
        return NoContent();
    }

    // Сигнатура правила для дедупа: тип и аргументы триггера, тяжесть и инструкция действия, имя.
    // Полное совпадение = дубль (защита от повторных POST при ретраях/сбоях хода).
    private static string AutomationSignature(PersonaAutomationRule r) =>
        string.Join("",
            r.Trigger.Type,
            JsonSerializer.Serialize(r.Trigger.Args),
            r.Action.Weight,
            r.Action.Instruction?.Trim() ?? "",
            r.Name.Trim());

    // Валидация projectId в triggerArgs: для File/GitCommit-триггера нужна
    // Project/ProjectPath-привязка, для остальных — любая.
    // Возвращает null при успехе или текст ошибки для 400.
    private string? ValidateTriggerProjectId(Persona persona, AutomationRuleRequest req,
        PersonaAutomationRule? current = null)
    {
        var triggerArgs = req.TriggerArgs ?? current?.Trigger.Args;
        if (triggerArgs is null) return null;
        var projectId = triggerArgs.TryGetValue("projectId", out var el) ? el.GetString() : null;
        if (string.IsNullOrWhiteSpace(projectId)) return null;

        var triggerType = req.TriggerType ?? current?.Trigger.Type;
        var isFileTrigger = triggerType is AutomationTriggerType.File or AutomationTriggerType.GitCommit;

        if (isFileTrigger)
        {
            if (!bindings.HasFileBindingToProject(persona, projectId))
                return $"Для триггера {triggerType} с projectId нужна привязка Project или ProjectPath к проекту «{projects.GetById(projectId)?.Name ?? projectId}»";
        }
        else
        {
            if (!bindings.HasAnyBindingToProject(persona, projectId))
                return $"Для правила с projectId нужна привязка Project, ProjectPath или ProjectTasks к проекту «{projects.GetById(projectId)?.Name ?? projectId}»";
        }
        return null;
    }

    // Маппинг DTO → модель правила. existing передаётся при обновлении — Id/CreatedAt и
    // null-поля (Args/Condition/Weight/…) наследуются от текущего правила.
    internal static PersonaAutomationRule ParseRule(AutomationRuleRequest req,
        PersonaAutomationRule? existing = null)
    {
        var cond = new AutomationCondition
        {
            OnlyIf = req.ConditionOnlyIf,
            QuietFrom = req.QuietFrom,
            QuietTo = req.QuietTo,
            MinIntervalMinutes = req.MinIntervalMinutes,
        };
        return new PersonaAutomationRule
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString(),
            CreatedAt = existing?.CreatedAt ?? DateTime.UtcNow,
            Enabled = req.Enabled ?? existing?.Enabled ?? true,
            Name = string.IsNullOrWhiteSpace(req.Name) ? (existing?.Name ?? "Правило") : req.Name.Trim(),
            Trigger = new AutomationTrigger
            {
                Type = req.TriggerType ?? existing?.Trigger.Type ?? AutomationTriggerType.Timer,
                Args = req.TriggerArgs ?? existing?.Trigger.Args,
            },
            Condition = cond.IsEmpty ? null : cond,
            Action = new AutomationAction
            {
                Weight = req.ActionWeight ?? existing?.Action.Weight ?? AutomationActionWeight.Gate,
                Instruction = req.ActionInstruction ?? existing?.Action.Instruction ?? "",
                RememberInHistory = req.RememberInHistory ?? existing?.Action.RememberInHistory ?? false,
                // -1 (не передано) — унаследовать текущее значение / дефолт 1440 при создании;
                // null — явный выбор «бессрочно»; N>0 — TTL в минутах
                ExpiresAfterMinutes = req.ActionExpiresAfterMinutes == -1
                    ? existing?.Action.ExpiresAfterMinutes ?? 1440
                    : req.ActionExpiresAfterMinutes,
            },
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // --- Общие хелперы (нужны и контроллеру, и http-тулсету) ---

    // Разбор DTO привязки: строковые type/mode → enum'ы, path нормализуется в валидации
    internal static (PersonaBinding? Binding, string? Error) ParseBinding(PersonaBindingRequest req)
    {
        if (!Enum.TryParse<Models.PersonaBindingType>(req.Type?.Trim(), true, out var type))
            return (null, $"Неизвестный тип привязки: {req.Type}");
        var mode = PersonaBindingMode.Auto;
        if (!string.IsNullOrWhiteSpace(req.Mode) && !Enum.TryParse(req.Mode.Trim(), true, out mode))
            return (null, $"Неизвестный режим привязки: {req.Mode}");
        return (new PersonaBinding
        {
            Type = type,
            Target = req.Target?.Trim() ?? "",
            Path = string.IsNullOrWhiteSpace(req.Path) ? null : req.Path.Trim(),
            Condition = req.Condition?.Trim() ?? "",
            Mode = mode,
        }, null);
    }

    internal static bool ValidColor(string? c) =>
        c is "yellow" or "orange" or "blue" or "green" or "purple" or "red" or "brown" or "cyan" or "pink";

    // Превращение заготовки в «своего» ассистента (план 2.8): Name/Role/Greeting или любой слот
    // характера поменян → статус нетронутой заготовки снимается. Посторонние поля (цвет, модель,
    // аватар, привязки) статус не трогают — карточка-приглашение не должна гаснуть от косметики.
    internal static bool IsCharacterRelevantChange(string beforeName, string? beforeRole,
        string? beforeGreeting, PersonaContract? beforeContract, Persona after) =>
        !string.Equals(beforeName, after.Name, StringComparison.Ordinal)
        || !string.Equals(beforeRole ?? "", after.Role ?? "", StringComparison.Ordinal)
        || !string.Equals(beforeGreeting ?? "", after.Greeting ?? "", StringComparison.Ordinal)
        || !SameCharacterContract(beforeContract, after.Contract);

    // Сравнение только слотов характера контракта (Character/Tone/MustDo/MustNot/OutputFormat/
    // SpeechExamples/Instructions); null-контракт трактуем как пустой. Списки — пословно.
    private static bool SameCharacterContract(PersonaContract? a, PersonaContract? b)
    {
        var ca = a ?? new PersonaContract();
        var cb = b ?? new PersonaContract();
        return Slot(ca.Character) == Slot(cb.Character)
            && Slot(ca.Tone) == Slot(cb.Tone)
            && Slot(ca.OutputFormat) == Slot(cb.OutputFormat)
            && Slot(ca.Instructions) == Slot(cb.Instructions)
            && ListSlot(ca.MustDo).SequenceEqual(ListSlot(cb.MustDo))
            && ListSlot(ca.MustNot).SequenceEqual(ListSlot(cb.MustNot))
            && ListSlot(ca.SpeechExamples).SequenceEqual(ListSlot(cb.SpeechExamples));

        static string Slot(string? s) => s ?? "";
        static IReadOnlyList<string> ListSlot(List<string>? xs) => xs is null ? Array.Empty<string>() : xs;
    }

    // Парс профиля доступа из запроса: null/пусто → «не менять» (out null),
    // валидная строка → значение, мусор → false (400 у вызывающего)
    internal static bool TryParseAccess(string? raw, out PersonaAccess? access)
    {
        access = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (!Enum.TryParse<PersonaAccess>(raw, ignoreCase: true, out var parsed)) return false;
        access = parsed;
        return true;
    }

    // Валидация ячейки матрицы уровней персоны (ADR-007 §2): пусто — годится, иначе id модели
    // или "preset:{id}". tier:* в ячейке запрещён (ячейка уже адресована уровнем). Наличие
    // пресета не проверяем — как у Model, ссылка становится битой только при удалении пресета.
    internal static bool IsValidTierCell(string? cell)
    {
        if (string.IsNullOrWhiteSpace(cell)) return true;
        var v = cell.Trim();
        if (Services.Llm.LocalActionOverridesStore.IsPresetRoute(v))
            return Services.Llm.LocalActionOverridesStore.ParsePresetRoute(v) is not null;
        // tier:* запрещён в ячейке; прочее трактуется как id модели
        return Services.Llm.LocalActionOverridesStore.ParseTierRoute(v) is null;
    }

    // Проект существует и принадлежит владельцу
    internal bool ValidProject(string userId, string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return false;
        var project = projects.GetById(projectId);
        return project is not null && project.OwnerId == userId;
    }
}
