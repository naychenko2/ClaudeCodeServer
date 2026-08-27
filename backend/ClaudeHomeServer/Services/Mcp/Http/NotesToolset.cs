using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Заметки владельца (notes_*) поверх HTTP-транспорта — четвёртый переехавший с node сервер
/// (ADR-012, фаза 2 волна 2). Как и tasks, это был тонкий JSON-RPC-фасад к нашему же API;
/// здесь он повёрнут напрямую к сервисам (NotesService, NotesKnowledgeService, NotesAiService,
/// NoteTaskSyncService) — HTTP-хоп через собственный Kestrel не нужен.
///
/// Маршрут — <c>POST /mcp/notes/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЯ (эквивалент
/// env NOTES_SESSION_ID/NOTES_PROJECT_ID stdio-ветки), по ней тулсет резолвит проект чата
/// (источник по умолчанию для создания) и персону. Сессия обязана принадлежать владельцу
/// токена — чужая это отказ.
///
/// ИНВАРИАНТ состава: ядро заметок (7 инструментов) — всегда; модуль комментариев и редких
/// операций (12 инструментов) — по живой привязке персоны notes-annotations (решение ПО
/// ПЕРСОНЕ, не по ходу — формула та же, что у SessionManager.BuildNotesContext). Право на
/// notes-сервер вообще (привязка tool:notes) проверяется на каждый вызов. Сторож парности —
/// NotesToolsetParityTests (index.js заморожен).
/// </summary>
public sealed class NotesToolset(
    NotesService notes,
    NotesKnowledgeService kb,
    NotesAiService ai,
    NoteTaskSyncService noteTasks,
    PersonaManager personas,
    PersonaBindingsService bindings,
    SessionManager sessions,
    IHubContext<SessionHub> hub) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/notes/{sessionId}
    public const string ServerName = "notes";

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out var annotations, out _)
            ? annotations ? [.. CoreTools, .. AnnotationTools] : CoreTools
            : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var session, out var persona, out var annotations, out var routeError))
            return Deny(routeError);

        // Defense-in-depth, как у stdio: выключенный модуль не отрабатывает и при ошибке
        // экспозиции (модель не должна видеть разницу между «нет в списке» и «отказ»)
        if (!annotations && AnnotationToolNames.Contains(tool))
            return Deny("Инструмент недоступен этой персоне (модуль комментариев и редких операций "
                + "заметок выключен). Попроси пользователя включить его привязкой tool:notes-annotations.");

        var projectId = session.ProjectId;
        var defaultSource = projectId ?? "personal";

        switch (tool)
        {
            case "notes_list":
                return Json(notes.GetSummaries(context.OwnerId, OptionalArg(arguments, "source"), null)
                    .Select(Brief).ToList());

            case "notes_search":
            {
                // status-фильтр комментариев уезжает в query-префикс (как у stdio)
                var q = StringArg(arguments, "query");
                var status = StringArg(arguments, "status");
                var query = status.Length > 0 ? $"status:{status} {q}".Trim() : q;
                return Json(notes.GetSummaries(context.OwnerId, null, query.Length > 0 ? query : null)
                    .Select(n => n.Annotation is null ? Brief(n) : new
                    {
                        n.Id, n.Title, n.Source, n.SourceLabel, n.Tags, n.UpdatedAt,
                        annotation = n.Annotation,
                    })
                    .ToList());
            }

            case "notes_read":
                return notes.GetDetail(context.OwnerId, StringArg(arguments, "id")) is { } detail
                    ? Json(detail)
                    : Deny($"Заметка {StringArg(arguments, "id")} не найдена.");

            case "notes_suggest_title":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    var title = await ai.SuggestTitleAsync(context.OwnerId, id, ct);
                    return Json(new { title });
                }
                catch (KeyNotFoundException) { return Deny($"Заметка {id} не найдена."); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
            }

            case "notes_create":
            {
                var title = StringArg(arguments, "title");
                if (string.IsNullOrWhiteSpace(title)) return Deny("Не задано название заметки");
                var req = new CreateNoteRequest(
                    Title: title,
                    Content: OptionalArg(arguments, "content"),
                    Source: OptionalArg(arguments, "source") ?? defaultSource,
                    ExpiresAfterMinutes: (int?)(arguments["expiresAfterMinutes"] is JsonValue m
                        && m.TryGetValue<double>(out var mins) ? (int)mins : null),
                    // Заметка из чата помнит свой источник — как NOTES_SESSION_ID у stdio
                    SourceSessionId: session.Id,
                    File: OptionalArg(arguments, "file"));
                try
                {
                    var created = notes.Create(context.OwnerId, req);
                    await BroadcastAsync(context.OwnerId, "created", created.Id);
                    return Json(created);
                }
                catch (KeyNotFoundException) { return Deny("Источник заметок не найден (проект удалён?)."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к источнику заметок."); }
            }

            case "notes_update":
            {
                var id = StringArg(arguments, "id");
                var req = new UpdateNoteRequest(
                    Title: OptionalArg(arguments, "title"),
                    // Пустая строка = очистить содержимое, null = не менять: OptionalArg
                    // глотал очистку молча (блокер приёмки волны 2.1, паритет со stdio)
                    Content: arguments.ContainsKey("content") ? StringArg(arguments, "content") : null);
                try
                {
                    var updated = notes.Update(context.OwnerId, id, req);
                    if (updated is null) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "updated", id);
                    return Json(updated);
                }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (ArgumentException) { return Deny("Некорректный id заметки."); }
            }

            case "notes_backlinks":
                return Json(notes.GetBacklinks(context.OwnerId, StringArg(arguments, "id")));

            case "notes_graph":
                return Json(notes.GetGraph(context.OwnerId, includeAnnotations: false));

            case "notes_delete":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    if (!notes.Delete(context.OwnerId, id)) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "deleted", id);
                    return Text($"Заметка {id} удалена.");
                }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (ArgumentException) { return Deny("Некорректный id заметки."); }
            }

            case "notes_annotate":
            {
                // Офсеты — хинт: сервис сам находит единственное дословное вхождение
                // (verify-before-write); не нашёл/неуникально — честная ошибка без порчи файла
                var anchor = StringArg(arguments, "anchorText");
                var req = new AnnotateRequest(
                    Doc: new AnnotateDocRef(
                        OptionalArg(arguments, "scope") ?? projectId ?? "personal",
                        StringArg(arguments, "path")),
                    Selection: new AnnotateSelection(0, anchor.Length, anchor),
                    Comment: StringArg(arguments, "comment"),
                    Tags: TagsArg(arguments));
                try
                {
                    var created = notes.Annotate(context.OwnerId, req);
                    await BroadcastAsync(context.OwnerId, "created", created.Id);
                    return Json(created);
                }
                catch (AnnotationConflictException ex) { return Deny(ex.Message); }
                catch (ArgumentException ex) { return Deny(ex.Message); }
                catch (KeyNotFoundException) { return Deny("Документ не найден."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к документу."); }
            }

            case "notes_annotations":
            {
                var scope = OptionalArg(arguments, "scope") ?? projectId ?? "personal";
                var path = StringArg(arguments, "path");
                if (path.Length == 0) return Deny("Не указан путь документа.");
                try { return Json(notes.GetDocAnnotations(context.OwnerId, scope, path)); }
                catch (KeyNotFoundException) { return Deny("Область документа не найдена."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к документу."); }
            }

            case "notes_reply":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    var created = notes.Reply(context.OwnerId, id,
                        new ReplyRequest(StringArg(arguments, "comment"), TagsArg(arguments)));
                    await BroadcastAsync(context.OwnerId, "created", created.Id);
                    return Json(created);
                }
                catch (ArgumentException ex) { return Deny(ex.Message); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
                catch (KeyNotFoundException) { return Deny($"Комментарий {id} не найден."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
            }

            case "notes_thread":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    return Json(new
                    {
                        root = notes.GetDetail(context.OwnerId, id),
                        replies = notes.GetReplies(context.OwnerId, id),
                    });
                }
                catch (KeyNotFoundException) { return Deny($"Комментарий {id} не найден."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
            }

            case "notes_set_status":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    var updated = notes.SetAnnotationStatus(context.OwnerId, id, StringArg(arguments, "status"));
                    if (updated is null) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "updated", id);
                    return Json(updated);
                }
                catch (ArgumentException ex) { return Deny(ex.Message); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
            }

            case "notes_move":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    var moved = notes.Move(context.OwnerId, id,
                        arguments.ContainsKey("folder") ? StringArg(arguments, "folder") : null,
                        OptionalArg(arguments, "targetSource"));
                    if (moved is null) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "updated", moved.Id);
                    return Json(moved);
                }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (ArgumentException) { return Deny("Некорректный id заметки."); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
            }

            case "notes_daily":
            {
                var date = notes.GetOrCreateDaily(context.OwnerId, OptionalArg(arguments, "date"));
                await BroadcastAsync(context.OwnerId, "updated", date.Id);
                // Дописывание не поддержано эндпоинтом — делаем сами (как stdio): читаем
                // текущий текст и PUT-им склейку
                var append = StringArg(arguments, "content");
                if (append.Length > 0)
                {
                    var merged = date.Content.Length > 0
                        ? date.Content.TrimEnd() + "\n\n" + append
                        : append;
                    var updated = notes.Update(context.OwnerId, date.Id, new UpdateNoteRequest(Content: merged));
                    if (updated is not null) return Json(updated);
                }
                return Json(date);
            }

            case "notes_resolve":
            {
                var name = StringArg(arguments, "name");
                if (name.Length == 0) return Deny("Не задано имя");
                return notes.ResolveByName(context.OwnerId, name, OptionalArg(arguments, "anchor")) is { } r
                    ? Json(new { note = r.Note, fragment = r.Fragment })
                    : Deny($"Заметка «{name}» не найдена.");
            }

            case "notes_promote_task":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    // Строку можно задать текстом чекбокса — резолвим по списку задач заметки
                    int? line = arguments["line"] is JsonValue lv && lv.TryGetValue<int>(out var li) ? li : null;
                    if (line is null)
                    {
                        var text = StringArg(arguments, "text");
                        if (text.Length == 0) return Deny("Укажи line (номер строки) или text (текст чекбокса)");
                        var rows = noteTasks.ListForNote(context.OwnerId, id);
                        var needle = text.Trim();
                        var hits = rows.Where(r => r.Text == needle).ToList();
                        var matches = hits.Count > 0 ? hits : rows.Where(r => r.Text.Contains(needle)).ToList();
                        if (matches.Count == 0) return Deny($"Чекбокс с текстом \"{needle}\" не найден в заметке");
                        if (matches.Count > 1)
                            return Deny($"Найдено несколько чекбоксов \"{needle}\" — уточни line (строки: "
                                + string.Join(", ", matches.Select(m => m.Line)) + ")");
                        line = matches[0].Line;
                    }
                    return Json(await noteTasks.PromoteAsync(context.OwnerId, id, line.Value));
                }
                catch (KeyNotFoundException) { return Deny($"Заметка {id} не найдена."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
            }

            case "notes_semantic_search":
            {
                var query = StringArg(arguments, "query");
                if (!kb.Available)
                    return Text("Семантический поиск не настроен (нет Dify) — используй notes_search.");
                var topK = 8;
                if (arguments["topK"] is JsonValue tv && tv.TryGetValue<int>(out var k)) topK = k;
                try
                {
                    var results = await kb.SearchAsync(context.OwnerId, query, Math.Clamp(topK, 1, 20));
                    return Json(results);
                }
                catch (HttpRequestException ex) { return Deny($"Dify недоступен: {ex.Message}"); }
            }

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // --- Маршрут: /mcp/notes/{sessionId} ---

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

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
    /// Резолв хвоста в сессию ВЛАДЕЛЬЦА токена + живые права: привязка tool:notes (право на
    /// сервер вообще) и notes-annotations (модуль) — формулы те же, что у
    /// SessionManager.BuildNotesContext, но по ЖИВОЙ персоне сессии и на каждый вызов.
    /// </summary>
    private bool TryResolve(McpToolCallContext context, out Session session,
        out Persona? persona, out bool annotations,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        persona = null;
        annotations = false;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера заметок — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к заметкам закрыт.";
            return false;
        }
        session = owned;
        persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        if (!bindings.EffectiveToolEnabled(context.OwnerId, persona, "notes"))
        {
            error = "Сервер заметок недоступен этой персоне (возможность notes выключена). "
                + "Попроси пользователя включить её.";
            return false;
        }
        annotations = bindings.SectionEnabled(context.OwnerId, persona, "notes-annotations");
        error = null;
        return true;
    }

    // Любая мутация — отложенная синхронизация семантического индекса + событие ленты
    // (как Broadcast контроллера: панель «Заметки» обновляется и при MCP-записи)
    private async Task BroadcastAsync(string ownerId, string action, string? noteId)
    {
        kb.QueueSync(ownerId);
        await hub.Clients.Group("user_" + ownerId)
            .SendAsync("message", new NotesChangedMessage(action, noteId));
    }

    // Компактное представление заметки для списков — как brief у stdio-ветки
    private static object Brief(NoteSummary n) => new
    {
        n.Id, n.Title, n.Source, n.SourceLabel, n.Tags, n.UpdatedAt,
    };

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

    private static List<string>? TagsArg(JsonObject arguments) =>
        arguments["tags"] is JsonArray arr && arr.Count > 0
            ? arr.Where(t => t is JsonValue v && v.TryGetValue<string>(out _))
                .Select(t => t!.GetValue<string>()).ToList()
            : null;

    // --- Схемы инструментов: копия mcp/notes-server/index.js (источник контракта — здесь,
    // index.js заморожен; сторож парности — NotesToolsetParityTests). internal для сторожа:

    internal static readonly IReadOnlyList<McpToolSchema> CoreTools =
    [
        Tool("notes_list",
            "Список заметок пользователя по всем источникам (личный vault + notes/ его проектов). Можно сузить фильтром source.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Фильтр по источнику: \"personal\" или id проекта",
                    },
                },
            }),
        Tool("notes_search",
            "Поиск заметок по заголовку, тексту и тегам — по всем источникам пользователя. Поддерживает операторы в query (tag:идея source:Личный status:open) и отдельный фильтр status для комментариев к документам.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Строка поиска (можно с операторами tag:/source:/status:)",
                    },
                    ["status"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "open", "resolved", "orphaned" },
                        ["description"] = "Только комментарии к документам с этим статусом (open — необработанные)",
                    },
                },
            }),
        Tool("notes_read",
            "Прочитать заметку целиком по id: markdown-содержимое, теги, исходящие связи [[...]] и обратные ссылки.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки" },
                },
            }),
        Tool("notes_create",
            "Создать заметку (.md). В тексте связывай с другими заметками через [[Заголовок]]. По умолчанию заметки создаются в notes/ текущего проекта (вне проекта — в личный vault); source=\"personal\" — в личный vault.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "title" },
                ["properties"] = new JsonObject
                {
                    ["title"] = new JsonObject { ["type"] = "string", ["description"] = "Заголовок (= имя файла)" },
                    ["content"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Текст заметки (markdown, можно с [[wikilinks]] и frontmatter)",
                    },
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Куда создать: \"personal\" или id проекта. По умолчанию — контекст сессии",
                    },
                    ["expiresAfterMinutes"] = new JsonObject
                    {
                        ["type"] = "number",
                        ["description"] = "Время жизни в минутах. Не указывать или null — бессрочно. Пример: 1440 = сутки.",
                    },
                    ["file"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Привязать заметку к файлу проекта: путь от корня проекта-источника через /. Только для проектных заметок (при source personal игнорируется).",
                    },
                },
            }),
        Tool("notes_update",
            "Обновить заметку: заменить содержимое и/или переименовать (смена title переименует файл). Передавай только изменяемые поля.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки" },
                    ["title"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "Новый заголовок (переименует файл)",
                    },
                    ["content"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Новое содержимое (markdown), заменяет целиком",
                    },
                },
            }),
        Tool("notes_move",
            "Переместить заметку в другую папку и/или другой источник. id заметки при этом меняется (путь входит в id) — используй возвращённый id дальше. Входящие [[wikilinks]] на неё сервер чинит автоматически. Переименование (смена заголовка) делается отдельно через notes_update.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки" },
                    ["folder"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Целевая папка внутри источника (\"Идеи/Черновики\"); пусто или отсутствует — корень источника",
                    },
                    ["targetSource"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Перенести в другой источник: \"personal\" или id проекта. По умолчанию — текущий источник заметки",
                    },
                },
            }),
        Tool("notes_semantic_search",
            "Семантический поиск по заметкам (по смыслу, не по подстроке): находит близкие по содержанию заметки со score и сниппетом. Используй, когда точный текст неизвестен.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "query" },
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject { ["type"] = "string", ["description"] = "Смысловой запрос" },
                    ["topK"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 20,
                        ["description"] = "Сколько результатов (по умолчанию 8)",
                    },
                },
            }),
    ];

    internal static readonly IReadOnlyList<McpToolSchema> AnnotationTools =
    [
        Tool("notes_suggest_title",
            "Предложить короткий заголовок заметки по её содержимому (напр. для «Без названия»). "
            + "Возвращает {title}. Ничего не сохраняет. Бесплатная локальная модель, если настроена, иначе Claude.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки" },
                },
            }),
        Tool("notes_backlinks",
            "Обратные ссылки заметки: какие заметки ссылаются на неё через [[...]], с контекстом.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки" },
                },
            }),
        Tool("notes_graph",
            "Граф связей всех заметок пользователя: узлы (заметки + «призрачные» несозданные) и рёбра.",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }),
        Tool("notes_delete",
            "Удалить заметку (файл) безвозвратно.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки" },
                },
            }),
        Tool("notes_annotate",
            "Оставить комментарий к месту в markdown-документе (создаёт заметку-комментарий со статусом open, привязанную к блоку). anchorText — ДОСЛОВНЫЙ фрагмент текста документа (скопируй точно из прочитанного файла): сервер сверяет его посимвольно и откажет, если текст не найден или неуникален. Документ — любой .md проекта (docs/, README…) или личного vault.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "path", "anchorText", "comment" },
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Путь документа: для проекта — от корня проекта (docs/architecture.md), для личного vault — внутри vault",
                    },
                    ["scope"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Область документа: id проекта или \"personal\". По умолчанию — контекст сессии",
                    },
                    ["anchorText"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Дословный фрагмент документа, к которому привязать комментарий (минимум несколько слов, без пересказа!)",
                    },
                    ["comment"] = new JsonObject { ["type"] = "string", ["description"] = "Текст комментария" },
                    ["tags"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Теги (без #)",
                    },
                },
            }),
        Tool("notes_annotations",
            "Комментарии к документу с резолвом привязки: статус (open/resolved), состояние якоря (exact/changed/orphan), цитата и позиция блока.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "path" },
                ["properties"] = new JsonObject
                {
                    ["path"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "Путь документа внутри области",
                    },
                    ["scope"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "id проекта или \"personal\". По умолчанию — контекст сессии",
                    },
                },
            }),
        Tool("notes_reply",
            "Ответить в треде комментария к документу (реплика — отдельная заметка, привязанная к корневому комментарию; тред плоский, отвечать можно только на корневой).",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id", "comment" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "ID корневого комментария (из notes_annotations)",
                    },
                    ["comment"] = new JsonObject { ["type"] = "string", ["description"] = "Текст ответа" },
                    ["tags"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string" },
                        ["description"] = "Теги (без #)",
                    },
                },
            }),
        Tool("notes_thread",
            "Тред комментария: корневая заметка-комментарий целиком + все ответы по времени.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID корневого комментария" },
                },
            }),
        Tool("notes_set_status",
            "Сменить статус комментария к документу: resolved — обработан («решён»), open — снова открыт.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id", "status" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки-комментария" },
                    ["status"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = new JsonArray { "open", "resolved" },
                    },
                },
            }),
        Tool("notes_daily",
            "Открыть или создать дневниковую заметку (Journal/YYYY-MM-DD.md в личном vault). Если передан content — дописать его в конец заметки. Удобно для быстрых записей «в дневник за сегодня».",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["date"] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "Дата в формате YYYY-MM-DD. По умолчанию — сегодня.",
                    },
                    ["content"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Текст (markdown) для дописывания в конец дневниковой заметки. Пусто — просто открыть/создать.",
                    },
                },
            }),
        Tool("notes_resolve",
            "Резолв вики-ссылки [[Имя]] в конкретную заметку (с учётом коллизий вида [[Проект/Имя]]). При заданном anchor вернёт и фрагмент заметки по якорю \"#Заголовок\" или \"#^блок\". Отвечает на вопрос «на какую именно заметку указывает эта ссылка».",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "name" },
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Имя из вики-ссылки, как в [[…]] (можно \"Проект/Имя\" для устранения коллизии)",
                    },
                    ["anchor"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Якорь внутри заметки: заголовок (\"#Раздел\") или блок (\"#^abc123\") — вернёт соответствующий фрагмент",
                    },
                },
            }),
        Tool("notes_promote_task",
            "Превратить чекбокс-пункт заметки (- [ ] …) в настоящую задачу (появится в календаре, работают напоминания). Чекбокс задаётся номером строки line (0-базовый индекс строки в markdown-содержимом из notes_read) ЛИБО его текстом text (сервер сам найдёт строку). Повторный промоут той же строки вернёт уже существующую задачу.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "id" },
                ["properties"] = new JsonObject
                {
                    ["id"] = new JsonObject { ["type"] = "string", ["description"] = "ID заметки с чекбоксом" },
                    ["line"] = new JsonObject
                    {
                        ["type"] = "integer", ["minimum"] = 0,
                        ["description"] = "0-базовый номер строки чекбокса в содержимом заметки (notes_read)",
                    },
                    ["text"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Текст чекбокса (без \"- [ ]\") — альтернатива line: строка найдётся по совпадению текста",
                    },
                },
            }),
    ];

    // Имена модульных инструментов — defense-in-depth на вызове (см. CallAsync)
    internal static readonly IReadOnlySet<string> AnnotationToolNames =
        AnnotationTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);
}
