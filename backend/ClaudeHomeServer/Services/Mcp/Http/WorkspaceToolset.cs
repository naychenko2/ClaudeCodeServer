using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Deploy;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Рабочее пространство владельца (wsp: projects/files/knowledge/search/git/kb/chats/deploy)
/// поверх HTTP-транспорта — самый крупный переехавший с node сервер (ADR-012, фаза 2
/// волна 3). Раньше это был mcp/workspace-server: JSON-RPC-фасад, ходивший в наш же REST
/// сервисным JWT. Здесь вызовы идут напрямую в сервисы через DI — HTTP-хопа через
/// собственный Kestrel нет. Общая с REST оркестрация (chats_send/report_up, каталог баз
/// знаний) вынесена в SessionMessagingService/KnowledgeBaseCatalogService — не дублируется.
///
/// Маршрут — <c>POST /mcp/wsp/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ (эквивалент env
/// WORKSPACE_PROJECT_ID/WORKSPACE_SECTIONS/WORKSPACE_PROJECT_IDS/WORKSPACE_SELF_SESSION_ID):
/// по ней тулсет живьём, на каждый tools/list и tools/call, резолвит секции и зону проектов
/// формулой SessionManager.BuildWorkspacePlan — той же, что собирает контекст адаптера
/// (урок приёмки волны 2: состав и его отпечаток обязаны считаться ОДНОЙ формулой).
/// Ключ сервера — wsp, НЕ workspace: claude CLI молча отбрасывает сервер с зарезервированным
/// именем workspace из --mcp-config.
///
/// Изоляция (проверяется на КАЖДЫЙ вызов, а не при построении контекста):
/// - сессия из хвоста обязана принадлежать владельцу токена (GetOwned) — чужая это отказ
///   и пустой tools/list (fail-closed);
/// - projectId приходит параметром инструмента и контролируется моделью: проект обязан
///   принадлежать владельцу токена И входить в зону сессии (AllowedProjectIds плана);
///   проверка одинакова для чтения и записи (урок приёмки волны 2 — «запись проверяет
///   только readonly-подмножество»);
/// - все пути файлов — только через FileService.SafeJoin внутри FileService (инвариант
///   проекта: path traversal отбивается там);
/// - секция инструмента проверяется и в составе (ToolsFor), и на вызове (defense-in-depth);
/// - разрушающие операции (files_delete/chats_delete) и запись в чужие чаты (chats_send/
///   chats_report_up) гейтятся DelegatedTurnGate — MVC-фильтр на McpTransportController
///   не применяется вовсе, тулсет зовёт сервисы через DI.
///
/// WORKSPACE_WRITE (фиксация, ADR-012): уже константа "1" со времён снятия WriteIntentGate —
/// write-инструменты в составе всегда; safety-уровень — права персоны (Persona.Tools /
/// ExtraDisallowedTools на уровне CLI: профиль «Только чтение» гасит все мутирующие
/// mcp__wsp__* через PersonaAccessPolicy.ReadOnlyDisallowed — правка волны 3.1) и гейты выше.
///
/// Сторож парности со stdio-веткой отката — WorkspaceToolsetParityTests (index.js заморожен).
/// </summary>
public sealed partial class WorkspaceToolset(
    ProjectManager projects,
    ProjectGroupManager groups,
    SessionManager sessions,
    PersonaManager personas,
    FileService files,
    NotesService notes,
    DocumentAiService docAi,
    KnowledgeService knowledge,
    WorkspaceKnowledgeStore workspaceStore,
    ProjectKnowledgeSyncService knowledgeSync,
    UnifiedSearchService search,
    Git.GitService git,
    Git.CommitAttributionService commitAttribution,
    UserStore users,
    TeamMemoryService teamMemory,
    DeployService deploy,
    SessionMessagingService messaging,
    TaskManager tasks,
    DefaultAssistantProvisioner provisioner,
    KnowledgeBaseCatalogService knowledgeCatalog,
    IHubContext<SessionHub> hub) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/wsp/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession)
    public const string ServerName = "wsp";

    // Ограничение выдачи files_tree — дерево большого проекта не должно раздувать контекст
    internal const int TreeMaxEntries = 500;
    // Потолок выдачи files_read, как у встроенного Read: файл целиком живёт в контексте
    // до конца сессии, а читают им обычно ради куска. Явный limit больше потолка уважаем —
    // модель попросила осознанно.
    internal const int ReadMaxLines = 2000;

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    // У параметризованного тулсета состава без хвоста не существует: контроллер на
    // /mcp/wsp без хвоста отвечает 404 до диспетчера
    public IReadOnlyList<McpToolSchema> Tools => [];

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out var session, out _, out var plan, out _)
            ? ToolsForSections(plan.Sections, ContextNote(session))
            : [];

    // Контекстная заметка в описании projects_list — как CONTEXT_NOTE stdio-ветки
    // (у неё собиралась из env при старте процесса; здесь — живьём на каждый tools/list)
    internal static string ContextNote(Session session) =>
        session.ProjectId is { } pid
            ? $"Текущая сессия идёт в проекте {pid}."
            : "Текущая сессия — чат вне проекта.";

    // Состав по секциям плана: фильтр полного каталога по карте инструмент → секция.
    // internal для сторожа парности (оси секций stdio-ветки).
    internal static IReadOnlyList<McpToolSchema> ToolsForSections(IReadOnlyCollection<string> sections,
        string contextNote) =>
        AllTools.Where(t => sections.Contains(ToolSection[t.Name]))
            .Select(t => t.Description.Contains(ContextNoteToken)
                ? t with { Description = t.Description.Replace(ContextNoteToken, contextNote) }
                : t)
            .ToList();

    // Инструмент → секция (эквивалент TOOL_SECTION stdio-ветки). defense-in-depth:
    // вызов инструмента выключенной секции отбивается даже при ошибке экспозиции состава
    internal static readonly IReadOnlyDictionary<string, string> ToolSection =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projects_list"] = "projects",
            ["projects_get"] = "projects",
            ["projects_create"] = "projects",
            ["projects_update"] = "projects",
            ["tags_apply"] = "projects",
            ["files_tree"] = "files",
            ["files_read"] = "files",
            ["files_document_read"] = "files",
            ["files_document_summary"] = "files",
            ["files_document_extract"] = "files",
            ["files_to_markdown"] = "files",
            ["files_write"] = "files",
            ["files_search"] = "files",
            ["files_mkdir"] = "files",
            ["files_rename"] = "files",
            ["knowledge_search"] = "knowledge",
            ["knowledge_status"] = "knowledge",
            ["knowledge_index"] = "knowledge",
            ["search_unified"] = "search",
            ["git_status"] = "git",
            ["git_diff"] = "git",
            ["git_log"] = "git",
            ["git_commit"] = "git_write",
            ["git_stage"] = "git_write",
            ["kb_list"] = "knowledge_bases",
            ["kb_get"] = "knowledge_bases",
            ["kb_search"] = "knowledge_bases",
            ["kb_add_document"] = "knowledge_bases",
            ["chats_list"] = "chats",
            ["chats_history"] = "chats",
            ["chats_create"] = "chats",
            ["chats_send"] = "chats",
            ["chats_report_up"] = "chats",
            ["chats_update"] = "chats",
            ["files_delete"] = "destructive",
            ["chats_delete"] = "destructive",
            ["deploy_start"] = "deploy",
            ["deploy_status"] = "deploy",
            ["deploy_rollback"] = "deploy",
        };

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

    // Один сегмент — id сессии; форма как у resumeSessionId-белого списка (хвост строим мы,
    // но проверяем форму всё равно — он приезжает из URL)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    /// <summary>
    /// Резолв хвоста в сессию ВЛАДЕЛЬЦА токена + ЖИВОЙ план сервера (секции и зона —
    /// SessionManager.BuildWorkspacePlan, одна формула с контекстом адаптера). Чужая сессия —
    /// отказ; план null (все секции выключены у персоны) — отказ: stdio-ветка в этом случае
    /// вообще не подключалась бы.
    /// </summary>
    private bool TryResolve(McpToolCallContext context,
        out Session session, out Persona? persona,
        out SessionManager.WorkspaceMcpPlan plan,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        persona = null;
        plan = null!;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера рабочего пространства — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к рабочему пространству закрыт.";
            return false;
        }
        session = owned;
        persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        var resolved = sessions.BuildWorkspacePlan(context.OwnerId, session.ProjectId, persona);
        if (resolved is null)
        {
            error = "Сервер рабочего пространства недоступен этой персоне — все секции выключены привязками.";
            return false;
        }
        plan = resolved;
        error = null;
        return true;
    }

    /// <summary>
    /// Проект из параметра инструмента (контролируется моделью!) с ПОЛНОЙ проверкой:
    /// принадлежит владельцу токена И входит в зону сессии. Одна проверка для чтения и
    /// записи — урок приёмки волны 2 (у tasks запись проверяла только readonly-подмножество,
    /// пропуская запись в любой проект владельца).
    /// </summary>
    private string? ProjectDenied(McpToolCallContext context, SessionManager.WorkspaceMcpPlan plan,
        string? projectId)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return "Не указан projectId";
        if (plan.AllowedProjectIds is { Count: > 0 } allowed && !allowed.Contains(projectId))
            return $"Проект {projectId} вне разрешённой зоны этой сессии";
        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != context.OwnerId)
            return $"Проект {projectId} не найден или недоступен";
        return null;
    }

    // Проект с проверкой (ProjectDenied) + сам объект, без двойного чтения стора
    private bool TryGetProject(McpToolCallContext context, SessionManager.WorkspaceMcpPlan plan,
        JsonObject arguments, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Project? project,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? denied)
    {
        project = null;
        var projectId = StringArg(arguments, "projectId");
        denied = ProjectDenied(context, plan, projectId);
        if (denied is not null) return false;
        project = projects.GetById(projectId)!;
        return true;
    }

    // Гейт делегированного хода для деструктива и записи в чужие чаты — та же точка, что
    // [DenyOnDelegatedTurn] на REST-эндпоинтах (фильтр на McpTransportController не
    // применяется вовсе). fail-closed без вызывателя: заголовки кладёт наш конфиг хода всегда
    // (ветка формально недостижима — сессию резолвит TryResolve раньше; защита на будущее).
    private string? DelegatedDenied(McpToolCallContext context, Session callerSession, string action) =>
        DelegatedTurnGate.Decide(sessions, context.OwnerId, callerSession.Id, action,
            alsoWhenExecutorSuppressed: false, allowInTeamImplement: false, allowInWorkLoop: false,
            failOpenWhenUnknown: false) is { Allowed: false } gate
            ? gate.DenyText
            : null;

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var persona, out var plan, out var routeError))
            return Deny(routeError);

        // Секция инструмента обязана быть включённой: состав (ToolsFor) фильтрует экспозицию,
        // а исполнение перепроверяем отдельно — деструктив не должен выполниться даже при
        // ошибке экспозиции (defense-in-depth, урок приёмки волны 2)
        if (!ToolSection.TryGetValue(tool, out var section))
            return Deny($"Неизвестный инструмент: {tool}");
        if (!plan.Sections.Contains(section))
            return Deny($"Инструмент {tool} недоступен: секция {section} выключена для этой сессии");

        return section switch
        {
            "projects" => await ProjectsCall(tool, arguments, context, session, plan),
            "files" => await FilesCall(tool, arguments, context, plan, ct),
            "knowledge" => await KnowledgeCall(tool, arguments, context, plan),
            "search" => await SearchCall(tool, arguments, context, plan),
            "git" or "git_write" => await GitCall(tool, arguments, context, session, plan, ct),
            "knowledge_bases" => await KbCall(tool, arguments, context),
            "chats" => await ChatsCall(tool, arguments, context, session, plan),
            "destructive" => await DestructiveCall(tool, arguments, context, session, plan),
            "deploy" => await DeployCall(tool, arguments, context, session, ct),
            _ => Deny($"Неизвестный инструмент: {tool}"),
        };
    }

    // --- Секция projects (projects_* + tags_apply) ---

    private async Task<McpToolCallResult> ProjectsCall(string tool, JsonObject arguments,
        McpToolCallContext context, Session session, SessionManager.WorkspaceMcpPlan plan)
    {
        switch (tool)
        {
            case "projects_list":
            {
                var groupName = groups.GetByOwner(context.OwnerId)
                    .ToDictionary(g => g.Id, g => g.Name);
                var query = StringArg(arguments, "query").Trim();
                var items = projects.GetByOwner(context.OwnerId)
                    .Where(p => plan.AllowedProjectIds is not { Count: > 0 } allowed
                        || allowed.Contains(p.Id))
                    .Where(p => query.Length == 0
                        || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .Select(p =>
                    {
                        // isCurrent — как stdio (p.id === PROJECT_ID || undefined): ключа нет
                        // у прочих проектов, поэтому словарь, а не анонимный тип
                        var item = new Dictionary<string, object?>
                        {
                            ["id"] = p.Id,
                            ["name"] = p.Name,
                            ["groupName"] = p.GroupId is { } gid
                                && groupName.TryGetValue(gid, out var gn) ? gn : null,
                            ["rootPath"] = p.RootPath,
                            ["sessionCount"] = sessions.CountByProject(p.Id),
                        };
                        if (p.Id == session.ProjectId) item["isCurrent"] = true;
                        return item;
                    })
                    .ToList();
                return Json(items);
            }

            case "projects_get":
            {
                if (!TryGetProject(context, plan, arguments, out var p, out var denied))
                    return Deny(denied);
                return Json(new
                {
                    id = p.Id,
                    name = p.Name,
                    rootPath = p.RootPath,
                    groupId = p.GroupId,
                    systemPrompt = p.SystemPrompt,
                    sessionCount = sessions.CountByProject(p.Id),
                    createdAt = p.CreatedAt,
                    updatedAt = p.UpdatedAt,
                });
            }

            case "projects_create":
            {
                // Сужение зоны: сессии с ограниченным списком проектов новые проекты не создают
                if (plan.AllowedProjectIds is { Count: > 0 })
                    return Deny("Создание проектов недоступно: зона этой сессии ограничена перечисленными проектами");
                var name = StringArg(arguments, "name").Trim();
                if (name.Length == 0) return Deny("Название проекта не может быть пустым");
                // Абсолютный rootPath НЕ принимаем (блокер приёмки волны 3.1): модель сама
                // переносила бы границу SafeJoin, подключив проектом любую папку сервера,
                // после чего файловые инструменты работали бы в ней законно. MCP создаёт
                // проект только в стандартном каталоге владельца; подключение существующей
                // папки — осознанное действие человека через UI (REST). Решение — ADR-012,
                // раздел «Волна 3.1». Параметр в схеме оставлен для паритета со stdio-веткой.
                var rootPath = OptionalArg(arguments, "rootPath");
                if (rootPath is not null)
                    return Deny("Подключение существующей папки недоступно из MCP: создайте проект "
                        + "без rootPath (папка появится в стандартном каталоге) либо попросите "
                        + "пользователя подключить нужную папку через интерфейс.");
                var groupId = OptionalArg(arguments, "groupId");
                try
                {
                    var username = users.GetById(context.OwnerId)?.Username ?? context.OwnerId;
                    // Без rootPath на диске создаётся папка в стандартном каталоге — как у
                    // stdio-ветки (она не передавала createDirectory, а REST-default false? —
                    // нет: stdio не слал поля, REST-дефолт CreateDirectory=false; создание
                    // папки при пустом rootPath делает сам ProjectManager.Create)
                    var p = projects.Create(name, rootPath, context.OwnerId, username,
                        createDirectory: false, groupId: groupId);
                    return Json(new { id = p.Id, name = p.Name, rootPath = p.RootPath, groupId = p.GroupId });
                }
                catch (DirectoryNotFoundException ex) { return Deny(ex.Message); }
                catch (ArgumentException ex) { return Deny(ex.Message); }
            }

            case "projects_update":
            {
                if (!TryGetProject(context, plan, arguments, out var p, out var denied))
                    return Deny(denied);
                // Частичное обновление: поле без ключа — не менять; groupId "" — убрать
                // из группы (семантика очистки — по ContainsKey, а не по «пусто = нет
                // параметра», урок приёмки волны 2)
                var name = arguments.ContainsKey("name") ? StringArg(arguments, "name") : null;
                var systemPrompt = arguments.ContainsKey("systemPrompt")
                    ? StringArg(arguments, "systemPrompt") : null;
                var groupId = arguments.ContainsKey("groupId") ? StringArg(arguments, "groupId") : null;
                var oldName = p.Name;
                try
                {
                    var updated = projects.Update(p.Id, name, rootPath: null, systemPrompt: systemPrompt,
                        groupId: groupId);
                    // Переименование: best-effort освежить имена Dify-датасетов — как REST
                    // (stdio-ветка ходила через тот же эндпоинт, побочка обязана сохраниться);
                    // сбой не ломает работу по id
                    if (!string.Equals(oldName, updated.Name, StringComparison.Ordinal))
                    {
                        var username = users.GetById(context.OwnerId)?.Username ?? context.OwnerId;
                        var datasetId = workspaceStore.GetByPath(updated.RootPath)?.DifyDatasetId;
                        if (!string.IsNullOrEmpty(datasetId))
                            try { await knowledge.RenameDatasetAsync(datasetId, $"{username}:{updated.Name}"); }
                            catch { /* стухшее имя не критично */ }
                        try { await teamMemory.RenameProjectDatasetAsync(context.OwnerId, updated.Id, username, updated.Name); }
                        catch { /* стухшее имя не критично */ }
                    }
                    return Json(new { id = updated.Id, name = updated.Name, rootPath = updated.RootPath, groupId = updated.GroupId });
                }
                catch (DirectoryNotFoundException ex) { return Deny(ex.Message); }
                catch (ArgumentException ex) { return Deny(ex.Message); }
                catch (KeyNotFoundException) { return Deny($"Проект {p.Id} не найден или недоступен"); }
            }

            case "tags_apply":
            {
                var entityType = StringArg(arguments, "entityType").Trim();
                if (entityType is not ("session" or "task"))
                    return Deny("entityType должен быть \"session\" или \"task\"");
                var entityId = StringArg(arguments, "entityId").Trim();
                if (entityId.Length == 0) return Deny("Не указан entityId");
                var incoming = StringsArg(arguments, "tags")
                    .Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                if (incoming.Count == 0) return Deny("Список tags пуст");
                var projectId = OptionalArg(arguments, "projectId");

                if (entityType == "session")
                {
                    // session: теги живут на проектной сессии — projectId нужен и для маршрута,
                    // и для реестра
                    if (projectId is null) return Deny("Для session обязателен projectId");
                    if (!TryGetProject(context, plan, arguments, out var project, out var denied))
                        return Deny(denied);
                    var registryAdded = EnsureRegistryTags(project, incoming);
                    var target = sessions.GetByProject(project.Id)
                        .FirstOrDefault(s => s.Id == entityId);
                    if (target is null)
                        return Deny($"Сессия {entityId} не найдена в проекте {project.Id}");
                    var merged = UnionStrings(target.Tags, incoming);
                    var updated = await sessions.UpdateAsync(entityId, context.OwnerId,
                        name: null, model: null, effort: null, tags: merged);
                    var answer = new Dictionary<string, object?>
                    {
                        ["entityType"] = "session",
                        ["id"] = updated?.Id ?? entityId,
                        ["projectId"] = project.Id,
                        ["tags"] = updated?.Tags ?? merged,
                    };
                    if (registryAdded.Count > 0) answer["registryAdded"] = registryAdded;
                    return Json(answer);
                }

                // task: projectId опционален — только для автосоздания в реестре. Сама задача
                // принадлежит владельцу по токену; личные задачи (без проекта) валидны.
                List<string> taskRegistryAdded = [];
                if (projectId is not null)
                {
                    if (!TryGetProject(context, plan, arguments, out var project, out var denied))
                        return Deny(denied);
                    taskRegistryAdded = EnsureRegistryTags(project, incoming);
                }
                var task = tasks.GetById(entityId);
                if (task is null || task.OwnerId != context.OwnerId)
                    return Deny($"Задача {entityId} не найдена.");
                // Зона сессии: при суженной зоне доступен только проектный ряд — личные
                // задачи и чужие проекты вне зоны (правка приёмки волны 3.1: раньше метки
                // менялись у любой задачи владельца)
                if (plan.AllowedProjectIds is { Count: > 0 } allowed
                    && (task.ProjectId is null || !allowed.Contains(task.ProjectId)))
                    return Deny($"Задача {entityId} вне разрешённой зоны этой сессии");
                var mergedLabels = UnionStrings(task.Labels, incoming);
                var updatedTask = tasks.Update(entityId, new UpdateTaskRequest(Labels: mergedLabels));
                // Бродкаст task_updated — как REST-путь (TasksController.Update): без него
                // интерфейс жил бы с устаревшими метками до перезагрузки (блокер волны 3.1;
                // TaskManager.Update бродкаста не делает — он был обязанностью контроллера)
                if (updatedTask is not null)
                    await hub.BroadcastTaskChangedAsync(context.OwnerId, "updated", updatedTask);
                var taskAnswer = new Dictionary<string, object?>
                {
                    ["entityType"] = "task",
                    ["id"] = entityId,
                    ["projectId"] = projectId ?? task.ProjectId,
                    ["labels"] = mergedLabels,
                };
                if (taskRegistryAdded.Count > 0) taskAnswer["registryAdded"] = taskRegistryAdded;
                return Json(taskAnswer);
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

    // Автосоздание тегов в реестре проекта: недостающие (case-insensitive) имена добавляются,
    // реестр сохраняется целиком с перенормировкой Order по позиции — как PUT /tags
    // контроллера (stdio-ветка ходила через него). Возвращает добавленные имена.
    private List<string> EnsureRegistryTags(Project project, IReadOnlyList<string> tags)
    {
        var registry = project.TagRegistry ?? [];
        var known = registry.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additions = new List<ProjectTag>();
        foreach (var name in tags)
        {
            if (!known.Add(name)) continue;
            additions.Add(new ProjectTag { Name = name, Color = null });
        }
        if (additions.Count == 0) return [];
        var merged = registry.Concat(additions).ToList();
        for (var i = 0; i < merged.Count; i++) merged[i].Order = i;
        projects.UpdateTags(project.Id, merged);
        return additions.Select(a => a.Name).ToList();
    }

    // Объединение списков строк с дедупом по регистру: сохраняем первое вхождение имени,
    // «Bug» и «bug» не плодят дубль (порт stdio-ветки)
    private static List<string> UnionStrings(IEnumerable<string>? existing, IEnumerable<string> incoming)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in (existing ?? Enumerable.Empty<string>()).Concat(incoming))
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;
            if (!seen.Add(name)) continue;
            result.Add(name);
        }
        return result;
    }

    // --- Секция files (files_*): все пути — только через FileService (SafeJoin внутри него) ---

    private async Task<McpToolCallResult> FilesCall(string tool, JsonObject arguments,
        McpToolCallContext context, SessionManager.WorkspaceMcpPlan plan, CancellationToken ct)
    {
        if (!TryGetProject(context, plan, arguments, out var p, out var denied))
            return Deny(denied);
        var root = p.RootPath;

        switch (tool)
        {
            case "files_tree":
            {
                var basePath = StringArg(arguments, "path").Replace('\\', '/').TrimEnd('/');
                IEnumerable<FileEntry> entries;
                try { entries = files.Tree(root, basePath, p.ShowHiddenFiles); }
                catch (DirectoryNotFoundException) { return Deny($"Папка не найдена: {basePath}"); }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                // Глубина считается от стартовой папки по числу сегментов относительного пути
                var baseDepth = basePath.Length > 0 ? basePath.Split('/').Length : 0;
                var depth = IntArg(arguments, "depth");
                var list = entries
                    .Select(e => (path: e.Path.Replace('\\', '/'), dir: e.IsDirectory, size: e.Size))
                    .Where(e => depth is null or <= 0
                        || e.path.Split('/').Length - baseDepth <= depth.Value)
                    .ToList();
                var truncated = list.Count > TreeMaxEntries;
                if (truncated) list = list.Take(TreeMaxEntries).ToList();
                var answer = new Dictionary<string, object?>
                {
                    ["entries"] = list.Select(e => new Dictionary<string, object?>
                    {
                        ["path"] = e.path,
                        ["dir"] = e.dir,
                        ["size"] = e.size,
                    }).ToList(),
                };
                if (truncated)
                {
                    answer["truncated"] = true;
                    answer["note"] = $"Показаны первые {TreeMaxEntries} записей — уточни path/depth";
                }
                return Json(answer);
            }

            case "files_read":
            {
                var path = StringArg(arguments, "path");
                string content;
                try
                {
                    if (Directory.Exists(FileService.SafeJoinPublic(root, path)))
                        return Deny("Файл не найден: путь ведёт в папку");
                    // Бинарники — только метаданные: base64 не тащим, он раздул бы контекст
                    var doc = files.GetDocumentInfo(path);
                    if (doc is { } d)
                        return Json(BinaryNote(path, d.Mime, files.GetFileSize(root, path)));
                    if (files.IsBinaryFile(root, path))
                        return Json(BinaryNote(path, BinaryMime(root, path), files.GetFileSize(root, path)));
                    content = files.ReadFile(root, path);
                }
                catch (FileNotFoundException) { return Deny($"Файл не найден: {path}"); }
                catch (DirectoryNotFoundException) { return Deny($"Файл не найден: {path}"); }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                // Читаем кусками всегда: без потолка полная выдача большого файла оседала в
                // контексте до конца сессии. Явный limit сильнее потолка — модель попросила
                // осознанно; limit: 0 трактуем как «не задан» (иначе nextOffset == offset,
                // и модель ходит по кругу)
                var lines = content.Split('\n');
                var start = Math.Max(0, IntArg(arguments, "offset") ?? 0);
                var limit = IntArg(arguments, "limit") is { } lim && lim > 0 ? lim : ReadMaxLines;
                var slice = lines.Skip(start).Take(limit).ToList();
                var nextOffset = start + slice.Count;
                var truncatedRead = nextOffset < lines.Length;
                var answer = new Dictionary<string, object?>
                {
                    ["path"] = path,
                    ["offsetLines"] = start,
                    ["totalLines"] = lines.Length,
                    ["content"] = string.Join("\n", slice),
                };
                if (truncatedRead)
                {
                    answer["truncated"] = true;
                    answer["nextOffset"] = nextOffset;
                    answer["note"] = $"Показаны строки {start}–{nextOffset - 1} из {lines.Length} — продолжение через offset: {nextOffset}";
                }
                return Json(answer);
            }

            case "files_search":
            {
                var query = StringArg(arguments, "query");
                var found = files.Search(root, query)
                    .Select(e => new Dictionary<string, object?>
                    {
                        ["path"] = e.Path.Replace('\\', '/'),
                        ["size"] = e.Size,
                    })
                    .ToList();
                return Json(found);
            }

            case "files_write":
            {
                var path = StringArg(arguments, "path");
                var content = StringArg(arguments, "content");
                try
                {
                    if (Directory.Exists(FileService.SafeJoinPublic(root, path)))
                        return Deny("Путь ведёт в папку, а не в файл");
                    try
                    {
                        files.WriteFile(root, path, content);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        // Файла/папки нет — создаём (как stdio-ветка: 404 → create → retry)
                        files.CreateFile(root, path, content);
                    }
                }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                return Text($"Файл {path} записан в проект {p.Id}.");
            }

            case "files_mkdir":
            {
                var path = StringArg(arguments, "path");
                try { files.CreateDirectory(root, path); }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                return Text($"Папка {path} создана в проекте {p.Id}.");
            }

            case "files_rename":
            {
                var oldPath = StringArg(arguments, "oldPath");
                var newPath = StringArg(arguments, "newPath");
                try
                {
                    files.Rename(root, oldPath, newPath);
                    // Комментарии к переименованному документу следуют за новым путём —
                    // привязка не сиротеет (как REST-эндпоинт rename)
                    try { notes.RewriteAnnotationTargets(context.OwnerId, p.Id, oldPath, p.Id, newPath, prefix: true); }
                    catch { /* перепись привязок — best-effort, rename уже состоялся */ }
                }
                catch (FileNotFoundException) { return Deny($"Файл не найден: {oldPath}"); }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                return Text($"{oldPath} → {newPath} (проект {p.Id}).");
            }

            case "files_document_read":
            {
                var path = StringArg(arguments, "path");
                // SafeJoinPublic внутри DocumentAbsPath — под try, как у остальных файловых
                // инструментов: traversal здесь обязан давать отказ, а не исключение (волна 3.1)
                try
                {
                    if (DocumentAbsPath(root, path) is not { } abs)
                        return Deny("Это не документ (pdf/docx/xlsx/pptx)");
                    var md = await docAi.ConvertAsync(abs, ct);
                    return md is null
                        ? Deny("Не удалось конвертировать документ (markitdown недоступен?)")
                        : Json(new Dictionary<string, object?> { ["path"] = path, ["markdown"] = md });
                }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
            }

            case "files_document_summary":
            {
                var path = StringArg(arguments, "path");
                try
                {
                    var text = await GetAiTextAsync(root, path, ct);
                    if (text is null) return Deny("Файл не поддерживается (нужен документ или текст)");
                    var summary = await docAi.SummaryAsync(context.OwnerId, text, ct);
                    return summary is null
                        ? Deny("Не удалось обработать файл")
                        : Json(new Dictionary<string, object?> { ["path"] = path, ["summary"] = summary });
                }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
            }

            case "files_document_extract":
            {
                var path = StringArg(arguments, "path");
                try
                {
                    var text = await GetAiTextAsync(root, path, ct);
                    if (text is null) return Deny("Файл не поддерживается (нужен документ или текст)");
                    var extract = await docAi.ExtractAsync(context.OwnerId, text, ct);
                    return extract is null
                        ? Deny("Не удалось обработать файл")
                        : Json(new
                        {
                            decisions = extract.Decisions,
                            dates = extract.Dates,
                            people = extract.People,
                            actionItems = extract.ActionItems,
                        });
                }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
            }

            case "files_to_markdown":
            {
                var path = StringArg(arguments, "path");
                if (path.Length == 0) return Deny("Нужен путь файла");
                string abs;
                try { abs = FileService.SafeJoinPublic(root, path); }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                if (!File.Exists(abs)) return Deny("Файл не найден");
                var md = await docAi.ConvertAsync(abs, ct);
                if (md is null)
                    return Deny("Не удалось конвертировать файл (markitdown недоступен или формат не поддержан)");
                // Опционально: восстановить Markdown-разметку локальной моделью (для pdf
                // без структуры)
                if (arguments["enhance"] is JsonValue e && e.TryGetValue<bool>(out var enh) && enh)
                    md = await docAi.EnhanceMarkdownAsync(context.OwnerId, md, ct);
                // Имя целевого .md — по исходному имени; каталог — targetDir или рядом с исходником
                var baseName = Path.GetFileNameWithoutExtension(path) + ".md";
                var targetDir = OptionalArg(arguments, "targetDir");
                var dir = string.IsNullOrWhiteSpace(targetDir)
                    ? (Path.GetDirectoryName(path.Replace('\\', '/')) ?? "")
                    : targetDir!.Replace('\\', '/').Trim('/');
                var targetRel = dir.Length == 0 ? baseName : $"{dir}/{baseName}";
                try
                {
                    // Целевая папка может не существовать (указана новая) — создаём
                    if (dir.Length > 0) files.CreateDirectory(root, dir);
                    files.WriteFile(root, targetRel, md);
                }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                return Json(new Dictionary<string, object?>
                {
                    ["savedPath"] = targetRel,
                    ["note"] = $"Файл трансформирован в Markdown → {targetRel}",
                });
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

    // Абсолютный путь документа с проверкой, что это просматриваемый документ
    // (pdf/docx/xlsx/pptx). Visio исключён: markitdown его не конвертирует.
    private string? DocumentAbsPath(string root, string path) =>
        files.GetDocumentInfo(path) is not { } d || d.Kind == "visio"
            ? null
            : FileService.SafeJoinPublic(root, path);

    // Текст файла для ИИ-действий (суть/выжимка): бинарный документ → markitdown; текстовый
    // файл → как есть; прочие бинарные → null (как GetAiTextAsync контроллера)
    private async Task<string?> GetAiTextAsync(string root, string path, CancellationToken ct)
    {
        if (Directory.Exists(FileService.SafeJoinPublic(root, path))) return null;
        if (files.GetDocumentInfo(path) is { } d)
        {
            if (d.Kind == "visio") return null;
            var abs = FileService.SafeJoinPublic(root, path);
            return File.Exists(abs) ? await docAi.ConvertAsync(abs, ct) : null;
        }
        return files.IsBinaryFile(root, path) ? null : files.ReadFile(root, path);
    }

    // Ответ на бинарный файл: только метаданные (как stdio-ветка)
    private static Dictionary<string, object?> BinaryNote(string path, string? mime, long size) =>
        new()
        {
            ["path"] = path,
            ["binary"] = true,
            ["mimeType"] = mime,
            ["fileSize"] = size,
            ["note"] = "Бинарный файл — содержимое не возвращается",
        };

    // MIME бинарника — те же ветки, что в FilesController.GetContent (картинка/видео/аудио/прочее)
    private string BinaryMime(string root, string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (FileService.IsVideoFile(path))
            return ext switch
            {
                "webm" => "video/webm",
                "mov" => "video/quicktime",
                "avi" => "video/x-msvideo",
                "mkv" => "video/x-matroska",
                _ => "video/mp4",
            };
        if (FileService.IsAudioFile(path))
            return ext switch
            {
                "wav" => "audio/wav",
                "ogg" => "audio/ogg",
                "flac" => "audio/flac",
                "aac" => "audio/aac",
                "m4a" => "audio/mp4",
                "opus" => "audio/opus",
                "weba" => "audio/webm",
                _ => "audio/mpeg",
            };
        if (ext == "svg") return "image/svg+xml";
        if (files.IsImageFile(root, path)) return $"image/{ext}";
        return "application/octet-stream";
    }

    // --- Секция knowledge (база знаний ПРОЕКТА) ---

    private async Task<McpToolCallResult> KnowledgeCall(string tool, JsonObject arguments,
        McpToolCallContext context, SessionManager.WorkspaceMcpPlan plan)
    {
        if (!TryGetProject(context, plan, arguments, out var p, out var denied))
            return Deny(denied);

        switch (tool)
        {
            case "knowledge_search":
            {
                var query = StringArg(arguments, "query").Trim();
                if (query.Length == 0)
                    return Json(new { items = Array.Empty<object>() });
                var datasetId = workspaceStore.GetByPath(p.RootPath)?.DifyDatasetId;
                if (string.IsNullOrEmpty(datasetId))
                    return Json(new { items = Array.Empty<object>(), hint = "знания не проиндексированы" });
                try
                {
                    var topK = Math.Clamp(IntArg(arguments, "topK") ?? 8, 1, 20);
                    var chunks = await knowledge.RetrieveAsync(datasetId, query, topK);
                    return Json(new
                    {
                        items = chunks.Select(c => new
                        {
                            content = c.Content,
                            score = c.Score,
                            documentName = c.DocumentName,
                        }),
                    });
                }
                catch (HttpRequestException ex) { return Deny($"Dify недоступен: {ex.Message}"); }
            }

            case "knowledge_status":
            {
                var wk = workspaceStore.GetByPath(p.RootPath);
                if (string.IsNullOrEmpty(wk?.DifyDatasetId))
                    return Json(new Dictionary<string, object?>
                    {
                        ["indexed"] = false,
                        ["total"] = 0,
                        ["documents"] = Array.Empty<object>(),
                    });
                // Сверка при открытии: подхватить правки, сделанные пока ватчеры не смотрели
                // (как REST GetStatus)
                knowledgeSync.QueueSync(p.RootPath);
                try
                {
                    var docs = await knowledge.ListAllDocumentsAsync(wk.DifyDatasetId);
                    return Json(new Dictionary<string, object?>
                    {
                        ["indexed"] = true,
                        ["total"] = docs.Total,
                        ["documents"] = docs.Data.Select(d => new
                        {
                            name = d.Name,
                            indexingStatus = d.IndexingStatus,
                        }).ToList(),
                    });
                }
                catch (HttpRequestException ex) { return Deny($"Dify недоступен: {ex.Message}"); }
            }

            case "knowledge_index":
            {
                var path = StringArg(arguments, "path");
                if (!KnowledgeService.IsKnowledgeIndexable(path))
                    return Deny($"Формат файла не поддерживается для индексирования: {Path.GetExtension(path)}");
                try
                {
                    var username = users.GetById(context.OwnerId)?.Username ?? context.OwnerId;
                    // Идемпотентная индексация через синк-сервис: имя документа = относительный
                    // путь, повторный вызов обновляет документ (не плодит дубль)
                    var (datasetId, doc) = await knowledgeSync.IndexPathAsync(p, username, path);
                    await BroadcastKnowledgeChanged(context.OwnerId, datasetId);
                    return Json(new
                    {
                        document = new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus },
                        note = "Документ загружен, индексация выполняется в фоне — статус через knowledge_status.",
                    });
                }
                catch (FileNotFoundException) { return Deny("Файл не найден"); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
                catch (HttpRequestException ex) { return Deny($"Dify недоступен: {ex.Message}"); }
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

    // Событие knowledge_changed в хаб — тот же канал, что у контроллеров знаний
    private Task BroadcastKnowledgeChanged(string ownerId, string? datasetId) =>
        hub.Clients.Group("user_" + ownerId)
            .SendAsync("message", new KnowledgeChangedMessage("doc_changed", datasetId));

    // --- Секция search (единый поиск по рабочему пространству) ---

    private async Task<McpToolCallResult> SearchCall(string tool, JsonObject arguments,
        McpToolCallContext context, SessionManager.WorkspaceMcpPlan plan)
    {
        if (tool != "search_unified") return Deny($"Неизвестный инструмент: {tool}");
        var query = StringArg(arguments, "query").Trim();
        if (query.Length == 0) return Json(Array.Empty<object>());
        var limit = Math.Clamp(IntArg(arguments, "limit") ?? 8, 1, 20);
        // Зона сессии режет выдачу: суженная персона не должна видеть заметки и задачи
        // за пределами своих проектов (правка приёмки волны 3.1)
        var hits = await search.SearchAsync(context.OwnerId, query, limit, plan.AllowedProjectIds);
        return Json(hits);
    }

    // --- Секции git (чтение) и git_write (запись): операции в КОРНЕ проекта — как
    // stdio-ветка (она не передавала ?sessionId=, REST RootFor брал корень проекта) ---

    private async Task<McpToolCallResult> GitCall(string tool, JsonObject arguments,
        McpToolCallContext context, Session session, SessionManager.WorkspaceMcpPlan plan,
        CancellationToken ct)
    {
        if (!TryGetProject(context, plan, arguments, out var p, out var denied))
            return Deny(denied);
        var root = p.RootPath;

        try
        {
            switch (tool)
            {
                case "git_status":
                {
                    var status = await git.StatusAsync(p.OwnerId, root, ct);
                    // Детект коммита по сдвигу HEAD — как REST Status (GitController):
                    // без него коммиты мимо продукта (Bash в чате, терминал) выпадали из
                    // атрибуции файлов чатам на этом пути (правка волны 3.1)
                    await commitAttribution.OnStatusRequestAsync(p.OwnerId, root, status.HeadSha);
                    return Json(status);
                }

                case "git_diff":
                {
                    var path = StringArg(arguments, "path");
                    var staged = arguments["staged"] is JsonValue sv
                        && sv.TryGetValue<bool>(out var sb) && sb;
                    // Ответ — { diff } (строка унифицированного diff), как REST
                    var diff = await git.DiffFileAsync(p.OwnerId, root, path, staged, ct);
                    return Json(new { diff });
                }

                case "git_log":
                {
                    var limit = IntArg(arguments, "limit") ?? 100;
                    var branch = OptionalArg(arguments, "branch");
                    return Json(await git.LogAsync(p.OwnerId, root, limit, branch, ct));
                }

                case "git_commit":
                {
                    var message = StringArg(arguments, "message").Trim();
                    if (message.Length == 0) return Deny("Пустое сообщение коммита");
                    // ADR-004 §1, «второй канал»: MCP git_commit коммитит от имени модели —
                    // трейлер CCS-Session/CCS-Task дописывает сервер по сессии-вызывателю
                    // (как AppendDossierTrailer в GitController у stdio-ветки)
                    message = AppendDossierTrailer(message, session);
                    var sha = await git.CommitAsync(p.OwnerId, root, message, amend: false, ct);
                    await BroadcastGitChanged(context.OwnerId, p.Id);
                    return Json(new { sha });
                }

                case "git_stage":
                {
                    var path = StringArg(arguments, "path");
                    await git.StageAsync(p.OwnerId, root, path, ct);
                    await BroadcastGitChanged(context.OwnerId, p.Id);
                    // Ответ — свежий git-статус (как Mutate-обёртка контроллера)
                    return Json(await git.StatusAsync(p.OwnerId, root, ct));
                }

                default:
                    return Deny($"Неизвестный инструмент: {tool}");
            }
        }
        catch (UnauthorizedAccessException) { return Deny("Недопустимый путь"); }
        catch (Git.GitCommandException ex) { return Deny(ex.Message); }
    }

    // Трейлер «Истории решений» для MCP-коммита: сессия-вызыватель + её задача.
    // Без сессии сообщение не трогаем — как у REST (там это коммит человека из UI).
    private static string AppendDossierTrailer(string message, Session? caller)
    {
        if (caller is null) return message;
        var trailer = $"CCS-Session: {caller.Id}"
            + (caller.TaskId is null ? "" : $"\nCCS-Task: {caller.TaskId}");
        return message.TrimEnd() + $"\n\n" + trailer;
    }

    // Событие git_status_changed в хаб — тот же канал, что у GitController.NotifyChanged
    private Task BroadcastGitChanged(string ownerId, string projectId) =>
        hub.Clients.Group("user_" + ownerId)
            .SendAsync("message", new GitStatusChangedMessage(projectId));

    // --- Секция knowledge_bases (менеджер баз Dify владельца) ---

    private async Task<McpToolCallResult> KbCall(string tool, JsonObject arguments,
        McpToolCallContext context)
    {
        // Username для классификации — из стора (сервисный JWT может не нести claim Name);
        // релевантность баз проверяет каталог на каждый вызов (своя/публичная — доступна,
        // чужая помеченная — нет)
        var username = users.GetById(context.OwnerId)?.Username ?? context.OwnerId;
        try
        {
            switch (tool)
            {
                case "kb_list":
                {
                    // Базы знаний владельца — уровень пользователя, projectId не участвует
                    var (configured, items) = await knowledgeCatalog.ListForUserAsync(username);
                    return Json(new { configured, items });
                }

                case "kb_get":
                {
                    var id = StringArg(arguments, "id");
                    if (id.Length == 0) return Deny("Не указан id базы знаний");
                    var detail = await knowledgeCatalog.GetDetailForUserAsync(username, id);
                    return detail is null
                        ? Deny($"База знаний {id} не найдена или недоступна.")
                        : Json(detail);
                }

                case "kb_search":
                {
                    var id = StringArg(arguments, "id");
                    if (id.Length == 0) return Deny("Не указан id базы знаний");
                    var d = await knowledgeCatalog.ResolveReadableAsync(username, id);
                    if (d is null) return Deny($"База знаний {id} не найдена или недоступна.");
                    var query = StringArg(arguments, "query").Trim();
                    if (query.Length == 0) return Json(new { items = Array.Empty<object>() });
                    var topK = Math.Clamp(IntArg(arguments, "topK") ?? 8, 1, 20);
                    // semantic → чисто по смыслу; fulltext → точные совпадения (как REST)
                    var method = string.Equals(StringArg(arguments, "method"), "fulltext",
                        StringComparison.OrdinalIgnoreCase) ? "full_text_search" : "semantic_search";
                    var chunks = await knowledge.RetrieveAsync(id, query, topK, searchMethod: method);
                    return Json(new
                    {
                        items = chunks.Select(c => new KnowledgeSearchHit(c.Score, c.Content, c.DocumentName)),
                    });
                }

                case "kb_add_document":
                {
                    var id = StringArg(arguments, "id");
                    if (id.Length == 0) return Deny("Не указан id базы знаний");
                    var d = await knowledgeCatalog.ResolveReadableAsync(username, id);
                    if (d is null) return Deny($"База знаний {id} не найдена или недоступна.");
                    var name = StringArg(arguments, "name").Trim();
                    if (name.Length == 0) return Deny("Не задано имя документа");
                    var text = StringArg(arguments, "text");
                    if (text.Length == 0) return Deny("Пустой текст документа");
                    var doc = await knowledge.IndexFileByTextAsync(id, name, text);
                    await BroadcastKnowledgeChanged(context.OwnerId, id);
                    return Json(new { id = doc.Id, name = doc.Name, indexingStatus = doc.IndexingStatus });
                }

                default:
                    return Deny($"Неизвестный инструмент: {tool}");
            }
        }
        catch (HttpRequestException ex) { return Deny($"Dify недоступен: {ex.Message}"); }
    }

    // --- Секция chats ---

    private async Task<McpToolCallResult> ChatsCall(string tool, JsonObject arguments,
        McpToolCallContext context, Session session, SessionManager.WorkspaceMcpPlan plan)
    {
        switch (tool)
        {
            case "chats_list":
            {
                IReadOnlyCollection<Session> list;
                if (OptionalArg(arguments, "projectId") is { } pid)
                {
                    if (!TryGetProject(context, plan, arguments, out var p, out var denied))
                        return Deny(denied);
                    list = sessions.GetByProject(p.Id);
                }
                else
                {
                    list = sessions.GetProjectlessChats(context.OwnerId);
                }
                var items = list.Select(s =>
                {
                    // isSelf — как stdio (s.id === SELF_SESSION_ID || undefined): ключа нет
                    // у чужих сессий
                    var item = new Dictionary<string, object?>
                    {
                        ["id"] = s.Id,
                        ["name"] = s.Name,
                        ["status"] = s.Status.ToString().ToLower(),
                        ["personaId"] = s.PersonaId,
                        ["model"] = s.Model,
                        ["updatedAt"] = s.UpdatedAt,
                    };
                    if (s.Id == session.Id) item["isSelf"] = true;
                    return item;
                }).ToList();
                return Json(items);
            }

            case "chats_history":
            {
                var sid = StringArg(arguments, "sessionId");
                var target = sessions.GetOwned(sid, context.OwnerId);
                if (target is null) return Deny($"Сессия {sid} не найдена.");
                // Зона сессии: маршрут адресуется по sessionId в обход projectId — суженная
                // зона не должна читать чаты чужих проектов владельца (как stdio-ветка
                // по ответу history)
                if (target.ProjectId is { } targetPid
                    && ProjectDenied(context, plan, targetPid) is { } zoneDenied)
                    return Deny(zoneDenied);
                var all = await sessions.GetHistoryAsync(sid);
                // Компактный срез — тот же, что отдаёт REST (SessionMessagesController.GetHistory):
                // общий маппинг, а не дубль
                var items = all.Select(SessionMessagesController.ToItem)
                    .Where(i => i is not null)
                    .ToList();
                var take = Math.Clamp(IntArg(arguments, "limit") ?? 20, 1, 200);
                return Json(new
                {
                    sessionId = target.Id,
                    name = target.Name,
                    projectId = target.ProjectId,
                    status = target.Status.ToString().ToLower(),
                    total = items.Count,
                    items = items.Skip(Math.Max(0, items.Count - take)),
                });
            }

            case "chats_create":
            {
                var projectId = OptionalArg(arguments, "projectId");
                var name = OptionalArg(arguments, "name");
                var model = OptionalArg(arguments, "model");
                var personaId = OptionalArg(arguments, "personaId");
                Session created;
                try
                {
                    if (projectId is not null)
                    {
                        if (!TryGetProject(context, plan, arguments, out var p, out var denied))
                            return Deny(denied);
                        // Персона: явная → она; иначе руководитель проекта, иначе провижн
                        // ассистента — правило «персона контекста», то же что в
                        // SessionsController.Create (stdio-ветка получала его через REST)
                        personaId ??= await ResolveNewChatPersona(context.OwnerId, p.Id, default);
                        if (personaId is null)
                            return Deny("Новый чат создаётся только с персоной: укажите personaId");
                        created = await sessions.CreateAsync(p.Id, ClaudeMode.AcceptEdits,
                            resumeSessionId: null, name: name, model: model, personaId: personaId);
                    }
                    else
                    {
                        personaId ??= (await provisioner.EnsureAsync(context.OwnerId))?.Id;
                        if (personaId is null)
                            return Deny("Новый чат создаётся только с персоной: укажите personaId");
                        created = await sessions.CreateChatAsync(context.OwnerId, ClaudeMode.Auto,
                            resumeSessionId: null, name: name, model: model, personaId: personaId);
                    }
                }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
                catch (KeyNotFoundException ex) { return Deny(ex.Message); }
                return Json(new { id = created.Id, name = created.Name, projectId = created.ProjectId });
            }

            case "chats_update":
            {
                var sid = StringArg(arguments, "sessionId");
                if (sid.Length == 0) return Deny("Не указан sessionId");
                var name = StringArg(arguments, "name").Trim();
                if (name.Length == 0) return Deny("Название чата пусто");
                var target = sessions.GetOwned(sid, context.OwnerId);
                if (target is null) return Deny($"Сессия {sid} не найдена.");
                if (target.ProjectId is { } targetPid
                    && ProjectDenied(context, plan, targetPid) is { } zoneDenied)
                    return Deny(zoneDenied);
                Session? updated;
                try
                {
                    updated = await sessions.UpdateAsync(sid, context.OwnerId, name,
                        model: null, effort: null);
                }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
                catch (KeyNotFoundException) { return Deny($"Сессия {sid} не найдена."); }
                return updated is null
                    ? Deny($"Сессия {sid} не найдена.")
                    : Json(new { id = updated.Id, name = updated.Name, projectId = updated.ProjectId });
            }

            case "chats_send":
            {
                var sid = StringArg(arguments, "sessionId");
                if (sid.Length == 0) return Deny("Не указан sessionId");
                // Запрет self-send — рекурсивный ход в собственную сессию (проверка ДО запроса)
                if (sid == session.Id)
                    return Deny("Нельзя писать в собственный чат — chats_send адресован ДРУГИМ сессиям");
                var text = StringArg(arguments, "text").Trim();
                if (text.Length == 0) return Deny("Текст сообщения пуст");
                var target = sessions.GetOwned(sid, context.OwnerId);
                if (target is null) return Deny($"Сессия {sid} не найдена.");
                // Зона: при суженной зоне целевой проект проверяем до отправки (как stdio)
                if (target.ProjectId is { } targetPid
                    && ProjectDenied(context, plan, targetPid) is { } zoneDenied)
                    return Deny(zoneDenied);
                // Анти-рекурсия — тот же гейт, что [DenyOnDelegatedTurn] на
                // SessionMessagesController.PostMessage (делегированный ход не пишет
                // в третьи чаты)
                if (DelegatedDenied(context, session, "Отправка сообщения в другой чат") is { } gateDenied)
                    return Deny(gateDenied);

                var outcome = await messaging.SendAsync(context.OwnerId, sid, text,
                    callerSessionId: session.Id, senderSessionId: session.Id,
                    agentDepthFallback: 0,
                    wait: OptionalArg(arguments, "wait"),
                    timeoutSec: IntArg(arguments, "timeoutSec"));
                return outcome switch
                {
                    SessionMessagingService.SendOutcome.NotFound => Deny($"Сессия {sid} не найдена."),
                    SessionMessagingService.SendOutcome.EmptyText => Deny("Текст сообщения пуст"),
                    SessionMessagingService.SendOutcome.TeamWakeDenied w => Deny(
                        $"Сообщение в чат-штаб недоступно: {w.Reason}. Доложи результат "
                        + "в своей задаче — координатор увидит его, когда человек разрешит продолжить."),
                    // busy/queued/queue_full — не сбой вызова, а ответ по существу: в теле
                    // status и hint, решение (ждать/не ретраить) принимает модель (как stdio)
                    SessionMessagingService.SendOutcome.Busy b => Json(new
                    {
                        status = "busy",
                        currentStatus = b.CurrentStatus.ToString().ToLower(),
                        hint = b.CurrentStatus == SessionStatus.Waiting
                            ? "сессия ждёт подтверждения человека — не вклинивайся; не ретраить чаще раза в 30 секунд и не более 2 раз"
                            : "сессия сейчас выполняет ход — попробуй позже; не ретраить чаще раза в 30 секунд и не более 2 раз",
                    }),
                    SessionMessagingService.SendOutcome.Queued qq => Json(new
                    {
                        status = "queued",
                        position = qq.Position,
                        duplicate = qq.Duplicate,
                        hint = qq.Duplicate
                            ? "такое же сообщение уже стоит в очереди — повторно не отправляй"
                            : "чат занят: сообщение доставлено в очередь и уйдёт после текущего хода. Не ретрай — ответ смотри через chats_history",
                    }),
                    SessionMessagingService.SendOutcome.QueueFull f => Json(new
                    {
                        status = "queue_full",
                        limit = f.Limit,
                        hint = $"в очереди чата уже {f.Limit} сообщений — дождись, пока он их разберёт",
                    }),
                    SessionMessagingService.SendOutcome.Completed c => Json(new
                    {
                        status = "completed",
                        reply = c.Reply,
                        durationMs = c.DurationMs,
                        costUsd = c.CostUsd,
                    }),
                    _ => Json(new
                    {
                        status = "running",
                        hint = "ход продолжается — результат позже через chats_history",
                    }),
                };
            }

            case "chats_report_up":
            {
                var text = StringArg(arguments, "text").Trim();
                if (text.Length == 0) return Deny("Пустой текст отчёта");
                // Анти-рекурсия — тот же гейт, что [DenyOnDelegatedTurn] на
                // SessionMessagesController.ReportUp
                if (DelegatedDenied(context, session, "Отчёт в вышестоящий чат") is { } gateDenied)
                    return Deny(gateDenied);
                // Адресат — родительский чат, его вычисляет сервер по текущей сессии (хвосту)
                var blocker = arguments["blocker"] is JsonValue bv
                    && bv.TryGetValue<bool>(out var bb) && bb;
                var outcome = await messaging.ReportUpAsync(context.OwnerId, session.Id, text, blocker);
                return outcome switch
                {
                    SessionMessagingService.ReportOutcome.EmptyText => Deny("Пустой текст отчёта"),
                    // 400-исходы — не сбой вызова, а ответ по существу (status/hint): модель
                    // не должна ретраить (как stdio-ветка)
                    SessionMessagingService.ReportOutcome.AlreadyReported => Json(new
                    {
                        status = "already_reported",
                        hint = "доклад о завершении этой задачи постановщик уже получил — повторять не нужно",
                    }),
                    SessionMessagingService.ReportOutcome.NoParent => Json(new
                    {
                        status = "no_parent",
                        hint = "у этого чата нет родительского — отчитываться некуда",
                    }),
                    SessionMessagingService.ReportOutcome.TooDeep => Json(new
                    {
                        status = "too_deep",
                        hint = "цепочка автоматических отчётов слишком длинная — доложи человеку в своём чате",
                    }),
                    SessionMessagingService.ReportOutcome.NotFound => Deny("Сессия не найдена."),
                    _ => Json(new { status = "delivered", hint = "отчёт лёг в ленту родительского чата" }),
                };
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

    // Персона для нового чата: руководитель проекта (вживую, сирота не считается),
    // иначе провижн личного ассистента — правило «персона контекста» из SessionsController
    private async Task<string?> ResolveNewChatPersona(string ownerId, string projectId,
        CancellationToken ct)
    {
        if (projects.GetById(projectId)?.DefaultPersonaId is { } leadId
            && personas.Get(leadId, ownerId) is { } lead)
            return lead.Id;
        return (await provisioner.EnsureAsync(ownerId, ct))?.Id;
    }

    // --- Секция destructive (безвозвратное удаление): гейт делегированного хода на КАЖДЫЙ
    // вызов — как [DenyOnDelegatedTurn] на REST-эндпоинтах ---

    private async Task<McpToolCallResult> DestructiveCall(string tool, JsonObject arguments,
        McpToolCallContext context, Session session, SessionManager.WorkspaceMcpPlan plan)
    {
        switch (tool)
        {
            case "files_delete":
            {
                if (!TryGetProject(context, plan, arguments, out var p, out var denied))
                    return Deny(denied);
                if (DelegatedDenied(context, session, "Удаление файлов") is { } gateDenied)
                    return Deny(gateDenied);
                var path = StringArg(arguments, "path");
                try { files.Delete(p.RootPath, path); }
                catch (FileNotFoundException) { return Deny($"Файл не найден: {path}"); }
                catch (UnauthorizedAccessException) { return Deny("Доступ за пределы проекта запрещён"); }
                return Text($"{path} безвозвратно удалён из проекта {p.Id}.");
            }

            case "chats_delete":
            {
                var sid = StringArg(arguments, "sessionId");
                if (sid.Length == 0) return Deny("Не указан sessionId");
                // Удаление собственной сессии оборвало бы текущий ход — запрещаем до запроса
                if (sid == session.Id)
                    return Deny("Нельзя удалить собственный чат — chats_delete адресован ДРУГИМ сессиям");
                var target = sessions.GetOwned(sid, context.OwnerId);
                if (target is null) return Deny($"Сессия {sid} не найдена.");
                if (target.ProjectId is { } targetPid
                    && ProjectDenied(context, plan, targetPid) is { } zoneDenied)
                    return Deny(zoneDenied);
                if (DelegatedDenied(context, session, "Удаление чата") is { } gateDenied)
                    return Deny(gateDenied);
                await sessions.DeleteAsync(sid);
                return Text($"Чат {sid} безвозвратно удалён вместе с историей.");
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

    // --- Секция deploy (выкатка прода из чата, ADR-010): обёртки тонкие, решения
    // принимает DeployService. Главная задача — изложить отказ так, чтобы он не выглядел
    // успехом: коды содержательны и требуют разных действий человека (порт deployCall) ---

    private async Task<McpToolCallResult> DeployCall(string tool, JsonObject arguments,
        McpToolCallContext context, Session session, CancellationToken ct)
    {
        switch (tool)
        {
            case "deploy_start":
            {
                var result = await deploy.StartAsync(
                    new DeployStartRequest(
                        OptionalArg(arguments, "ref"),
                        BoolArg(arguments, "skipFrontend"),
                        BoolArg(arguments, "skipSandbox"),
                        BoolArg(arguments, "allowDirty")),
                    context.OwnerId, session.Id, ct);
                return DeployRespond(result, "Выкатка", context);
            }

            case "deploy_rollback":
            {
                var result = await deploy.RollbackAsync(OptionalArg(arguments, "releaseId"),
                    context.OwnerId, session.Id, ct);
                return DeployRespond(result, "Откат", context);
            }

            case "deploy_status":
            {
                if (!deploy.Options.Enabled)
                    return Json(new Dictionary<string, object?>
                    {
                        ["enabled"] = false,
                        ["note"] = "Механизм выкатки на этой машине не настроен (секция Deploy выключена). "
                            + "deploy_start и deploy_rollback здесь работать не будут — это настройка администратора, а не сбой.",
                    });
                var state = deploy.Load();
                return Json(new Dictionary<string, object?>
                {
                    ["enabled"] = true,
                    ["current"] = DeployBrief(state.Current),
                    ["running"] = state.Current is { Result: null },
                    ["history"] = state.History.Take(5).Select(DeployBrief).ToList(),
                    ["releases"] = state.Releases.Take(10)
                        .Select(r => new { releaseId = r.Id, sha = r.Sha, createdAt = r.CreatedAt })
                        .ToList(),
                });
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

    // Маппинг исхода заявки: Accepted → принято (с предупреждением о перезапуске); отказы —
    // честные тексты с действием (порт deployCall stdio-ветки: 409 занято, 400 параметры/
    // грязное дерево, 503 не настроено)
    private McpToolCallResult DeployRespond(DeployStartResult result, string action,
        McpToolCallContext context)
    {
        switch (result.Status)
        {
            case DeployStartStatus.Accepted:
            {
                var active = sessions.GetAll()
                    .Count(s => s.Status is SessionStatus.Working or SessionStatus.Starting);
                return Json(new Dictionary<string, object?>
                {
                    ["accepted"] = true,
                    ["deployId"] = result.DeployId,
                    ["activeTurns"] = active,
                    ["warning"] = $"{action} принята и пойдёт своим ходом. Сервер будет перезапущен: этот чат "
                        + $"прервётся, а вместе с ним оборвутся идущие сейчас ходы ({active}). Скажи об этом пользователю.",
                    ["next"] = "Итог выкатки придёт отдельным сообщением от уже нового процесса. "
                        + "Ход выкатки виден через deploy_status, пока сервер жив.",
                });
            }
            case DeployStartStatus.AlreadyRunning:
                return Deny($"{action} НЕ запущена: {result.Error ?? "на сервере уже идёт другая выкатка"}. "
                    + "Двух одновременно не бывает — посмотри deploy_status и дождись конца текущей.");
            case DeployStartStatus.DirtyTree:
            {
                var files = result.DirtyFiles ?? [];
                return Deny($"{action} НЕ запущена: в рабочем дереве незакоммиченные изменения "
                    + $"({files.Count}): {string.Join(", ", files.Take(20))}"
                    + (files.Count > 20 ? ", …" : "")
                    + ". Покажи список пользователю и спроси, коммитить ли их. Ехать как есть — "
                    + "только по его явному согласию, повторным вызовом с allowDirty=true: тогда "
                    + "в прод уедет ровно то, что лежит в дереве.");
            }
            case DeployStartStatus.InvalidRef:
            case DeployStartStatus.NoRelease:
                return Deny($"{action} НЕ запущена: {result.Error ?? "запрос отклонён сервером"}. "
                    + "Повторять тот же вызов бессмысленно — исправь параметры или спроси пользователя.");
            case DeployStartStatus.Disabled:
            case DeployStartStatus.Misconfigured:
                return Deny($"{action} НЕ запущена: механизм выкатки на этой машине не настроен "
                    + $"({result.Error}). Это не временный сбой и не занятость — включить контур может только "
                    + "администратор в appsettings.Local.json. Сообщи это пользователю и не повторяй вызов.");
            default:
                return Deny($"{action} НЕ запущена: {result.Error ?? "внутренняя ошибка сервера"}.");
        }
    }

    // Компактная карточка выкатки (порт brief из stdio-ветки)
    private static Dictionary<string, object?>? DeployBrief(DeployRecord? rec)
    {
        if (rec is null) return null;
        var item = new Dictionary<string, object?>
        {
            ["deployId"] = rec.Id,
            ["kind"] = rec.Kind,
            ["phase"] = rec.Phase,
            ["ref"] = rec.Ref,
            ["sha"] = rec.Sha,
            ["steps"] = rec.Steps.Select(s => $"{s.Name}: {s.Status}").ToList(),
            ["result"] = rec.Result is { } r
                ? new Dictionary<string, object?>
                {
                    ["ok"] = r.Ok,
                    ["status"] = r.Status,
                    ["message"] = r.Message,
                }
                : null,
        };
        // dirty/reported — как stdio (rec.dirty === true || undefined): ключа нет, пока false
        if (rec.Dirty) item["dirty"] = true;
        if (rec.Reported) item["reported"] = true;
        return item;
    }

    // --- Ответы и аргументы ---

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name);
        return value.Length == 0 ? null : value;
    }

    private static int? IntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool BoolArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static List<string> StringsArg(JsonObject arguments, string name) =>
        arguments[name] is JsonArray arr
            ? arr.Where(t => t is JsonValue v && v.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()).ToList()
            : [];
}
