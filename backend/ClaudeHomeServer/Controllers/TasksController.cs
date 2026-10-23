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
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Spheres;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
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
    GitService? git = null,
    // Б5b (минор 4): боевой резолвер модели исполнителя (ModelAssignmentResolver.ExecutorModel) —
    // та же модель, что реально едет в чат (матрицы персоны, дефолт места «исполнитель» = Strong,
    // явная модель персоны). null — подсистема Llm выключена → «Авто» без разворота (не 500).
    ModelAssignmentResolver? assignments = null,
    // Б5b: журнал событий фичи (волна 3): точный режим stats.timeline (work/waiting/error/idle)
    // и модели breakdown из журнала. null — подсистема Tasks выключена (как features) →
    // stats.source=approx, не 500.
    FeatureJournalStore? journal = null,
    // Б-mk-b: каталог макетов фичи (Docs): null — подсистема Docs выключена → mockups: []
    // (без флага, не 500). Склейка в GetFeature, не в FeatureProjector (ADR-031 «Макеты фичи»).
    MockupCatalog? catalog = null,
    // Б9b: шов расхода фичи (Core, ISpendFeatureReader): null — подсистема Spend выключена
    // (гейт Subsystems:spend:Enabled) → stats.spend = null, не 500 (решение Андрея 20.10).
    // Вертикаль Tasks Spend не видит напрямую — только через этот шов (SubsystemBoundaryTests).
    ISpendFeatureReader? spend = null) : ControllerBase
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
        [FromQuery] string? personaId = null, [FromQuery] int? doneDays = null)
    {
        var result = tasks.GetByOwner(UserId).AsEnumerable();
        // doneDays задан — закрытые только за N дней по CompletedAt (границу включаем); не задан — все
        if (doneDays is { } dd)
        {
            var border = DateTime.UtcNow.AddDays(-Math.Max(dd, 0));
            result = result.Where(t => t.Status != TaskItemStatus.Done || (dd > 0 && t.CompletedAt is { } c && c >= border));
        }
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
                    ReturnReason = req.Verification.ReturnReason, // санирует TaskManager
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
            // Исполнитель по claim typ=svc (fail-closed, решение Александра 22.10): сервисный токен
            // без валидной сессии-вызывающей (заголовка нет, чужая или несуществующая сессия) — исполнитель;
            // с валидной — исполнитель, только если сессия привязана к ЭТОЙ задаче (Session.TaskId).
            // Пользовательский JWT из логина — человек, критерии менять может. Заголовок в расчёте
            // участвует лишь как указание сессии: его отсутствие правило не смягчает.
            var callerSessionHeader = Request.Headers[McpEndpoints.CallerSessionHeader].FirstOrDefault();
            var callerSession = callerSessionHeader is not null
                && sessions.GetById(callerSessionHeader) is { } cs
                && sessions.ResolveOwnerId(cs) == UserId ? cs : null;
            // Дверь MCP (TasksToolset) строже REST: исполнитель там определяется только по
            // session.TaskId == id, без claim typ=svc (MCP всегда под сервисным токеном). Правила двух
            // дверей обязаны оставаться согласованными: ослабление одной открывает обход через другую.
            var callerIsExecutor =
                User.FindFirstValue(JwtService.TokenKindClaim) == JwtService.ServiceTokenKind
                && (callerSession is null || callerSession.TaskId == taskId);
            updated = tasks.Update(taskId, req, effectiveColumn, isAgentCall, callerIsExecutor)
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
        // Б7: снимок сессий владельца в оба ветвления — исполнители (chatId/chatArchived)
        // одинаковы для features?sessionId= и features/{id} (ProjectFull); fail-closed.
        // Б4: одна и та же функция модели чата (ChatModelResolver) в оба ветвления —
        // modelLabel/modelFull исполнителей одинаковы, что в списке, что на странице.
        var snapshot = SessionSnapshot();
        var resolver = ChatModelResolver();
        var runs = sessionId is null
            ? features.ProjectAll(UserId, snapshot, resolver)
            : features.ProjectForSession(sessionId, UserId, snapshot, resolver);
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

    // Узкий снимок сессий владельца (id, taskId, projectId, archived, model) — единый источник
    // для индекса (GetFeaturesIndex) и ленты одной фичи (GetFeature): fail-closed, чужие/удалённые
    // id чатов в снимок не попадают (GetAllOwnedBy — только живые сессии владельца). Model —
    // Session.Model (null/пусто — «Авто», слот; Б4).
    private List<FeatureIndexSession> SessionSnapshot() =>
        sessions.GetAllOwnedBy(UserId)
            .Select(s => new FeatureIndexSession(s.Id, s.TaskId, s.ProjectId, s.IsArchived, s.Model))
            .ToList();

    // Б4/Б5b: готовая функция модели чата (label/full) для проекции фичи. Явная модель
    // (Session.Model) — её имя/метка. «Авто» — боевой резолвер ModelAssignmentResolver.ExecutorModel
    // (минор 4): та же модель, что реально едет в чат (уровень задачи → матрицы персоны → дефолт
    // места «исполнитель» = Strong → UserModelTierResolver.ModelFor, единственная точка, инвариант
    // CLAUDE.md). assignments null (Llm выключена) → null (без разворота). Функцию передаём
    // в проектор: он в Main не лезет (инвариант изоляции вертикалей).
    // internal — тестовый хук (минор 5): сверка owner-check задачи (чужая задача → «без задачи»).
    internal Func<FeatureIndexSession, (string? Label, string? Full)>? ChatModelResolver()
    {
        if (assignments is null) return null;
        return s =>
        {
            // Явная модель чата: имя + короткая метка.
            if (!string.IsNullOrWhiteSpace(s.Model))
                return (ModelShortLabel(s.Model), ModelDisplayName(s.Model));
            // «Авто»: боевой резолвер. tasks.GetById — общий словарь, поэтому проверяем владельца
            // (fail-closed, минор 5): чужая задача → как без задачи (резолв по персоне/месту).
            var task = s.TaskId is not null ? tasks.GetById(s.TaskId) : null;
            if (task is not null && task.OwnerId != UserId) task = null;
            var persona = task?.PersonaId is not null ? personas.Get(task.PersonaId, UserId) : null;
            // (минор 8) assignments здесь не null (null-случай — Llm выключена — обработан выше,
            // функция не создаётся) → мёртвая проверка убрана.
            string? model = task is not null
                // Слот задачи: уровень задачи (постановщик) → PersonaModel (модель персоны → уровень).
                ? assignments.ExecutorModel(task, persona, UserId)
                // Задачи нет (или чужая): модель персоны; у персоны без явной модели/уровня
                // подхватывается дефолт места «исполнитель» (Strong) — PersonaModel(placeDefaultTier:
                // Strong). Персоны нет (null) → null: «Авто» без модели (в контексте фичи места нет).
                : assignments.PersonaModel(persona, UserId,
                    LocalActionCatalog.DefaultTierOf(LocalActionCatalog.TasksExecutor));
            var full = model is null ? "Авто" : $"Авто → {ModelDisplayName(model)}";
            return ("Авто", full);
        };
    }

    // Полное имя модели для чипа: Claude → «Claude {Семейство}», иначе id модели как есть.
    // internal — для сверки «Авто» с боевым резолвером (тест минора 4).
    internal static string? ModelDisplayName(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (ClaudeModelFamily.Resolve(model) is { } family)
            return $"Claude {family.DisplayName}";
        return model;
    }

    // Короткая метка модели: Claude → семейство («Opus»), иначе первое слово id («Qwen»).
    private static string? ModelShortLabel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (ClaudeModelFamily.Resolve(model) is { } family)
            return family.DisplayName;
        return model.Split(' ')[0];
    }

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
        // ProjectFull: ПОЛНЫЙ (необлегчённый) run — null, если эпика нет (нет/чужой/удалён —
        // один 404, см. выше). Тело совпадает с 404 POST merge: { error: "Фича не найдена" }
        // (фронт читает error).
        // Б7: снимок сессий владельца → исполнители/шаги/витки (chatId/reviewChatId) fail-closed,
        // как sessionId события ниже; одинаковые исполнители, что в features?sessionId= (ProjectForSession).
        // Б4: chats[] — все чаты фичи из снимка владельца (fail-closed); modelLabel/modelFull — та же
        // функция (ChatModelResolver), что у исполнителей: список и страница дают одинаковые.
        var snapshot = SessionSnapshot();
        var resolver = ChatModelResolver();
        var run = features.ProjectFull(featureId, UserId, snapshot, resolver);
        if (run is null) return NotFound(new { error = "Фича не найдена" });
        // Снимок сессий владельца — как для индекса (SessionSnapshot): sessionId события наружу
        // только если в нём (fail-closed: удалённый/чужой чат → null). Лента считается по
        // ПОЛНОМУ run: reason вердикта = Verification.Notes, который Slim обнуляет для
        // списков/ответа (контракт master). run в ответе облегчён (SlimRun) — тот же контракт.
        var all = FeatureProjector.BuildEvents(run, snapshot);
        var eventsTotal = all.Count;
        var events = all.Take(ResolveEventsLimit(eventsLimit)).ToList();
        // Б-pl: документы фичи (вкладка «Планы и макеты») — СПИСОК (без текста) по ПОЛНОМУ run
        // (до SlimRun: Slim обнуляет ResultMarkdown, а hasText/mockupPaths — из него).
        var plans = FeatureProjector.BuildPlans(run);
        // Б4: chats[] — все чаты фичи (эпик + дочерние) из снимка владельца; только features/{id}
        // (в SlimRun/индекс chats не входит). kind: executor — чат задачи, curator/orchestrator/key
        // — чат эпика по признакам (BuildFeatureChats).
        var allFeatureTasks = new List<TaskItem> { run.Epic };
        allFeatureTasks.AddRange(run.Steps);
        allFeatureTasks.AddRange(run.Reviews);
        allFeatureTasks.AddRange(run.Qa);
        if (run.Final is not null) allFeatureTasks.Add(run.Final);
        if (run.Merge is not null) allFeatureTasks.Add(run.Merge);
        allFeatureTasks.AddRange(run.Cut);
        allFeatureTasks.AddRange(run.Plan);
        var chats = FeatureProjector.BuildFeatureChats(run.Epic, allFeatureTasks, snapshot, resolver);
        // Б5a/Б5b: stats — график времени фичи по ПОЛНОМУ run (до SlimRun: интервалы work из
        // ClaudeStartedAt/CompletedAt, которые Slim не трогает, но run облегчён для ответа).
        // Индекс/список stats НЕ несут (лёгкий путь) — только страница фичи: чтение журнала
        // (FeatureJournalStore.Read) — ТОЛЬКО здесь, не в индексе/списке.
        // Б5b: журнал фичи (опционально — null, если подсистема Tasks выключена): полный
        // (StartsWithCreated && !Truncated) → timeline в точном режиме (work/waiting/error/idle)
        // и модели breakdown из журнала; иначе approx (по задачам). Выключенный журнал →
        // source=approx, не 500 (Read сам не бросает: fail-closed на кривой id/отсутствие файла).
        var journalResult = journal?.Read(UserId, featureId);
        var journalComplete = journalResult is { StartsWithCreated: true, Truncated: false };
        // Б6: breakdown (разрезы по персонам/моделям/ролям) + longestNow («дольше всех сейчас») —
        // тоже по ПОЛНОМУ run, на том же снимке владельца (fail-closed) и now. Журнал полный →
        // модели из журнала (turn.model), иначе из снимка через resolver (резолв «Авто» — Б4).
        var now = DateTime.UtcNow;
        // Б9b/Б9c: расход фичи (stats.spend). Атрибуция (единое правило членства,
        // FeatureChatMembership — доработка Б9c): задачи фичи (id) + сессии (LinkedSessionId) +
        // чаты, состоящие ТОЛЬКО в этой фиче (M[c] = {F}); ОБЩИЕ чаты (M[c] ⊇ {F}, |M[c]| ≥ 2)
        // вычитаются из прямого среза (exclude) и делятся поровну (shared). Шов опционален:
        // null (Spend выключен) → spend = null, не 500.
        var taskIds = allFeatureTasks
            .Select(t => t.Id)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        var F = run.Epic.Id;
        var owner = tasks.GetByOwner(UserId).ToList();
        // Единое правило «чат → фичи» (executor ∪ curator ∪ orchestrator), без фильтра по проекту.
        var membership = FeatureChatMembership.Build(owner, snapshot);
        // Общие чаты фичи: входят в 2+ фичи (F ∈ M[c], |M[c]| ≥ 2). Исключение главнее обеих осей.
        var sharedChatIds = membership
            .Where(kv => kv.Value.Contains(F) && kv.Value.Count >= 2)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        // sessionIds: LinkedSessionId задач + чаты, состоящие ТОЛЬКО в этой фиче (M[c] = {F}),
        // минус общие (они — в долю, не в прямой срез).
        var sessionIds = allFeatureTasks
            .Select(t => t.LinkedSessionId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (chatId, feats) in membership)
            if (feats.Count == 1 && feats.Contains(F))
                sessionIds.Add(chatId);
        sessionIds.ExceptWith(sharedChatIds);
        // Один проход (Slice): итог и записи из одного набора — без окна рассогласования между
        // двумя отдельными сканами стора (минор доработки Б9b). spend null (Spend выключен) →
        // оба null, как раньше (spend?.Totals / spend?.Records).
        FeatureSpendTotals? spendTotals = null;
        IReadOnlyList<FeatureSpendRecord>? spendRecords = null;
        if (spend is not null)
            (spendTotals, spendRecords) = spend.Slice(UserId, taskIds, sessionIds, sharedChatIds, from: run.Epic.CreatedAt);

        // Б9c: доля ОБЩИХ (оркестраторских) чатов фичи. «Общий чат» — чат, входящий в 2+ фичи
        // владельца (единое правило FeatureChatMembership: executor ∪ curator ∪ orchestrator
        // эпика и дочерних задач). Их расход делится ПОРОВНУ (1/N) между фичами, активными на
        // момент хода (state ∈ {running, waiting, merging}). Доля текущей фичи → stats.spend.shared
        // + ВКЛЮЧЕНА в итоги. Нет общих чатов или Spend выключен (spendTotals null) → shared: null.
        FeatureSpendShared? shared = null;
        bool sharedCostPartial = false;
        var sharedHidden = new List<string>();
        if (spendTotals is not null && sharedChatIds.Count > 0)
        {
            // (1) Карта chatId → M[c] (состав каждого общего чата фичи) — знаменатель записи.
            //     M[c] = membership[c]: множество фич, в которые чат входит (FeatureChatMembership).
            var chatParticipants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var chatId in sharedChatIds)
                chatParticipants[chatId] = membership[chatId];

            // (2) Кэш журнала на запрос (решение Александра, п.в): featureId → FeatureJournalReadResult?,
            //     Read один раз на фичу, включая текущую (journalResult уже прочитан выше) —
            //     глобального кэша нет. journal null (подсистема выключена) → null, не 500.
            var journalCache = new Dictionary<string, FeatureJournalReadResult?>(StringComparer.Ordinal)
            {
                [run.Epic.Id] = journalResult,
            };
            FeatureJournalReadResult? ReadJournal(string fid)
            {
                if (!journalCache.TryGetValue(fid, out var jr))
                {
                    jr = journal?.Read(UserId, fid);
                    journalCache[fid] = jr;
                }
                return jr;
            }
            // Журнал фичи полный: StartsWithCreated && !Truncated (единый предикат, как в п.4).
            bool JournalComplete(string fid) =>
                ReadJournal(fid) is { StartsWithCreated: true, Truncated: false };

            // (3) Участницы = объединение M[c] по общим чатам фичи (для интервалов и minCreated).
            var participants = new HashSet<string>(StringComparer.Ordinal);
            foreach (var chatId in sharedChatIds)
                foreach (var fid in membership[chatId])
                    participants.Add(fid);

            // (4) ОДИН проход по участницам (решение Александра, п.8: один ProjectFull на
            // участницу): ProjectFull даёт и интервалы, и Epic.CreatedAt — для границы окна (from)
            // и для clipFrom journal-интервалов (знаменатели режимов однородны: оба обрезаются по
            // epic.CreatedAt, минор 3). Источник интервалов выбирается ПО УЧАСТНИЦЕ: полный
            // журнал участницы → её интервалы из её журнала, иначе approx. Так интервалы фичи
            // не зависят от того, чьё состояние запрашивают (глобальный выбор по объединению
            // давал F+H ≠ расход чата: у F в запросе от F approx, в запросе от H журнал).
            var activities = new List<FeatureActivity>();
            var minCreated = DateTime.MaxValue;
            foreach (var fid in participants)
            {
                var p = features!.ProjectFull(fid, UserId, snapshot, resolver);
                var intervals = JournalComplete(fid)
                    ? SharedSpendSplitter.JournalActivity(ReadJournal(fid)!, now, p?.Epic.CreatedAt)
                    : SharedSpendSplitter.ApproxActivity(p, now);
                activities.Add(new FeatureActivity(fid, intervals));
                if (p is not null && p.Epic.CreatedAt < minCreated)
                    minCreated = p.Epic.CreatedAt;
            }
            if (minCreated == DateTime.MaxValue) minCreated = run.Epic.CreatedAt;

            // (5) Записи ВСЕХ общих чатов — ОДИН Records (from = min CreatedAt участниц).
            var sharedRecords = spend!.Records(UserId, [], sharedChatIds, [], from: minCreated);

            // (6) Метод каждого общего чата: journal, если (a) журналы ВСЕХ M[c] полные И
            //     (b) среди записей чата в окне нет дневных строк; иначе approx. Дневная строка
            //     понижает метку независимо от активных (п.5). Итог фичи: journal, только если
            //     ВСЕ её общие чаты journal (смысл метки — комментарий у FeatureSpendShared.Method).
            var method = "journal";
            foreach (var (chatId, m) in chatParticipants)
            {
                var complete = m.All(JournalComplete);
                var hasDaily = sharedRecords.Any(r => r.SessionId == chatId && r.IsDaily);
                if (!(complete && !hasDaily)) { method = "approx"; break; }
            }

            // (7) Деление: знаменатель = M[rec.SessionId] (состав чата), не объединение участниц.
            var (share, shareCostPartial, shareHidden) =
                SharedSpendSplitter.Split(sharedRecords, activities, chatParticipants, run.Epic.Id, method);
            sharedCostPartial |= shareCostPartial;
            sharedHidden.AddRange(shareHidden);
            if (share.Turns > 0 || share.InputTokens > 0 || share.OutputTokens > 0
                || share.CacheReadTokens > 0 || share.CacheCreationTokens > 0 || share.CostUsd > 0)
                shared = share;
        }
        sharedHidden = sharedHidden.Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();

        // FeatureSpendTotals + доля общего чата → FeatureSpend (camelCase в JSON). Итоги
        // (токены/цена/costPartial/hiddenCostModels) ВКЛЮЧАЮТ долю: токены и цена складываются,
        // costPartial/hiddenCostModels — объединяются (та же логика надёжности цены, что в срезе).
        var allHidden = spendTotals is null
            ? sharedHidden
            : spendTotals.HiddenCostModels.Concat(sharedHidden)
                .Distinct(StringComparer.Ordinal).OrderBy(m => m, StringComparer.Ordinal).ToList();
        FeatureSpend? featureSpend = spendTotals is null ? null : new FeatureSpend(
            InputTokens: spendTotals.InputTokens + (shared?.InputTokens ?? 0),
            OutputTokens: spendTotals.OutputTokens + (shared?.OutputTokens ?? 0),
            CacheReadTokens: spendTotals.CacheReadTokens + (shared?.CacheReadTokens ?? 0),
            CacheCreationTokens: spendTotals.CacheCreationTokens + (shared?.CacheCreationTokens ?? 0),
            CostUsd: spendTotals.CostUsd + (shared?.CostUsd ?? 0),
            CostPartial: spendTotals.CostPartial || sharedCostPartial,
            HiddenCostModels: allHidden,
            // from — контракт string (не null, TS featureStats.ts): срез пуст (записей нет) →
            // дата начала фичи (создание эпика), не null (решение по минору Б9b: не правим TS-тип).
            From: (spendTotals.From ?? DateOnly.FromDateTime(run.Epic.CreatedAt)).ToString("yyyy-MM-dd"),
            Shared: shared);
        var (breakdown, longestNow) = FeatureBreakdown.Build(run, journalResult, snapshot, resolver, now, spendRecords);
        var stats = new FeatureStats(
            Source: journalComplete ? "journal" : "approx",
            Truncated: journalResult?.Truncated ?? false,
            Timeline: FeatureTimeline.Build(run, journalResult, now),
            Breakdown: breakdown,
            LongestNow: longestNow,
            Spend: featureSpend);
        // Б-mk-b: макеты фичи (вкладка «Планы и макеты»): mockups[] + mockupsUnavailable.
        // Порядок и fail-closed — в BuildMockups (ADR-031 «Макеты фичи»).
        var (mockups, mockupsUnavailable) = BuildMockups(run, catalog);
        return Ok(new { run = FeatureProjector.SlimRun(run), events, eventsTotal, chats, plans, stats,
            mockups, mockupsUnavailable });
    }

    // Б-mk-b: склейка макетов фичи (ADR-031 «Макеты фичи»). Порядок (fail-closed, сторож G10):
    //   1. эпик → его проект: только если принадлежит текущему владельцу (GetByOwner);
    //      чужой/нет → mockups: [] (файлы чужого проекта не трогаем);
    //   2. локальный проект (ProjectCapabilities.FilesOnServer=false) → [] + "device"
    //      ДО любого вызова Docs (файлы на устройстве, диск сервера не читаем);
    //   3. Docs: catalog?.Find (null — каталога нет в DI → [] без флага, не 500; в проде
    //      Docs регистрируется безусловно, тумблера нет — ветка защитная, жива в тестах).
    // TaskId/AuthorPersonaId записи — по первой (по CreatedAt) задаче фичи, в linkedFiles
    // которой есть path макета или любой из его files; иначе null. Сортировка: ModifiedAt
    // убыв., null в конце (детерминизм — путь).
    private (IReadOnlyList<MockupEntry> Mockups, string? Unavailable) BuildMockups(FeatureRun run, MockupCatalog? catalog)
    {
        // 1. Проект эпика: только если он принадлежит текущему владельцу (GetByOwner, fail-closed).
        var project = run.Epic.ProjectId is { } pid
            ? projects.GetByOwner(UserId).FirstOrDefault(p => p.Id == pid)
            : null;
        if (project is null) return ([], null);

        // 2. Локальный проект (файлы на устройстве) → [] + "device" ДО Docs (G10).
        if (!ProjectCapabilities.FilesOnServer(project))
            return ([], "device");

        // 3. Docs: catalog?.Find (null — каталога нет в DI → [] без флага). linkedPaths —
        //    linkedFiles всех задач ПОЛНОГО run (эпик + шаги + ревью + qa + финал + мерж +
        //    разрез + план), только из проекта эпика (см. AllLinkedFiles).
        var linkedPaths = AllLinkedFiles(run);
        var found = catalog?.Find(project.RootPath, run.FeatureId, linkedPaths) ?? [];

        // 4. TaskId/AuthorPersonaId — по первой (по CreatedAt) задаче, в linkedFiles которой есть
        //    path макета или любой из его files; иначе null.
        var attributed = found
            .Select(e =>
            {
                var owner = OwnerTask(run, e);
                return e with { TaskId = owner?.Id, AuthorPersonaId = owner?.PersonaId };
            })
            .ToList();

        // 5. Сортировка: ModifiedAt убыв., null в конце; детерминизм — путь.
        attributed = attributed
            .OrderBy(e => e.ModifiedAt is null)
            .ThenByDescending(e => e.ModifiedAt ?? DateTime.MinValue)
            .ThenBy(e => e.Path)
            .ToList();

        return (attributed, null);
    }

    // Все задачи ПОЛНОГО run в одном списке: эпик + шаги + ревью + qa + финал + мерж +
    // разрез + план. Единый хелпер (не две копии): новая роль в run не должна молча отстать
    // в одном из мест (автор макета / linkedFiles) — обе точки берут список отсюда.
    private static List<TaskItem> AllRunTasks(FeatureRun run)
    {
        var all = new List<TaskItem> { run.Epic };
        all.AddRange(run.Steps);
        all.AddRange(run.Reviews);
        all.AddRange(run.Qa);
        if (run.Final is not null) all.Add(run.Final);
        if (run.Merge is not null) all.Add(run.Merge);
        all.AddRange(run.Cut);
        all.AddRange(run.Plan);
        return all;
    }

    // linkedFiles всех задач ПОЛНОГО run. Резолв идёт от RootPath проекта эпика, поэтому
    // берём linkedFiles только из задач того же проекта (иначе файл чужого проекта дал бы
    // exists:false-шум в списке макетов). Фильтр по ProjectId применяется ВСЕГДА: эпик без
    // проекта (ProjectId=null) → проходят только задачи тоже без проекта (ветка недостижима —
    // без проекта эпика BuildMockups каталог не вызывает, см. шаг 1).
    private static List<string> AllLinkedFiles(FeatureRun run)
    {
        var epicProject = run.Epic.ProjectId;
        return AllRunTasks(run)
            .Where(t => t.ProjectId == epicProject)
            .SelectMany(t => t.LinkedFiles)
            .ToList();
    }

    // Первая (по CreatedAt) задача фичи, в linkedFiles которой есть path макета или любой из его
    // files; иначе null. Пути нормализуем (регистр/разделители): linkedFiles в сторе может
    // отличаться от пути каталога регистром или обратным слэшем. Фильтр по проекту эпика —
    // как в AllLinkedFiles: иначе макет (из проекта эпика) мог бы получить taskId/authorPersonaId
    // задачи ДРУГОГО проекта.
    private static TaskItem? OwnerTask(FeatureRun run, MockupEntry entry)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Path };
        foreach (var f in entry.Files) targets.Add(f.Path);
        var epicProject = run.Epic.ProjectId;
        return AllRunTasks(run)
            .Where(t => t.ProjectId == epicProject)
            .Where(t => t.LinkedFiles.Any(p => targets.Contains(NormalizeLinkedPath(p))))
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefault();
    }

    // Нормализация пути linkedFiles для сравнения с путями каталога: прямые слэши, без
    // ведущего «/», регистр в нижний (Windows-диск регистронезависим, Linux — нет, но
    // сравнение делаем нечувствительным, чтобы не терять совпадение).
    private static string NormalizeLinkedPath(string? p) =>
        (p ?? "").Replace('\\', '/').TrimStart('/').ToLowerInvariant();

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

        // «Уже смержена» (State == merged: merge Done ∧ (финал принят ∨ эпик Done)) и «закрыта»
        // (State == closed) — ПЕРЕД «финал не принят»: у закрытой фичи без принятого финала
        // причина — «закрыта», а не «финал не принят» (п.5). Частичный мерж (merge Done, финал не
        // принят, эпик открыт) → State != merged: верхний гард НЕ срабатывает (инцидент 21.10).
        if (run.State == FeatureStates.Merged)
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
            var freshRun = features.Project(featureId, UserId);
            var existing = freshRun?.Merge;
            if (existing is not null)
            {
                // п.2/п.73: merge-задача закрыта (Done) в окне между снимком и гейтом → 409: дубль
                // не проходит (верхний гард смотрел на устаревший снимок). Текст отличен от верхнего
                // гарда («Фича уже смержена»), чтобы тест гонки различал, какая ветка сработала.
                // Частичный мерж (merge Done при State != merged) сюда не доходит: верхний гард
                // «!run.Ready → Финал не принят» (921) отсекает его раньше (§7.8) — ветка «создать
                // новую» была мёртвой и убрана, чтобы код не обещал несуществующую обработку.
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
