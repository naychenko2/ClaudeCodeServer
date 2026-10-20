using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Projector;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

// Задачи внутри проекта
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/projects/{projectId}/tasks")]
public class ProjectTasksController(
    TaskManager tasks, ProjectManager projects, PersonaManager personas,
    ISessionBroadcaster broadcaster, PersonaBindingsService bindings,
    ISphereDirectory spheres) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    private Project? OwnProject(string projectId)
    {
        var project = projects.GetById(projectId);
        return project?.OwnerId == UserId ? project : null;
    }

    [HttpGet]
    public IActionResult GetAll(string projectId)
    {
        if (OwnProject(projectId) is null) return NotFound();
        // Список — облегчённый (без Description/ResultMarkdown); полная карточка — GET /api/tasks/{id}
        return Ok(tasks.GetByProject(projectId).Select(TaskListItem.From).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create(string projectId, [FromBody] CreateTaskRequest req)
    {
        var project = OwnProject(projectId);
        if (project is null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });

        // Колонка доски → статус выводим из её категории
        var cat = BoardColumnHelper.Category(project, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };
        // Полный объект колонки — для гейта DefectRules.EnsureNotClosedAtCreate:
        // дефект в Todo с columnId="done" не должен пройти мимо правила только потому,
        // что клиент не привёл Status в соответствие с категорией колонки.
        var targetColumn = project?.BoardColumns?.FirstOrDefault(c => c.Id == req.ColumnId);

        // Персона-исполнитель: своя и в правильном проекте (или есть ProjectTasks-привязка)
        if (!string.IsNullOrEmpty(req.PersonaId))
        {
            var p = personas.Get(req.PersonaId, UserId);
            var scopes = p is not null ? bindings.BuildExternalTaskScopes(UserId, p) : [];
            if (TaskPersonaValidator.Error(personas, UserId, req.PersonaId, projectId, scopes, spheres) is { } personaError)
                return BadRequest(new { error = personaError });
        }

        // Персона-постановщик (происхождение): та же валидация — своя и в правильном проекте
        if (!string.IsNullOrEmpty(req.CreatedByPersonaId))
        {
            var p = personas.Get(req.CreatedByPersonaId, UserId);
            var scopes = p is not null ? bindings.BuildExternalTaskScopes(UserId, p) : [];
            if (TaskPersonaValidator.Error(personas, UserId, req.CreatedByPersonaId, projectId, scopes, spheres) is { } creatorError)
                return BadRequest(new { error = creatorError });
        }

        TaskItem task;
        try
        {
            task = tasks.Create(projectId, UserId, req, targetColumn);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await broadcaster.ToOwner(UserId, new TaskChangedMessage("created", task));
        return Ok(task);
    }
}

// Задачи пользователя без привязки к конкретному проекту: календарь + операции по id
[ProjectCapability(ProjectCapabilityArea.Platform)]
[ApiController]
[Authorize]
[Route("api/tasks")]
public class TasksController(
    TaskManager tasks, ISessionBroadcaster broadcaster, TaskAiService ai, ProjectManager projects,
    PersonaManager personas, TaskExecutionService executor,
    PersonaBindingsService bindings, SessionManager sessions, ISphereDirectory spheres,
    // Подсистема Notes отключаемая: null — обратная запись чекбокса в заметку-источник
    // тихо пропускается (SyncTaskToNoteAsync ниже, флаг notes-task-sync и так no-op
    // для задач не из заметки).
    INoteTaskSync? noteSync = null,
    // Агрегат фичи (режим «Исполнение фичи»): null, когда подсистема Tasks выключена
    // гейтом Subsystems:tasks:Enabled — тогда GET /features отвечает честным 503, а не 500
    // (правило отключаемости, ADR-014; образец — AgentDownloadsController).
    FeatureProjector? features = null,
    // Git-вертикаль (опционально, как в SessionManager): гард мержа проверяет, что worktree
    // фичи числится в `git worktree list` проекта (тот же предикат, что AttachWorktreeAsync).
    // null — подсистема Git выключена → мерж fail-closed (иначе исполнитель стартует в корне).
    GitService? git = null) : ControllerBase
{
    private string UserId => User.FindFirstValue(JwtRegisteredClaimNames.Sub)!;

    // Проект генерации: только свой; чужой/несуществующий → личный контекст
    private string? OwnProjectId(string? projectId) =>
        projectId is not null && projects.GetById(projectId)?.OwnerId == UserId ? projectId : null;

    // Сгенерировать описание задачи (Claude): по названию + контекст проекта (личная — только название)
    [HttpPost("ai/description")]
    public async Task<IActionResult> GenerateDescription([FromBody] GenerateDescriptionRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        try
        {
            var description = await ai.GenerateDescriptionAsync(UserId, req.Title.Trim(), OwnProjectId(req.ProjectId), ct);
            return Ok(new { description });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Сгенерировать подзадачи (Claude) по названию и описанию
    [HttpPost("ai/subtasks")]
    public async Task<IActionResult> GenerateSubtasks([FromBody] GenerateSubtasksRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        try
        {
            var subtasks = await ai.GenerateSubtasksAsync(
                UserId, req.Title.Trim(), req.Description ?? "", OwnProjectId(req.ProjectId), ct);
            return Ok(new { subtasks });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Классификация (локальная модель): приоритет + метки по названию/описанию
    [HttpPost("ai/classify")]
    public async Task<IActionResult> Classify([FromBody] ClassifyTaskRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        var existing = tasks.GetByOwner(UserId).SelectMany(t => t.Labels)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            var r = await ai.ClassifyAsync(UserId, req.Title.Trim(), req.Description, existing, OwnProjectId(req.ProjectId), ct);
            return Ok(new { priority = r.Priority, labels = r.Labels });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Нормализация заголовка (чистка голосового ввода) → аккуратный title + подсказка срока
    [HttpPost("ai/normalize-title")]
    public async Task<IActionResult> NormalizeTitle([FromBody] NormalizeTitleRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужен текст заголовка" });
        try
        {
            var r = await ai.NormalizeTitleAsync(UserId, req.Title, ct);
            return Ok(new { title = r.Title, dueHint = r.DueHint });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Поиск дубля среди существующих задач владельца (предфильтр по ключевым словам + модель)
    [HttpPost("ai/find-duplicate")]
    public async Task<IActionResult> FindDuplicate([FromBody] FindDuplicateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Нужно название задачи" });
        var projectId = OwnProjectId(req.ProjectId);
        // Ключевые слова нового заголовка (≥4 букв) для дешёвого предотбора кандидатов
        var words = System.Text.RegularExpressions.Regex.Matches(req.Title.ToLowerInvariant(), @"\p{L}{4,}")
            .Select(m => m.Value).ToHashSet();
        var candidates = tasks.GetByOwner(UserId)
            .Where(t => t.ProjectId == projectId && !string.IsNullOrWhiteSpace(t.Title))
            .Where(t => words.Count == 0 || words.Any(w => t.Title.ToLowerInvariant().Contains(w)))
            .Take(20).Select(t => (t.Id, t.Title)).ToList();
        try
        {
            var r = await ai.FindDuplicateAsync(UserId, req.Title.Trim(), req.Description, candidates, ct);
            return Ok(new { duplicateId = r.Id, reason = r.Reason });
        }
        catch (InvalidOperationException ex) { return StatusCode(502, new { error = ex.Message }); }
    }

    // Личная задача — без привязки к проекту
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body)
    {
        // projectId в wire-формате CreateTaskRequest отсутствует, но клиент может прислать
        // его в теле — нужно для резолва кастомной колонки (гейт дефекта по категории).
        // Парсим руками, чтобы не расширять публичный record, который живёт в TaskManager.
        string? bodyProjectId = body.TryGetProperty("projectId", out var pid)
            && pid.ValueKind == JsonValueKind.String
            ? pid.GetString()
            : null;

        var req = JsonSerializer.Deserialize<CreateTaskRequest>(body.GetRawText(),
            TaskCreateJson.Options);
        if (req is null)
            return BadRequest(new { error = "Некорректное тело запроса" });

        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Название задачи не может быть пустым" });
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });

        // Проект из тела (если он свой) — для резолва кастомной колонки. Чужой проект
        // игнорируем: личный эндпоинт не должен подменять статус по чужой доске.
        var project = bodyProjectId is not null && projects.GetById(bodyProjectId)?.OwnerId == UserId
            ? projects.GetById(bodyProjectId)
            : null;

        // Колонка доски → статус из категории. У личных — только дефолтные колонки,
        // а также кастомные колонки проекта из тела (если projectId свой).
        var cat = BoardColumnHelper.Category(project, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };
        // Полный объект колонки — для гейта DefectRules.EnsureNotClosedAtCreate: дефект
        // в Todo с columnId="done" не должен пройти мимо правила только потому, что
        // клиент не привёл Status в соответствие с категорией колонки.
        var targetColumn = project?.BoardColumns?.FirstOrDefault(c => c.Id == req.ColumnId);

        // Персона-исполнитель: своя; проектная персона личную задачу не берёт
        if (!string.IsNullOrEmpty(req.PersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.PersonaId, taskProjectId: null, spheres: spheres) is { } personaError)
            return BadRequest(new { error = personaError });

        // Персона-постановщик (происхождение): та же валидация, что у исполнителя
        if (!string.IsNullOrEmpty(req.CreatedByPersonaId)
            && TaskPersonaValidator.Error(personas, UserId, req.CreatedByPersonaId, taskProjectId: null, spheres: spheres) is { } creatorError)
            return BadRequest(new { error = creatorError });

        TaskItem task;
        try
        {
            // Личная задача — проект не сохраняем в TaskItem, но колонку прокидываем
            // через TaskManager.Create для гейта DefectRules (review-гейт у личных
            // задач не действует: targetColumn приходит из BoardColumns ПРОЕКТА, а у
            // личной задачи projectId == null ⇒ project == null ⇒ targetColumn всегда null,
            // гейт EnsureReproOnReview no-op). Внутренние пути (NoteTaskSyncService/
            // TeamWaveService) могут слать personal-карточки в review-колонку своих
            // проектов через bodyProjectId — это не «личная» задача в смысле обхода,
            // обычный кейс гейта.
            task = tasks.Create(null, UserId, req, targetColumn);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await broadcaster.ToOwner(UserId, new TaskChangedMessage("created", task));
        return Ok(task);
    }

    // Все задачи пользователя (календарь, MCP): диапазон по сроку, поиск и фильтры.
    // personal=true — только личные (вне проекта); projectId — только задачи проекта;
    // personaId — только задачи, порученные конкретной персоне-исполнителю.
    [HttpGet]
    public IActionResult GetAll(
        [FromQuery] string? from = null, [FromQuery] string? to = null,
        [FromQuery] string? q = null, [FromQuery] string? status = null,
        [FromQuery] string? priority = null, [FromQuery] string? assignee = null,
        [FromQuery] string? projectId = null, [FromQuery] bool personal = false,
        [FromQuery] string? personaId = null)
    {
        var result = tasks.GetByOwner(UserId).AsEnumerable();
        // Строковое сравнение корректно для ISO-дат YYYY-MM-DD
        if (from is not null)
            result = result.Where(t => t.DueDate is not null && string.Compare(t.DueDate, from, StringComparison.Ordinal) >= 0);
        if (to is not null)
            result = result.Where(t => t.DueDate is not null && string.Compare(t.DueDate, to, StringComparison.Ordinal) <= 0);
        if (personal)
            result = result.Where(t => t.ProjectId is null);
        else if (!string.IsNullOrEmpty(projectId))
            result = result.Where(t => t.ProjectId == projectId);
        if (status is not null && Enum.TryParse<TaskItemStatus>(status, true, out var s))
            result = result.Where(t => t.Status == s);
        if (priority is not null && Enum.TryParse<TaskItemPriority>(priority, true, out var p))
            result = result.Where(t => t.Priority == p);
        if (assignee is not null && Enum.TryParse<TaskItemAssignee>(assignee, true, out var a))
            result = result.Where(t => t.Assignee == a);
        if (!string.IsNullOrEmpty(personaId))
            result = result.Where(t => t.PersonaId == personaId);
        if (!string.IsNullOrWhiteSpace(q))
            result = result.Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                t.Labels.Any(l => l.Contains(q, StringComparison.OrdinalIgnoreCase)));
        // Фильтры (в т.ч. q по полному описанию) отработали на полных задачах; наружу уходит
        // облегчённый элемент: без Description/ResultMarkdown (на проде это ~80% веса ответа).
        // Полная карточка — GET /api/tasks/{taskId}.
        return Ok(result.Select(TaskListItem.From).ToList());
    }

    [HttpGet("{taskId}")]
    public IActionResult GetById(string taskId)
    {
        var task = tasks.GetById(taskId);
        return task is null || task.OwnerId != UserId ? NotFound() : Ok(task);
    }

    [HttpPut("{taskId}")]
    public async Task<IActionResult> Update(string taskId, [FromBody] UpdateTaskRequest req)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != UserId) return NotFound();
        if (!ModelTiers.IsValidWireValue(req.ModelTier))
            return BadRequest(new { error = ModelTiers.WireError });

        // Целевой проект для валидации колонки/персоны: текущий, либо новый из req.ProjectId
        // (null в req = не менять; "" = сделать личной; guid = привязать к проекту)
        string? targetProjectId = task.ProjectId;
        if (req.ProjectId is not null)
        {
            targetProjectId = req.ProjectId == "" ? null : req.ProjectId;
            if (targetProjectId is not null && projects.GetById(targetProjectId)?.OwnerId != UserId)
                return BadRequest(new { error = "Проект не найден или недоступен" });
        }

        // Колонка доски → статус выводим из её категории (единый источник для MCP/Claude и доски).
        // Категорию берём по целевому проекту — колонка актуальна для него, а не для прежнего.
        var targetProject = targetProjectId is null ? null : projects.GetById(targetProjectId);
        var cat = BoardColumnHelper.Category(targetProject, req.ColumnId);
        if (cat is not null) req = req with { Status = cat };
        // Дефект: карточка попадает в review-колонку → нужны шаги воспроизведения (гейт —
        // TaskManager.EnsureReproOnReview). Колонка должна быть той, в которой карточка
        // ОКАЖЕТСЯ после Update: новая из req.ColumnId либо текущая (когда columnId не
        // менялся — а Review-колонка могла уже стоять). Без текущей колонки гейт не
        // срабатывает на repro:{} для дефекта, уже стоящего в ревью (находка 2 ревью Глеба).
        BoardColumn? effectiveColumn = null;
        if (!string.IsNullOrEmpty(req.ColumnId))
            effectiveColumn = targetProject?.BoardColumns?.FirstOrDefault(c => c.Id == req.ColumnId);
        else if (task.ColumnId is not null && targetProject?.BoardColumns is { } currentColumns)
            effectiveColumn = currentColumns.FirstOrDefault(c => c.Id == task.ColumnId);

        // Персона-исполнитель: "" = убрать (валидировать нечего), непустая — проверяем.
        // Валидация по целевому проекту: проектная персона прежнего проекта в новом недействительна,
        // если не имеет кросс-проектной ProjectTasks-привязки с полным доступом.
        if (!string.IsNullOrEmpty(req.PersonaId))
        {
            var p = personas.Get(req.PersonaId, UserId);
            var scopes = p is not null ? bindings.BuildExternalTaskScopes(UserId, p) : [];
            if (TaskPersonaValidator.Error(personas, UserId, req.PersonaId, targetProjectId, scopes, spheres) is { } personaError)
                return BadRequest(new { error = personaError });
        }

        // Вердикт проверки дефекта: автора и время подставляет сервер из сессии вызова
        // (X-Caller-Session-Id) — клиентские значения игнорируются, это гигиена атрибуции,
        // не защита. null-сессия/персона → проверка человеком (TaskVerification.PersonaId == null)
        if (req.Verification is not null)
        {
            var callerSessionId = Request.Headers[McpEndpoints.CallerSessionHeader].FirstOrDefault();
            var callerPersonaId = callerSessionId is not null ? sessions.GetById(callerSessionId)?.PersonaId : null;
            req = req with
            {
                Verification = new TaskVerification
                {
                    Notes = req.Verification.Notes,
                    VerifiedAt = DateTime.UtcNow,
                    PersonaId = callerPersonaId,
                    // Дефект a7b3b85d: verdict терялся при PUT (MCP tasks_complete его копировал,
                    // REST — нет) — через REST нельзя было закрыть review/qa/final фичи с вердиктом.
                    Verdict = req.Verification.Verdict,
                },
            };
        }

        var wasDone = task.Status == TaskItemStatus.Done;
        TaskItem updated;
        try
        {
            // Агентский путь ловим по X-Caller-Session-Id: заголовок ставят MCP-серверы
            // (mcp/tasks-server/index.js шлёт его на КАЖДЫЙ вызов), а браузер — никогда.
            // Без этого гард против затирания снятой задачи терялся на stdio-ветке отката
            // (Mcp:HttpTransport=false): tasks_complete идёт сюда обычным PUT, и затирание
            // снова было бы молчаливым (фикс-волна 4 team-blocker-honest). Это не защита от
            // подделки, а разведение путей: подделанный заголовок делает правило строже.
            var isAgentCall = !string.IsNullOrEmpty(
                Request.Headers[DenyOnDelegatedTurnAttribute.CallerHeader].FirstOrDefault());
            updated = tasks.Update(taskId, req, effectiveColumn, isAgentCall)
                ?? throw new InvalidOperationException("Задача не найдена");
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        await broadcaster.ToOwner(UserId, new TaskChangedMessage("updated", updated));

        // Завершение экземпляра регулярной задачи → следующий экземпляр серии.
        // Покрывает и UI, и MCP (tasks_complete/tasks_update идут через этот PUT)
        if (!wasDone && updated.Status == TaskItemStatus.Done && updated.Recurrence is not null)
        {
            var next = tasks.SpawnNextOccurrence(updated);
            if (next is not null)
                await broadcaster.ToOwner(UserId, new TaskChangedMessage("created", next));
        }

        // Обратная запись в заметку-источник: смена done-состояния ставит/снимает галочку
        // (флаг notes-task-sync; no-op если задача не из заметки)
        if (wasDone != (updated.Status == TaskItemStatus.Done))
            await (noteSync?.SyncTaskToNoteAsync(UserId, updated) ?? Task.CompletedTask);

        // Мироринг MCP (TasksToolset.CompleteResponse): если ревью/проверку/финал фичи
        // закрыли без вердикта, ответ несёт мягкое предупреждение (отдельное поле warning,
        // не notes) — карточка фичи вердикт не покажет, а блокировать закрытие ради UI нельзя.
        return WithWarning(updated);
    }

    // Карточка + мягкое warning (FeatureVerdict.Warning), если условие сработало. Единый путь
    // сериализации: TaskItem → JsonObject теми же опциями, что MVC (camelCase + string-enum
    // camelCase, relaxed-энкодер против двойного \u-экранирования) — один source of truth,
    // warning (если есть) дописывается на верхнем уровне. Та же форма, что у MCP tasks_complete.
    private ObjectResult WithWarning(TaskItem task)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(task, FeatureJsonOptions))!.AsObject();
        if (FeatureVerdict.Warning(task) is { } warning)
            json["warning"] = warning;
        return Ok(json);
    }

    // Опции сериализации TaskItem, совпадающие с MVC (AddJsonOptions: camelCase +
    // JsonStringEnumConverter(CamelCase)). Relaxed-энкодер: кириллица не уходит в \uXXXX
    // при пред-сериализации, иначе MVC переэкранировал бы её вторично.
    private static readonly JsonSerializerOptions FeatureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // Агрегат фичи (режим «Исполнение фичи», docs/research/turn-plan-feature-mode-cut-2026-10.md):
    // GET /api/tasks/features?sessionId=… — фичи чата (эпик + конвейер) с агрегатом каждой.
    // Без sessionId — все фичи владельца. Выборка и подсчёт — только через FeatureProjector
    // (без второго источника правды); [Authorize] + OwnerId из claim. Подсистема Tasks
    // выключена (features == null) → честный 503, а не 500 (ADR-014).
    [HttpGet("features")]
    public IActionResult GetFeatures([FromQuery] string? sessionId = null)
    {
        if (features is null)
            return StatusCode(503, new { error = "Подсистема задач выключена на этом сервере" });
        // Связь фичи с чатом: эпик через SourceSessionId/ExecutorLaunchedBySessionId (разрез §2:
        // «фича переезжает за оркестратором»). sessionId — id сессии (GUID-строка).
        var runs = sessionId is null
            ? features.ProjectAll(UserId)
            : features.ProjectForSession(sessionId, UserId);
        return Ok(runs);
    }

    // Индекс «чат → фичи» (контракт С0, GET /api/tasks/features/index?projectId=…): [Authorize],
    // владелец из claim, только свои данные (GetByOwner). Сессии владельца — узкий снимок
    // (FeatureIndexSession) из SessionManager: чужие/неизвестные id чатов в снимок не попадают
    // (fail-closed). projectId — только сессии/фичи этого проекта (эпик в проекте ИЛИ собственный
    // чат в проекте); без projectId — все проекты владельца. Подсистема Tasks выключена
    // (features == null) → честный 503, а не 500 (ADR-014, как у GetFeatures).
    [HttpGet("features/index")]
    public IActionResult GetFeaturesIndex([FromQuery] string? projectId = null)
    {
        if (features is null)
            return StatusCode(503, new { error = "Подсистема задач выключена на этом сервере" });
        // Сессии владельца → узкий снимок (id, taskId, projectId, archived). GetAllOwnedBy
        // отдаёт только живые сессии владельца: чужие/удалённые id чатов в снимок не попадают.
        return Ok(features.BuildIndex(UserId, SessionSnapshot(), projectId));
    }

    // Узкий снимок сессий владельца (id, taskId, projectId, archived) — единый источник для
    // индекса (GetFeaturesIndex) и ленты одной фичи (GetFeature): fail-closed, чужие/удалённые
    // id чатов в снимок не попадают (GetAllOwnedBy — только живые сессии владельца).
    private List<FeatureIndexSession> SessionSnapshot() =>
        sessions.GetAllOwnedBy(UserId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived))
            .ToList();

    // Одна фича по id эпика (волна 2 «Обзор фич», GET /api/tasks/features/{featureId}):
    // обёртка { run, events, eventsTotal } — агрегат (Project, без новой арифметики) + лента
    // событий (BuildEvents, порядок как отдаёт функция) + общее число событий до обрезки.
    // [Authorize], владелец из claim (не из маршрута); нет эпика/чужой/удалён → один 404
    // (не отличаем: это же состояние «Фичу удалили» на фронте). Подсистема Tasks выключена
    // (features == null) → честный 503 (ADR-014, как у GetFeatures/GetFeaturesIndex). Ручка
    // от фич-флагов не зависит. ?eventsLimit=: не задан → 100; "all"/"0" → все; N (целое > 0)
    // → N; прочее → 100.
    // п.7: белый список символов id фичи (как resumeSessionId): GUID/строка id без спецсимволов.
    // Нестандартный id (слеш, «..», >128) → 404 «Фича не найдена» (тело как у «нет эпика»).
    private static readonly System.Text.RegularExpressions.Regex FeatureIdPattern =
        new(@"^[A-Za-z0-9_-]{1,128}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    [HttpGet("features/{featureId}")]
    public IActionResult GetFeature(string featureId, [FromQuery] string? eventsLimit = null)
    {
        if (features is null)
            return StatusCode(503, new { error = "Подсистема задач выключена на этом сервере" });
        // п.7: нестандартный id (символы вне белого списка) → тот же 404, что «нет эпика»:
        // lookup по кривому id (с «/» или «..») не идёт, фронт 404 не различает.
        if (!FeatureIdPattern.IsMatch(featureId))
            return NotFound(new { error = "Фича не найдена" });
        // Project: null — эпика нет (нет/чужой/удалён — один 404, см. выше).
        // Тело совпадает с 404 POST merge: { error: "Фича не найдена" } (фронт читает error).
        var run = features.Project(featureId, UserId);
        if (run is null) return NotFound(new { error = "Фича не найдена" });
        // Снимок сессий владельца — как для индекса (SessionSnapshot): sessionId события наружу
        // только если в нём (fail-closed: удалённый/чужой чат → null).
        var all = FeatureProjector.BuildEvents(run, SessionSnapshot());
        var eventsTotal = all.Count;
        var events = all.Take(ResolveEventsLimit(eventsLimit)).ToList();
        return Ok(new { run, events, eventsTotal });
    }

    // ?eventsLimit= : не задан (null) → 100; строка «0» или «all» → все (int.MaxValue) — «0»
    // читается как «без лимита», а не как числовой ноль (отдельного числового 0 на wire нет:
    // параметр всегда строка); N (целое > 0) → N; прочее → 100.
    private static int ResolveEventsLimit(string? eventsLimit)
    {
        if (eventsLimit is null) return 100;
        if (eventsLimit == "all" || eventsLimit == "0") return int.MaxValue;
        if (int.TryParse(eventsLimit, out var n) && n > 0) return n;
        return 100;
    }

    // Мерж фичи (волна 2 «Обзор фич», POST /api/tasks/features/{featureId}/merge):
    // создаёт задачу role=merge с персоной-исполнителем DevopsExecutor и запускает её.
    // Fail-closed: 503 (подсистема выключена), 404 (нет/чужой эпик, тело { error: "Фича не найдена" }),
    // 409 (финал не принят, уже смержено/закрыто, worktree не на диске, 0 или >1 исполнителей DevOps).
    // Идемпотентна и атомарна: открытая merge-задача → вернуть её (создание сериализовано по featureId),
    // если todo — запустить, вторую не заводить.
    [HttpPost("features/{featureId}/merge")]
    [DenyOnDelegatedTurn("Запуск мержа фичи", AllowInTeamImplement = true,
        AllowedDepth = McpEndpoints.RunExecutorAllowedDepth)]
    public async Task<IActionResult> MergeFeature(string featureId)
    {
        if (features is null)
            return StatusCode(503, new { error = "Подсистема задач выключена на этом сервере" });

        // m2: нестандартный id (символы вне белого списка) → тот же 404, что у GET features/{id}
        // (одинаковый код и тело { error: "Фича не найдена" } — фронт читает error).
        if (!FeatureIdPattern.IsMatch(featureId))
            return NotFound(new { error = "Фича не найдена" });

        var run = features.Project(featureId, UserId);
        // 404 с телом: фича чужая/удалена — то же тело, что у GET features/{id} (фронт читает error).
        if (run is null) return NotFound(new { error = "Фича не найдена" });

        // «Уже смержена» (merge-задача Done) и «закрыта» (эпик Done) — ПЕРЕД «финал не принят»:
        // у закрытой фичи без принятого финала причина — «закрыта», а не «финал не принят» (п.5).
        if (run.Merge is { Status: TaskItemStatus.Done })
            return Conflict(new { error = "Фича уже смержена" });
        if (run.Closed)
            return Conflict(new { error = "Фича закрыта" });

        // Финал не принят → мерж преждевременно.
        if (!run.Ready)
            return Conflict(new { error = "Финал не принят: мерж преждевременно" });

        // Worktree фичи: непустые путь и ветка, путь на диске и числится в `git worktree list`
        // проекта (тот же предикат, что AttachWorktreeAsync) — иначе исполнитель тихо стартовал бы
        // в корне проекта. Текст 409 — без абсолютного пути хоста (п.4).
        var (gitDisabled, worktreeError) = await FeatureWorktreeErrorAsync(run.Epic);
        // п.57: подсистема Git выключена → честный 503 (как у Tasks), а не 409 «не найден».
        if (gitDisabled)
            return StatusCode(503, new { error = "Подсистема Git выключена на этом сервере" });
        if (worktreeError is not null)
            return Conflict(new { error = worktreeError });

        // Исполнитель: ровно одна персона с Specialty=DevopsExecutor, допустимая в проекте эпика.
        // Те же externalScopes, что в Create: кросс-проектная ProjectTasks-привязка не даёт ложного 409.
        var devops = personas.GetByOwner(UserId)
            .Where(p => p.Specialty == PersonaSpecialty.DevopsExecutor)
            .Where(p =>
            {
                var scopes = bindings.BuildExternalTaskScopes(UserId, p);
                return TaskPersonaValidator.Error(personas, UserId, p.Id,
                    taskProjectId: run.Epic.ProjectId, externalScopes: scopes, spheres: spheres) is null;
            })
            .ToList();

        if (devops.Count == 0)
            return Conflict(new { error = "Нет исполнителя DevOps: задайте персона с специальностью DevopsExecutor" });
        if (devops.Count > 1)
            return Conflict(new { error = "Несколько исполнителей DevOps: уточните, кто ведёт мерж" });

        var persona = devops[0];

        // m1: основная ветка проекта (текущая ветка корня) — для шаблона постановки; null — git не дал.
        var mainBranch = await ResolveMainBranchAsync(run.Epic);

        // Создание идемпотентно и атомарно: два конкурентных POST сериализуем по featureId,
        // иначе оба увидят «merge-задачи нет» и заведут две (два исполнителя на одном worktree).
        var gate = MergeGates.GetOrAdd(featureId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            // Идемпотентность: merge-задача уже есть (свежий снимок — после gate).
            var existing = features.Project(featureId, UserId)?.Merge;
            if (existing is not null)
            {
                // п.2/п.73: merge-задача закрыта (Done) в окне между снимком и гейтом → 409: дубль
                // не проходит (верхний гард смотрел на устаревший снимок). Текст отличен от верхнего
                // гарда («Фича уже смержена»), чтобы тест гонки различал, какая ветка сработала.
                if (existing.Status == TaskItemStatus.Done)
                    return Conflict(new { error = "Фича смержена во время мержа" });
                // Открыта (todo/inProgress): если todo — запускаем исполнителя; возвращаем
                // актуальную задачу (п.6: та же форма, что и ветка создания — с ClaudeStartedAt,
                // если запуск прошёл).
                if (existing.Status == TaskItemStatus.Todo)
                {
                    try
                    {
                        existing = await executor.ExecuteAsync(existing, auto: false, null);
                    }
                    catch (InvalidOperationException ex)
                    {
                        return BadRequest(new { error = ex.Message });
                    }
                }
                return Ok(existing);
            }

            // Создаём задачу merge: FeatureId, ProjectId, WorktreePath/WorktreeBranch — из эпика.
            // Description — самодостаточный шаблон постановки (исход/цель, push, конфликт, критерий, уборка).
            // ProjectId! — здесь не null: гард worktree (FeatureWorktreeErrorAsync) выше уже вернул 409,
            // если у эпика нет проекта.
            var req = new CreateTaskRequest(
                Title: $"Мерж фичи: {run.Epic.Title}",
                Description: BuildMergeDescription(run, projects.GetById(run.Epic.ProjectId!)?.RootPath, mainBranch),
                FeatureId: featureId,
                Role: TaskRoles.Merge,
                PersonaId: persona.Id,
                Assignee: TaskItemAssignee.Claude,
                WorktreePath: run.Epic.WorktreePath,
                WorktreeBranch: run.Epic.WorktreeBranch);

            TaskItem task;
            try
            {
                task = tasks.Create(run.Epic.ProjectId, UserId, req);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            await broadcaster.ToOwner(UserId, new TaskChangedMessage("created", task));

            // Возвращаем задачу ПОСЛЕ запуска: ClaudeStartedAt/статус подтверждают, что исполнитель стартовал.
            TaskItem started;
            try
            {
                started = await executor.ExecuteAsync(task, auto: false, null);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            return Ok(started);
        }
        finally
        {
            gate.Release();
            // Не снимаем запись из MergeGates (осознанно, отменяет минор п.42 «MergeGates растёт»):
            // один SemaphoreSlim на фичу живёт до рестарта процесса, цена — десятки объектов.
            // Снятие записи из словаря давало параллельные критические секции (два запроса на
            // разных экземплярах гейта) и дубль merge-задачи; идемпотентность держит свежий снимок
            // features.Project ПОСЛЕ gate, гейт лишь сериализует создание, а не гарантирует «один в секции».
        }
    }

    // Причина, по которой worktree фичи непригоден для мержа (null — годен). Тот же предикат,
    // что AttachWorktreeAsync (SessionManager): путь непустой, на диске и числится в
    // `git worktree list` проекта — иначе чат-исполнитель не привяжется к дереву и тихо
    // стартовал бы в корне проекта. Текст 409 — без абсолютного пути хоста (п.4).
    // Возврат (gitDisabled, reason): gitDisabled=true → честный 503 (подсистема Git выключена,
    // п.57, как у Tasks), иначе reason — текст 409 (null — worktree годен).
    private async Task<(bool GitDisabled, string? Reason)> FeatureWorktreeErrorAsync(TaskItem epic)
    {
        if (string.IsNullOrWhiteSpace(epic.WorktreePath)) return (false, "Worktree фичи не задан");
        if (string.IsNullOrWhiteSpace(epic.WorktreeBranch)) return (false, "Ветка фичи не задана");
        // п.56: у эпика нет проекта (личная задача) → 409, а не ArgumentNullException из GetById(null).
        if (epic.ProjectId is null) return (false, "Worktree фичи не найден");
        if (projects.GetById(epic.ProjectId) is not { } project) return (false, "Worktree фичи не найден");
        // п.56: некорректный путь (недопустимые символы) → 409, а не 500 из Path.GetFullPath.
        string path;
        try
        {
            path = Path.GetFullPath(epic.WorktreePath.Trim());
        }
        catch (Exception)
        {
            return (false, "Worktree фичи не найден");
        }
        if (!Directory.Exists(path)) return (false, "Worktree фичи не найден");
        // п.54: гард принимает корень проекта как worktree (WorktreePath = RootPath) — запрещаем:
        // иначе «мерж в основную ветку» происходил бы в том же дереве, что и исход.
        if (PathNormalizer.NormalizePath(path) == PathNormalizer.NormalizePath(project.RootPath))
            return (false, "Worktree фичи совпадает с корнем проекта");
        // п.57: подсистема Git выключена → честный 503 (как у Tasks), а не 409 «не найден».
        if (git is null) return (true, null);
        var wanted = PathNormalizer.NormalizePath(path);
        IReadOnlyList<GitWorktreeInfo> list;
        try
        {
            // п.53: сбой git (worktree list) → 409 с честной причиной, не 500 (как AttachWorktreeAsync).
            list = await git.WorktreeListAsync(UserId, project.RootPath);
        }
        catch (Exception)
        {
            return (false, "Не удалось прочитать git worktree list проекта");
        }
        if (!list.Any(w => PathNormalizer.NormalizePath(w.Path) == wanted))
            return (false, "Worktree фичи не найден");
        return (false, null);
    }

    // m1: основная ветка проекта — текущая ветка корня (git symbolic-ref). null — git выключен,
    // корень не репо, detached HEAD или сбой git: шаблон тогда пишет «обычно master» без жёсткого имени.
    private async Task<string?> ResolveMainBranchAsync(TaskItem epic)
    {
        if (git is null || epic.ProjectId is null) return null;
        var project = projects.GetById(epic.ProjectId);
        if (project is null) return null;
        try
        {
            return await git.CurrentBranchAsync(UserId, project.RootPath);
        }
        catch
        {
            return null;
        }
    }

    // Шаблон постановки merge-задачи (M1): самодостаточный — исход/цель, push, конфликт, критерий, уборка.
    // Названия ветки и пути worktree подставляются из эпика; основное рабочее дерево (корень проекта,
    // куда мержим) — абсолютный путь (п.40; прецедент TeamImplementPrompts: путь дерева в постановке).
    private static string BuildMergeDescription(FeatureRun run, string? projectRoot, string? mainBranch)
    {
        var branch = string.IsNullOrWhiteSpace(run.Epic.WorktreeBranch) ? "ветку фичи" : run.Epic.WorktreeBranch;
        var worktree = string.IsNullOrWhiteSpace(run.Epic.WorktreePath) ? "worktree фичи" : run.Epic.WorktreePath;
        var mainTree = string.IsNullOrWhiteSpace(projectRoot) ? "корень проекта" : projectRoot;
        // m1: основная ветка — из git (текущая ветка корня проекта); если неизвестна — честно
        // «основная ветка проекта (обычно master)», без жёсткого master в команде.
        var target = string.IsNullOrWhiteSpace(mainBranch)
            ? "основную ветку проекта (в этом проекте обычно master)"
            : $"основную ветку проекта «{mainBranch}»";
        return string.Join("\n",
            $"Смержить ветку фичи «{branch}» в {target} в основном рабочем дереве проекта; исход — worktree фичи {worktree}.",
            $"Основное рабочее дерево (сюда мержим): {mainTree}.",
            "Предусловие: финал фичи принят (проверено сервером).",
            "Push НЕ делать — только локальный мерж.",
            "При конфликте не разрешать молча: остановиться и доложить в отчёте задачи, что именно конфликтует.",
            "После успешного мержа проверить git log/сборку по кратким критериям проекта и закрыть эпик через tasks_complete только если мерж прошёл чисто.",
            "Worktree и ветки не удалять — уборка по слову человека.");
    }

    // Семафоры создания merge-задач по featureId: статика, т.к. контроллер transient (по одному на запрос).
    // Запись НЕ убирается в finally (осознанно, отменяет минор п.42): один SemaphoreSlim на фичу живёт
    // до рестарта процесса (цена — десятки объектов). Снятие записи давало параллельные критические секции
    // и дубль merge-задачи; идемпотентность (свежий снимок после gate) держит корректность, гейт — сериализация.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> MergeGates = new();

    // Запустить выполнение задачи Claude-ом (кнопка «Выполнить с Claude»)
    [HttpPost("{taskId}/execute")]
    // Анти-рекурсия: раньше жила в составе инструментов (env TASKS_EXECUTE), из-за чего
    // чередование обычного и делегированного хода перезапускало процесс CLI со всеми MCP.
    // AllowInTeamImplement: у чата-штаба «Командной реализации» запрет заменён квотой —
    // автономный цикл волн иначе невозможен, а лавину держит бюджет итерации (Э4).
    // Ход-реакция на доклад исполнителя запускать может: запрет снят решением владельца
    // (мешал и пользы не давал), цикл «доклад → запуск → доклад» останавливает «Стоп».
    [DenyOnDelegatedTurn("Запуск задачи на исполнение", AllowInTeamImplement = true,
        AllowedDepth = McpEndpoints.RunExecutorAllowedDepth)]
    public async Task<IActionResult> Execute(string taskId)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != UserId) return NotFound();

        try
        {
            // Кнопка в UI: сессии-вызывателя нет — после успешного запуска поля сбрасываются; на идущей
            // задаче запуск отказывает раньше записи, поле запустившего не затирается
            var callerSessionId = Request.Headers[McpEndpoints.CallerSessionHeader].FirstOrDefault();
            var callerSession = callerSessionId is not null && sessions.GetById(callerSessionId) is { } cs
                && sessions.ResolveOwnerId(cs) == UserId ? cs : null;
            return Ok(await executor.ExecuteAsync(task, auto: false,
                new ExecutorLauncher(callerSession?.Id, callerSession?.PersonaId)));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{taskId}")]
    public async Task<IActionResult> Delete(string taskId)
    {
        var task = tasks.GetById(taskId);
        if (task is null || task.OwnerId != UserId) return NotFound();

        tasks.Delete(taskId);
        await broadcaster.ToOwner(UserId, new TaskChangedMessage("deleted", task));
        return NoContent();
    }
}

public record GenerateDescriptionRequest(string Title, string? ProjectId = null);
public record GenerateSubtasksRequest(string Title, string? Description = null, string? ProjectId = null);
public record ClassifyTaskRequest(string Title, string? Description = null, string? ProjectId = null);
public record NormalizeTitleRequest(string Title);
public record FindDuplicateRequest(string Title, string? Description = null, string? ProjectId = null);

// Валидация персоны-исполнителя/постановщика задачи: персона существует и принадлежит
// владельцу; проектная персона допустима только у задач своего проекта, если не имеет
// кросс-проектной ProjectTasks-привязки с полным доступом (externalScopes). null — ошибок нет.
public static class TaskPersonaValidator
{
    public static string? Error(PersonaManager personas, string userId, string personaId,
        string? taskProjectId,
        IReadOnlyList<(string ProjectId, bool ReadOnly)>? externalScopes = null,
        ISphereDirectory? spheres = null)
    {
        var persona = personas.Get(personaId, userId);
        if (persona is null) return "Персона не найдена или недоступна";
        // Персона сферы — исполнитель в любом проекте своей сферы: синтетические полные скоупы
        // зоны приходят в externalScopes (PersonaBindingsService.BuildExternalTaskScopes)
        // Зона — единая точка правды PersonaZone.VisibleIn (как у исполнителя): внешняя
        // ProjectTasks-привязка не расширяет зону персоны сферы. Без справочника (null) —
        // отказ: fail-closed, зону проверить нечем
        if (PersonaZone.IsSpherePersona(persona))
            return taskProjectId is not null
                && spheres is not null && PersonaZone.VisibleIn(persona, taskProjectId, spheres)
                && externalScopes?.Any(s => s.ProjectId == taskProjectId && !s.ReadOnly) == true
                ? null
                : "Персона сферы может выполнять только задачи проектов своей сферы";
        if (PersonaZone.IsProjectPersona(persona) && persona.ProjectId != taskProjectId)
        {
            // Кросс-проектная ProjectTasks-привязка с полным доступом разрешает
            if (externalScopes is not null)
            {
                var scope = externalScopes.FirstOrDefault(s => s.ProjectId == taskProjectId);
                if (scope != default && !scope.ReadOnly)
                    return null;
            }
            return "Проектная персона может выполнять только задачи своего проекта";
        }
        return null;
    }
}

// Резолв категории статуса по id колонки доски
public static class BoardColumnHelper
{
    // Кастомная колонка проекта → её Category; дефолтная (id == имя категории) →
    // распарсенный статус (todo/inProgress/done); иначе null (не менять статус).
    public static TaskItemStatus? Category(Project? project, string? columnId)
    {
        if (string.IsNullOrEmpty(columnId)) return null;
        var custom = project?.BoardColumns?.FirstOrDefault(c => c.Id == columnId);
        if (custom is not null) return custom.Category;
        return Enum.TryParse<TaskItemStatus>(columnId, ignoreCase: true, out var cat) ? cat : null;
    }

    // Признак «карточка попадает в колонку ревью» (BoardColumn.Role == "review") для
    // DefectRules.EnsureReproOnReview. Только кастомные колонки проекта могут иметь Role —
    // дефолтные (todo/inProgress/done) им не бывают.
    public static bool IsReview(Project? project, string? columnId)
    {
        if (string.IsNullOrEmpty(columnId)) return false;
        var custom = project?.BoardColumns?.FirstOrDefault(c => c.Id == columnId);
        return custom?.Role == "review";
    }
}

// Опции десериализации CreateTaskRequest для ручного парсинга тела в TasksController.Create
// (полю projectId нет в record, но клиент шлёт его в JSON — нужно для резолва колонки).
// Те же настройки, что и в Program.cs для глобального MVC: enum-ы в CamelCase,
// регистр имён свойств не важен.
public static class TaskCreateJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
