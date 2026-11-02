using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using ClaudeHomeServer.Services.Spheres;

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
/// ИНВАРИАНТ состава: ядро заметок (7 инструментов) — всегда; модуль вики (4 инструмента,
/// S6c) — всегда при включённой вертикали: не заведённая вика, пустой список или выключенный
/// флаг дают ОТКАЗ НА ВЫЗОВЕ, а не исчезновение инструмента (тот же порядок, что у выключенного
/// флага image-editor, mcp-servers.md «Инвариант: состав инструментов не зависит от хода»);
/// модуль комментариев и редких операций (12 инструментов) — по живой привязке персоны
/// notes-annotations (решение ПО ПЕРСОНЕ, не по ходу — формула та же, что у
/// SessionManager.BuildNotesContext). Право на notes-сервер вообще (привязка tool:notes)
/// проверяется на каждый вызов. Сторож парности — NotesToolsetParityTests (index.js заморожен;
/// в stdio-ветке отката инструментов вики нет — поэтому WikiTools отдельным списком).
/// </summary>
public sealed class NotesToolset(
    IPersonaResolver personas,
    IMcpPersonaBindings bindings,
    IMcpSessionAccessor sessions,
    ISessionBroadcaster broadcaster,
    NotesService? notes = null,
    // Все четыре — синглтоны одной отключаемой вертикали (NotesSubsystem.Register),
    // гейтятся ОДНИМ рубильником Subsystems:Notes:Enabled: либо все null, либо все не null.
    // Раздельная опциональность нужна только затем, чтобы DI смог собрать тулсет при
    // выключенной подсистеме — деградация ниже завязана на единственную проверку notes.
    NotesKnowledgeService? kb = null,
    NotesAiService? ai = null,
    NoteTaskSyncService? noteTasks = null,
    // Горизонталь вики (S6c): те же синглтоны той же вертикали (NotesSubsystem.Register:
    // WikiIngestService, WikiSourceCapture, WikiChatIngestService) и общие швы видимости
    // (ISphereDirectory/IProjectManager — их читает только WikiVisibility.SectionsFor).
    // Опциональность — та же, что у заметок: тестовая обвязка без вики обязана собираться.
    IConfiguration? config = null,
    WikiIngestService? wikiIngest = null,
    WikiSourceCapture? wikiCapture = null,
    WikiChatIngestService? wikiChat = null,
    ISessionDirectory? sessionsDir = null,
    ISphereDirectory? spheres = null,
    IProjectManager? projects = null,
    // Шов флага llm-wiki — та же опциональная зависимость, что у WikiLintService: нет шва — выключено.
    IFeatureFlagGate? flags = null) : IMcpParameterizedToolset
{
    // Корень data — для признака заведённой вики (WikiPaths) и корня чтения её разделов.
    // Та же формула, что у всех сервисов вертикали (DataPath указывает на projects.json).
    private readonly string? _dataDir = config is null
        ? null
        : Path.GetDirectoryName(Path.GetFullPath(config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json")))!;

    // Имя сервера = первый сегмент маршрута POST /mcp/notes/{sessionId}
    public const string ServerName = McpEndpoints.NotesName;

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        notes is null || kb is null || ai is null || noteTasks is null
            ? []
            : TryResolve(context, out _, out _, out var annotations, out _)
                // Состав не читает состояние вики (ни _schema.md, ни числа пакетов, ни флага):
                // ядро + WikiTools — всегда, модуль комментариев — по живой привязке персоны.
                ? annotations ? [.. CoreTools, .. WikiTools, .. AnnotationTools]
                : [.. CoreTools, .. WikiTools]
                : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Единый гейт: все четыре co-регистрируются одним рубильником (см. шапку класса) —
        // при выключенной подсистеме notes==null гарантирует kb/ai/noteTasks==null и наоборот,
        // но компилятору это неизвестно, поэтому narrowing делаем локальными переменными.
        if (notes is not { } notesSvc || kb is not { } kbSvc
            || ai is not { } aiSvc || noteTasks is not { } noteTasksSvc)
            return Deny("Подсистема Notes отключена");
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
            {
                // Чтение источника wiki — только в наборе хода (ADR-036, решение 2); обычные
                // заметки не сужаются (решение 6): «посмотри мою заметку X» из чата проекта —
                // живой сценарий.
                var visible = VisibleSet(context.OwnerId, session, persona);
                return Json(notesSvc.GetSummaries(context.OwnerId, OptionalArg(arguments, "source"), null)
                    .Where(n => WikiReadAllowed(n.Source, n.Path, visible))
                    .Select(n => Brief(n, WikiDisplayPath(context.OwnerId, n)))
                    .ToList());
            }

            case "notes_search":
            {
                // status-фильтр комментариев уезжает в query-префикс (как у stdio)
                var q = StringArg(arguments, "query");
                var status = StringArg(arguments, "status");
                var query = status.Length > 0 ? $"status:{status} {q}".Trim() : q;
                var visible = VisibleSet(context.OwnerId, session, persona);
                return Json(notesSvc.GetSummaries(context.OwnerId, null, query.Length > 0 ? query : null)
                    .Where(n => WikiReadAllowed(n.Source, n.Path, visible))
                    .Select(n => n.Annotation is null
                        ? Brief(n, WikiDisplayPath(context.OwnerId, n))
                        : new
                        {
                            n.Id, n.Title, n.Source, n.SourceLabel, n.Tags, n.UpdatedAt,
                            annotation = n.Annotation,
                        })
                    .ToList());
            }

            case "notes_read":
            {
                var id = StringArg(arguments, "id");
                if (notesSvc.GetDetail(context.OwnerId, id) is not { } detail)
                    return Deny($"Заметка {id} не найдена.");
                var visible = VisibleSet(context.OwnerId, session, persona);
                if (detail.Source == NotesService.WikiKey)
                {
                    // Страница раздела вне набора хода — не отличима от несуществующей
                    // (ADR-036, решение 2): отказ не называет того, чего нет.
                    if (WikiSections.Parse(detail.Path) is not { } key || !visible.Contains(key))
                        return Deny($"Заметка {id} не найдена.");
                    detail = RedactWikiDetail(notesSvc, context.OwnerId, detail, visible);
                }
                else
                {
                    // Ревью S6g-2, M3: сама обычная заметка не сужается (решение 6), но её Links/Backlinks —
                    // системная проекция страниц-целей: targetId/targetTitle чужого раздела отдавались
                    // как есть, а по targetId доставалась вся скрытая страница (M1/M2). Ссылки на страницы
                    // вне набора заменяются [скрыто] тем же HiddenWikiRef; текст самой заметки — текст
                    // пользователя, его не трогаем.
                    detail = RedactHiddenWikiLinks(detail, IndexByIds(notesSvc, context.OwnerId), visible);
                }
                return Json(detail);
            }

            case "notes_suggest_title":
            {
                var id = StringArg(arguments, "id");
                // Ревью S6g-2, M3: SuggestTitleAsync отправляет Truncate(Content, 2000) в промпт модели —
                // текст страницы вики вне набора уходить туда не должен: отказ ДО вызова сервиса,
                // неотличимый от несуществующей (ADR-036, решение 2). Признак — по индексу суммариев,
                // содержимое страницы ради отказа не читается.
                if (HiddenWikiRef(id, IndexByIds(notesSvc, context.OwnerId),
                        VisibleSet(context.OwnerId, session, persona)))
                    return Deny($"Заметка {id} не найдена.");
                try
                {
                    var title = await aiSvc.SuggestTitleAsync(context.OwnerId, id, ct);
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
                    var created = notesSvc.Create(context.OwnerId, req);
                    await BroadcastAsync(context.OwnerId, "created", created.Id);
                    return Json(created);
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
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
                    var updated = notesSvc.Update(context.OwnerId, id, req);
                    if (updated is null) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "updated", id);
                    return Json(updated);
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (ArgumentException) { return Deny("Некорректный id заметки."); }
            }

            case "notes_backlinks":
            {
                var id = StringArg(arguments, "id");
                var visible = VisibleSet(context.OwnerId, session, persona);
                var index = IndexByIds(notesSvc, context.OwnerId);
                // Чтение страницы раздела вне набора — тот же отказ, что у notes_read: список
                // обратных ссылок — тоже чтение этой страницы (ADR-036, решение 2).
                if (index.TryGetValue(id, out var self) && self.Source == NotesService.WikiKey
                    && (WikiSections.Parse(self.Path) is not { } selfKey || !visible.Contains(selfKey)))
                    return Deny($"Заметка {id} не найдена.");
                // Обратная ссылка со страницы вне набора остаётся фактом связи, но без имени
                // и текста: заголовок чужого раздела — тоже текст (решение 2).
                return Json(notesSvc.GetBacklinks(context.OwnerId, id)
                    .Select(b => HiddenWikiRef(b.SourceId, index, visible)
                        ? new NoteBacklinkDto("", HiddenMark, "", "", "")
                        : b)
                    .ToList());
            }

            case "notes_graph":
            {
                var graph = notesSvc.GetGraph(context.OwnerId, includeAnnotations: false);
                var visible = VisibleSet(context.OwnerId, session, persona);
                var index = IndexByIds(notesSvc, context.OwnerId);
                // Узлы страниц вне набора и их рёбра выпадают; обычные заметки в графе не
                // сужаются (решение 6), «призрачные» узлы не принадлежат разделам.
                var hidden = graph.Nodes
                    .Where(n => HiddenWikiRef(n.Id, index, visible))
                    .Select(n => n.Id)
                    .ToHashSet(StringComparer.Ordinal);
                return Json(new NoteGraph(
                    graph.Nodes.Where(n => !hidden.Contains(n.Id)).ToList(),
                    graph.Edges.Where(e => !hidden.Contains(e.Source) && !hidden.Contains(e.Target)).ToList()));
            }

            case "notes_delete":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    if (!notesSvc.Delete(context.OwnerId, id)) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "deleted", id);
                    return Text($"Заметка {id} удалена.");
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
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
                    var created = notesSvc.Annotate(context.OwnerId, req);
                    await BroadcastAsync(context.OwnerId, "created", created.Id);
                    return Json(created);
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
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
                try { return Json(notesSvc.GetDocAnnotations(context.OwnerId, scope, path)); }
                catch (KeyNotFoundException) { return Deny("Область документа не найдена."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к документу."); }
            }

            case "notes_reply":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    var created = notesSvc.Reply(context.OwnerId, id,
                        new ReplyRequest(StringArg(arguments, "comment"), TagsArg(arguments)));
                    await BroadcastAsync(context.OwnerId, "created", created.Id);
                    return Json(created);
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
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
                    // Ревью S6g-2, M2: корень треда — та же проверка, что у notes_read: страница вики
                    // вне набора — отказ тем же текстом, что и отсутствующая (решение 2). replies —
                    // комментарии, их правило про страниц не касается (решение 6).
                    var root = notesSvc.GetDetail(context.OwnerId, id);
                    if (root is { } rootDetail)
                    {
                        var visible = VisibleSet(context.OwnerId, session, persona);
                        if (rootDetail.Source == NotesService.WikiKey)
                        {
                            if (WikiSections.Parse(rootDetail.Path) is not { } key || !visible.Contains(key))
                                return Deny($"Комментарий {id} не найден.");
                            root = RedactWikiDetail(notesSvc, context.OwnerId, rootDetail, visible);
                        }
                        else
                        {
                            // Ревью S6g-2, доработка 2, M3: обычный корень возвращался как есть —
                            // его Links/Backlinks содержали настоящие targetId/targetTitle скрытых
                            // страниц (та же утечка, что у notes_read:160-168: по targetId достижимы
                            // notes_promote_task и memory_from_note). Ссылки — тот же
                            // RedactHiddenWikiLinks, что в notes_read и notes_resolve; текст самой
                            // заметки — текст пользователя, его не трогаем (решение 6).
                            root = RedactHiddenWikiLinks(rootDetail, IndexByIds(notesSvc, context.OwnerId), visible);
                        }
                    }
                    return Json(new
                    {
                        root,
                        replies = notesSvc.GetReplies(context.OwnerId, id),
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
                    var updated = notesSvc.SetAnnotationStatus(context.OwnerId, id, StringArg(arguments, "status"));
                    if (updated is null) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "updated", id);
                    return Json(updated);
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
                catch (ArgumentException ex) { return Deny(ex.Message); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
            }

            case "notes_move":
            {
                var id = StringArg(arguments, "id");
                try
                {
                    var moved = notesSvc.Move(context.OwnerId, id,
                        arguments.ContainsKey("folder") ? StringArg(arguments, "folder") : null,
                        OptionalArg(arguments, "targetSource"));
                    if (moved is null) return Deny($"Заметка {id} не найдена.");
                    await BroadcastAsync(context.OwnerId, "updated", moved.Id);
                    return Json(moved);
                }
                catch (WikiReadOnlyException ex) { return WikiRefusal(ex); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (ArgumentException) { return Deny("Некорректный id заметки."); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
            }

            case "notes_daily":
            {
                var date = notesSvc.GetOrCreateDaily(context.OwnerId, OptionalArg(arguments, "date"));
                await BroadcastAsync(context.OwnerId, "updated", date.Id);
                // Текст дневника — обычная заметка, он не сужается (решение 6); но его структурные
                // ссылки на страницы — системная проекция целей: тот же RedactHiddenWikiLinks, что в
                // notes_read/notes_resolve (M3; W3-7): по targetId скрытой страницы доставалась вся
                // её страница, а targetTitle называл заголовок невидимого раздела.
                var visible = VisibleSet(context.OwnerId, session, persona);
                var index = IndexByIds(notesSvc, context.OwnerId);
                // Дописывание не поддержано эндпоинтом — делаем сами (как stdio): читаем
                // текущий текст и PUT-им склейку
                var append = StringArg(arguments, "content");
                if (append.Length > 0)
                {
                    var merged = date.Content.Length > 0
                        ? date.Content.TrimEnd() + "\n\n" + append
                        : append;
                    var updated = notesSvc.Update(context.OwnerId, date.Id, new UpdateNoteRequest(Content: merged));
                    if (updated is not null) return Json(RedactHiddenWikiLinks(updated, index, visible));
                }
                return Json(RedactHiddenWikiLinks(date, index, visible));
            }

            case "notes_resolve":
            {
                var name = StringArg(arguments, "name");
                if (name.Length == 0) return Deny("Не задано имя");
                if (notesSvc.ResolveByName(context.OwnerId, name, OptionalArg(arguments, "anchor")) is not { } r)
                    return Deny($"Заметка «{name}» не найдена.");
                // Ревью S6g-2, M1: пул резолва глобален (NotesService.Resolve) — страница вне набора
                // достижима и по префиксу раздела, и по голому имени. Для источника wiki — та же
                // проверка visible, что у notes_read: раздел вне набора неотличим от несуществующего.
                var visible = VisibleSet(context.OwnerId, session, persona);
                if (r.Note.Source == NotesService.WikiKey)
                {
                    if (WikiSections.Parse(r.Note.Path) is not { } key || !visible.Contains(key))
                        return Deny($"Заметка «{name}» не найдена.");
                    // Видимая страница отдаётся как в notes_read (RedactWikiDetail); фрагмент — часть
                    // текста страницы, тот же RedactLinks.
                    return Json(new
                    {
                        note = RedactWikiDetail(notesSvc, context.OwnerId, r.Note, visible),
                        fragment = r.Fragment is { } f ? WikiVisibility.RedactLinks(f, visible) : null,
                    });
                }
                // Обычная заметка не сужается (решение 6), но её структурные ссылки на страницы —
                // тот же HiddenWikiRef, что в notes_read (M3); фрагмент — текст самой заметки, не трогаем.
                return Json(new
                {
                    note = RedactHiddenWikiLinks(r.Note, IndexByIds(notesSvc, context.OwnerId), visible),
                    fragment = r.Fragment,
                });
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
                        var rows = noteTasksSvc.ListForNote(context.OwnerId, id);
                        var needle = text.Trim();
                        var hits = rows.Where(r => r.Text == needle).ToList();
                        var matches = hits.Count > 0 ? hits : rows.Where(r => r.Text.Contains(needle)).ToList();
                        if (matches.Count == 0) return Deny($"Чекбокс с текстом \"{needle}\" не найден в заметке");
                        if (matches.Count > 1)
                            return Deny($"Найдено несколько чекбоксов \"{needle}\" — уточни line (строки: "
                                + string.Join(", ", matches.Select(m => m.Line)) + ")");
                        line = matches[0].Line;
                    }
                    return Json(await noteTasksSvc.PromoteAsync(context.OwnerId, id, line.Value));
                }
                catch (KeyNotFoundException) { return Deny($"Заметка {id} не найдена."); }
                catch (UnauthorizedAccessException) { return Deny("Нет доступа к заметке."); }
                catch (InvalidOperationException ex) { return Deny(ex.Message); }
            }

            case "notes_semantic_search":
            {
                var query = StringArg(arguments, "query");
                if (!kbSvc.Available)
                    return Text("Семантический поиск не настроен (нет Dify) — используй notes_search.");
                // Несобранная горизонталь — отказ с причиной, а не пустой список: модель не
                // может отличить «нет результатов» от «поиска не было» (ревью S6g-1; та же
                // формулировка, что у wiki_query).
                if (spheres is null || projects is null)
                    return Deny("Горизонталь вики не собрана (подсистема Notes отключена).");
                var topK = 8;
                if (arguments["topK"] is JsonValue tv && tv.TryGetValue<int>(out var k)) topK = k;
                try
                {
                    // Тот же путь, что у auto-recall (ADR-036, решение 6): notes_semantic_search —
                    // recall по запросу модели, сужается набором видимых разделов хода
                    // (явные notes_list/notes_search/notes_read — не сужаются, решение 2).
                    var results = await kbSvc.SearchAsync(context.OwnerId, query, Math.Clamp(topK, 1, 20),
                        new WikiVisibility.CallContext(session.ProjectId, persona));
                    return Json(results);
                }
                catch (HttpRequestException ex) { return Deny($"Dify недоступен: {ex.Message}"); }
            }

            // ─── Вики (S6c): заявка, факт, вопрос, состояние. Ни один не пишет файлы
            // напрямую: запись — захват (S6h) и очередь разбора (S6b-3/S6e-2), чтение —
            // каталог раздела через внутренний читатель Хранителя. ───

            case "wiki_ingest":
                return await WikiIngestCallAsync(arguments, context.OwnerId, session, ct);

            case "wiki_add_fact":
                return await WikiAddFactCallAsync(arguments, context.OwnerId, session, ct);

            case "wiki_query":
                return WikiQueryCall(arguments, context.OwnerId, session, persona);

            case "wiki_status":
                return WikiStatusCall(arguments, context.OwnerId, session, persona);

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // --- Маршрут: /mcp/notes/{sessionId} ---

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpEndpoints.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

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
        kb?.QueueSync(ownerId);
        await broadcaster.ToOwner(ownerId, new NotesChangedMessage(action, noteId));
    }

    // Компактное представление заметки для списков — как brief у stdio-ветки. Для страницы
    // вики — дополнительно путь с префиксом раздела и живым именем (ADR-036, решение 6):
    // агент не должен принять чужой раздел за свой.
    private static object Brief(NoteSummary n, string? wikiPath = null) => wikiPath is null
        ? (object)new { n.Id, n.Title, n.Source, n.SourceLabel, n.Tags, n.UpdatedAt }
        : new { n.Id, n.Title, n.Source, n.SourceLabel, n.Tags, n.UpdatedAt, path = wikiPath };

    // --- Видимость источника wiki при чтении через notes_* (S6g-2; ADR-036, решения 2 и 6).
    // Набор считает только WikiVisibility.SectionsFor — та же единая точка правды, что у
    // wiki_query и recall (решение 2: ни recall, ни инструменты набор не собирают сами).
    // Обычные заметки не сужаются (решение 6, подтверждено Андреем): правило про страницы
    // разделов, не про заметки.

    private const string HiddenMark = "[скрыто]";

    /// <summary>Набор видимых разделов хода. Несобранная горизонталь — пустое множество:
    /// страница вики не видна нигде (fail-closed), обычные заметки как обычно. В живой
    /// композиции этого не бывает: горизонтали регистрируются тем же рубильником, что notes.</summary>
    private HashSet<string> VisibleSet(string ownerId, Session session, Persona? persona) =>
        spheres is null || projects is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(WikiVisibility.SectionsFor(ownerId,
                new WikiVisibility.CallContext(session.ProjectId, persona), spheres, projects),
                StringComparer.Ordinal);

    /// <summary>Видна ли запись в списке: обычная заметка — всегда (не сужается); страница
    /// вики — только в наборе; страница с неразобранным путём не принадлежит ни одному
    /// разделу и не видна нигде (fail-closed).</summary>
    private static bool WikiReadAllowed(string source, string path, HashSet<string> visible) =>
        source != NotesService.WikiKey
        || (WikiSections.Parse(path) is { } key && visible.Contains(key));

    /// <summary>Является ли запись по id страницей раздела вне набора (или страницей с
    /// неразобранным путём): её имя и текст в выдаче заменяются [скрыто]. Обычная заметка
    /// страницей раздела не является — не закрывается (правило про страницы разделов).</summary>
    private static bool HiddenWikiRef(string noteId, Dictionary<string, NoteSummary> index, HashSet<string> visible) =>
        index.TryGetValue(noteId, out var s) && s.Source == NotesService.WikiKey
        && (WikiSections.Parse(s.Path) is not { } key || !visible.Contains(key));

    private static Dictionary<string, NoteSummary> IndexByIds(NotesService notesSvc, string ownerId)
    {
        var index = new Dictionary<string, NoteSummary>(StringComparer.Ordinal);
        foreach (var s in notesSvc.GetSummaries(ownerId, null, null))
            index[s.Id] = s;
        return index;
    }

    /// <summary>Префикс раздела с живым именем для страницы вики в выдаче (ADR-036, решение 6):
    /// id папки раздела в пути заменяется живым именем проекта/сферы, остальная часть пути —
    /// как есть; «Общее»/«Личное» — свои имена папок без id. Имя не разрешается — id (сирота
    /// уже вне набора видимости, это запасной путь). null — не страница вики.</summary>
    private string? WikiDisplayPath(string ownerId, NoteSummary n) =>
        n.Source == NotesService.WikiKey ? DisplayPathFor(ownerId, n.Path) : null;

    private string? DisplayPathFor(string ownerId, string relPath)
    {
        if (WikiSections.Parse(relPath) is not { } key || WikiSections.Folder(key) is not { } folder)
            return null;
        var head = folder[..folder.IndexOf('/')]; // «Проекты» | «Сферы» | «Общее» | «Личное»
        var name =
            WikiSections.TryProjectId(key, out var projectId) ? projects?.GetById(projectId)?.Name ?? projectId :
            WikiSections.TrySphereId(key, out var sphereId) ? spheres?.SphereName(ownerId, sphereId) ?? sphereId :
            "";
        if (name.Length > 0) head += "/" + name;
        var rest = relPath.Length > folder.Length ? relPath[folder.Length..] : "";
        return rest.Length > 0 ? head + "/" + rest : head;
    }

    /// <summary>Отдача видимой страницы вики (ADR-036, решение 2): содержимое — RedactLinks
    /// (ссылка на страницу вне набора — [скрыто]); структурные списки — тем же правилом:
    /// цель чужого раздела — маркер без имени и текста (заголовок страницы чужого раздела —
    /// тоже текст). Путь — с префиксом раздела (решение 6).</summary>
    private NoteDetail RedactWikiDetail(NotesService notesSvc, string ownerId, NoteDetail d, HashSet<string> visible)
    {
        var index = IndexByIds(notesSvc, ownerId);
        return RedactHiddenWikiLinks(
            d with
            {
                Path = DisplayPathFor(ownerId, d.Path) ?? d.Path,
                Content = WikiVisibility.RedactLinks(d.Content, visible),
            },
            index, visible);
    }

    /// <summary>Структурные ссылки записи на страницы вики (ревью S6g-2, M3): цель вне набора —
    /// [скрыто] без имени и targetId, факт связи остаётся. Применяется и к видимой странице
    /// (RedactWikiDetail), и к обычной заметке: сама обычная заметка не сужается (решение 6),
    /// но её Links/Backlinks — системная проекция страниц-целей, и по targetId скрытой страницы
    /// доставалась вся её страница (пути M1/M2). Текст самой записи здесь не меняется: он текст
    /// пользователя или своего раздела — не текст чужой страницы.</summary>
    private static NoteDetail RedactHiddenWikiLinks(
        NoteDetail d, Dictionary<string, NoteSummary> index, HashSet<string> visible)
    {
        var hiddenLink = new NoteLinkDto("", HiddenMark, false);
        var hiddenBacklink = new NoteBacklinkDto("", HiddenMark, "", "", "");
        return d with
        {
            Links = d.Links.Select(l => HiddenWikiRef(l.TargetId, index, visible) ? hiddenLink : l).ToList(),
            Backlinks = d.Backlinks.Select(b => HiddenWikiRef(b.SourceId, index, visible) ? hiddenBacklink : b).ToList(),
            UnlinkedMentions = d.UnlinkedMentions
                .Select(b => HiddenWikiRef(b.SourceId, index, visible) ? hiddenBacklink : b).ToList(),
        };
    }

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    // Отказ «в вики пишет только Хранитель» (ADR-035, решение 2): тот же код, что у REST
    // (409 wiki_readonly), и текст-подсказка, какую операцию звать вместо прямой правки.
    // Catch во всех мутирующих инструментах обязан стоять ПЕРЕД catch (InvalidOperationException):
    // WikiReadOnlyException наследует её, и общий путь терял код (ревью S1a: у create/update/
    // delete/annotate отказа не было вовсе — его догонял транспорт как «Ошибка: …»).
    private static McpToolCallResult WikiRefusal(WikiReadOnlyException ex) =>
        Deny($"{WikiReadOnlyException.ErrorCode}: {ex.Message}");

    // --- Вики: четыре инструмента (S6c; ADR-035 §5 «нет MCP-принятия пакета», §7 «ссылка
    // вместо текста», §8 «wiki_query без модели»; ADR-036 §2 «видимость», §3 «дом источника»).
    //
    // Порядок защит на каждый вход один: вертикаль → заведённость вики → лимит открытых
    // пакетов → форма аргумента (длина, управляющие символы, «..», форма диапазона) →
    // владение и дом источника (их делает захват, здесь он не дублируется) → заявка. Любой
    // неожиданный сбой — отказ с причиной, не исключение наружу (уроки ревью S4b/S4f/S6e).
    // Отказ не называет того, чего нет: чужой/невидимый раздел не отличается от несуществующего.

    // Форма диапазона — та же, что у ссылки на чат в сноске (WikiPage.TryParseSessionRef):
    // двух форм одного диапазона в контракте быть не должно.
    private static readonly Regex SessionRangeRx = new(
        @"^m(?<from>[0-9]{1,6})-(?<to>[0-9]{1,6})\z", RegexOptions.CultureInvariant);

    private const int MaxSourceRefLength = 512;

    // Id заметки — base64url (NotesService.EncodeId): пробелов и знаков препинания в нём нет.
    // Поэтому «текст вместо ссылки» отсеивается здесь, а не приходит отказом «не найдено»:
    // отказ схемы обязан называть причину схемы (ADR-035 §7: ссылка вместо текста).
    private static readonly Regex NoteIdRx = new(@"^[A-Za-z0-9_-]{1,512}\z", RegexOptions.CultureInvariant);

    // Что отдаётся модели на один вопрос: до трёх подходящих страниц, не больше N файлов
    // раздела на просмотр и не больше MaxQueryPageChars на страницу — дальше полнотекст
    // делает notes_search (ADR-035, решение 8: wiki_query — короткая сборка контекста).
    private const int MaxQueryPages = 3;
    private const int MaxQueryScannedFiles = 100;
    private const int MaxQueryPageChars = 8000;

    /// <summary>Заведённость вики и её корень: тот же признак, что у REST и у очереди
    /// заявок (pages/_schema.md). Отказ — без утечки существования чужой вики.</summary>
    private string? WikiRootOrRefusal(string ownerId, out string? root)
    {
        root = null;
        if (_dataDir is null) return "Каталог данных не задан — вики недоступна.";
        if (!WikiPaths.IsInitialized(_dataDir, ownerId))
            return "Вики этого владельца не заведена: её инструменты работают только с заведённой.";
        root = WikiPaths.Root(_dataDir, ownerId);
        return null;
    }

    /// <summary>Лимит открытых пакетов на всю вики (ADR-035, решение 4): 21-я заявка не
    /// ставится, пока человек не разберёт «Входящие». Занимают место и нечитаемые файлы —
    /// CountOpen уже считает их (fail-closed).</summary>
    private static string? OpenPackagesRefusal(string root) =>
        WikiPackageStore.CountOpen(root) >= WikiPackage.MaxOpenPackages
            ? $"Открытых пакетов уже {WikiPackage.MaxOpenPackages}: разберите Входящие, потом повторяй заявку."
            : null;

    /// <summary>Недоверенный ввод ссылки/аргумента: длина, управляющие символы, «..» —
    /// отказ до обращения к диску.</summary>
    private static bool IsSafeInput(string value, int maxLength) =>
        value.Length > 0 && value.Length <= maxLength
        && !value.Any(char.IsControl)
        && !value.Contains("..", StringComparison.Ordinal);

    // Входы с расходом модели и записью заявки гейтятся флагом llm-wiki (читающие wiki_query/wiki_status — нет).
    // Флаг читается в момент вызова; проверка идёт до любой записи. Состав tools/list не зависит от флага.
    private bool IsLlmWikiOn(string ownerId) =>
        flags?.IsEnabled(ownerId, FeatureFlagKeys.LlmWiki) == true;

    private const string WikiFlagOffText = "Функция выключена: включите «LLM-вики» в экспериментальных функциях.";

    private async Task<McpToolCallResult> WikiIngestCallAsync(
        JsonObject arguments, string ownerId, Session chat, CancellationToken ct)
    {
        try
        {
            if (!IsLlmWikiOn(ownerId)) return Deny(WikiFlagOffText);
            if (wikiIngest is null || wikiCapture is null || wikiChat is null)
                return Deny("Горизонталь вики не собрана (подсистема Notes отключена).");
            if (WikiRootOrRefusal(ownerId, out var root) is { } notReady) return Deny(notReady);
            if (OpenPackagesRefusal(root!) is { } full) return Deny(full);

            var source = StringArg(arguments, "source");
            if (source.Length == 0)
                return Deny("Нужна ССЫЛКА источника: id заметки, путь файла проекта, "
                    + "session:{id}#m{a}-{b} или session:{id}. Текст источника вместо ссылки — не принимается.");
            if (!IsSafeInput(source, MaxSourceRefLength))
                return Deny($"Ссылка источника некорректна: длина до {MaxSourceRefLength}, без управляющих символов и «..».");
            var target = OptionalArg(arguments, "target");
            if (target is not null && target != WikiSections.Common)
                return Deny("target: единственный допустимый — \"common\", и только для источника вне проекта.");

            // Источники следующих фаз — отказ по имени с явной причиной, а не «не найдено».
            if (source.StartsWith("task:", StringComparison.Ordinal)
                || source.StartsWith("dossier:", StringComparison.Ordinal)
                || source.StartsWith("http://", StringComparison.Ordinal)
                || source.StartsWith("https://", StringComparison.Ordinal))
                return Deny("Источники task:, dossier: и url будут в Ф2 (S6k): сейчас доступны "
                    + "заметка, файл проекта и чат.");

            if (source.StartsWith("session:", StringComparison.Ordinal))
                return await IngestChatAsync(source, target, ownerId, ct);

            if (source.Contains('/') || source.Contains('\\'))
            {
                if (chat.ProjectId is not { Length: > 0 } projectId)
                    return Deny("Файл по пути принимается только в чате проекта; вне проекта — id заметки или ссылка чата.");
                var cap = await wikiCapture.CaptureFile(ownerId, projectId, source);
                return cap.Ok
                    ? IngestAnswer(await WikiCardIngest.RunAsync(wikiIngest, cap, root!, ownerId, ct))
                    : Deny(cap.Reason ?? "файл не захвачен");
            }

            if (!NoteIdRx.IsMatch(source))
                return Deny("Нужна ССЫЛКА источника — id заметки, путь файла проекта или session:…, "
                    + "а не его текст: текст источника и готовый план не присылается (ADR-035 §7).");

            var noteCap = await wikiCapture.CaptureNote(ownerId, source, target);
            return noteCap.Ok
                ? IngestAnswer(await WikiCardIngest.RunAsync(wikiIngest, noteCap, root!, ownerId, ct))
                : Deny(noteCap.Reason ?? "заметка не захвачена");
        }
        catch (Exception e)
        {
            return Deny($"сбой заявки: {e.Message}");
        }
    }

    /// <summary>Ссылка на чат: с диапазоном — карточка этой выдержки и заявка на неё;
    /// без диапазона — «разобрать этот чат» по курсору (S6e-2). Раздел назначения — дом
    /// чата (ADR-036, решение 3): его выбирает код, и его же проверяет захват.</summary>
    private async Task<McpToolCallResult> IngestChatAsync(string source, string? target, string ownerId, CancellationToken ct)
    {
        // Defense-in-depth: путь вызывается только из проверенного места, но компилятору это
        // неизвестно — те же локальные переменные, что в CallAsync.
        if (wikiCapture is not { } capture || wikiChat is not { } chatSvc)
            return Deny("Горизонталь вики не собрана (подсистема Notes отключена).");

        if (WikiPage.TryParseSessionRef(source, out var sessionId, out var from, out var to))
        {
            if (sessionsDir?.GetById(sessionId) is not { } chat)
                return Deny("чат не существует");
            if (sessionsDir.ResolveOwnerId(chat) != ownerId)
                return Deny("чат принадлежит другому владельцу: источник — тому же владельцу, что вики");
            var home = chat.ProjectId is { Length: > 0 } projectId
                ? WikiSections.ProjectsPrefix + projectId
                : WikiSections.Personal;
            var cap = await capture.CaptureSession(ownerId, home, sessionId, from, to);
            if (!cap.Ok) return Deny(cap.Reason ?? "выдержка чата не захвачена");
            return await RunIngestAsync(new WikiIngestService.Request(
                "session", source, cap.CardRelPath!, cap.SourceText!, home, target), ownerId, ct);
        }

        var id = source["session:".Length..];
        if (id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            var res = await chatSvc.AnalyzeAsync(ownerId, id, ct);
            return res.Outcome switch
            {
                WikiChatIngestService.OutcomeApplied => Json(new { outcome = res.Outcome, packageIds = res.PackageIds }),
                WikiChatIngestService.OutcomeAwaitingHuman => Text(
                    "разбор поставил пакеты, ни один не применён: они ждут человека в «Входящих»."),
                WikiChatIngestService.OutcomeNothingNew => Text("нечего добавить: " + (res.Reason ?? "без причины")),
                _ => Deny(res.Reason ?? "разбор отклонён"),
            };
        }
        return Deny("Ссылка на чат не разбирается: форма session:{id}#m{a}-{b} или session:{id}.");
    }

    // Заявка по уже захваченной карточке (заметка, файл проекта) идёт через общий код
    // S7b0b — WikiCardIngest: та же склейка «карточка → заявка», что у REST
    // POST api/wiki/ingest/note. Здесь остаётся только отображение исхода на ответ
    // инструмента; путь выдержки чата (IngestChatAsync) ставит заявку сам: её дом знает
    // чат, а не карточка (ADR-036, решение 3).

    private async Task<McpToolCallResult> RunIngestAsync(
        WikiIngestService.Request request, string ownerId, CancellationToken ct)
    {
        if (wikiIngest is not { } ingest)
            return Deny("Горизонталь вики не собрана (подсистема Notes отключена).");
        return IngestAnswer(await ingest.IngestAsync(ownerId, request, ct));
    }

    /// <summary>Исход заявки — ответ инструмента: applied/awaiting_human — JSON с id пакета,
    /// nothing_new и отказ — текст с причиной. Одна формула для обоих путей постановки
    /// заявки: через карточку (WikiCardIngest) и через выдержку чата.</summary>
    private static McpToolCallResult IngestAnswer(WikiIngestService.IngestResult res) => res.Outcome switch
    {
        WikiIngestService.OutcomeApplied => Json(new { outcome = res.Outcome, packageId = res.PackageId }),
        WikiIngestService.OutcomeAwaitingHuman => Json(new
        {
            outcome = res.Outcome,
            packageId = res.PackageId,
            note = "пакет со спорными операциями ждёт решения человека в «Входящих»",
        }),
        WikiIngestService.OutcomeNothingNew => Text("нечего добавить: " + (res.Reason ?? "без причины")),
        _ => Deny(res.Reason ?? "заявка отклонена"),
    };

    private async Task<McpToolCallResult> WikiAddFactCallAsync(
        JsonObject arguments, string ownerId, Session chat, CancellationToken ct)
    {
        try
        {
            if (!IsLlmWikiOn(ownerId)) return Deny(WikiFlagOffText);
            if (wikiIngest is null || wikiCapture is null)
                return Deny("Горизонталь вики не собрана (подсистема Notes отключена).");
            if (WikiRootOrRefusal(ownerId, out var root) is { } notReady) return Deny(notReady);
            if (OpenPackagesRefusal(root!) is { } full) return Deny(full);

            var fact = StringArg(arguments, "fact");
            var quote = StringArg(arguments, "quote");
            var range = StringArg(arguments, "sessionRange");
            if (!IsSafeInput(fact, 600) || fact.Contains('\n'))
                return Deny("факт — одна строка длиной до 600 символов, без управляющих символов");
            if (quote.Length is < 8 or > 300)
                return Deny("цитата — от 8 до 300 символов (ADR-035, решение 3)");
            if (quote.Any(c => char.IsControl(c) && c != '\n'))
                return Deny("в цитате управляющие символы");
            var m = SessionRangeRx.Match(range);
            if (!m.Success
                || !int.TryParse(m.Groups["from"].Value, out var from)
                || !int.TryParse(m.Groups["to"].Value, out var to)
                || from < 1 || from > to)
                return Deny("sessionRange — та же форма, что в ссылке на чат: m{a}-{b}, например m12-17");

            // «Запиши это» относится к этому чату: его дом и есть раздел назначения.
            if (chat.ProjectId is not { Length: > 0 } projectId)
                return Deny("личный раздел — с Ф2");
            var home = WikiSections.ProjectsPrefix + projectId;
            var cap = await wikiCapture.CaptureSession(ownerId, home, chat.Id, from, to);
            if (!cap.Ok) return Deny(cap.Reason ?? "выдержка чата не захвачена");

            // Факт без дословной цитаты в этой выдержке — не факт: та же мера, что у проверок
            // применения (мера 1), а не отдельная догадка тулсета.
            if (!QuoteMatcher.ContainsVerbatim(cap.SourceText!, quote))
                return Deny("цитаты нет в выдержке этого чата: факт обязан быть её дословным фрагментом; "
                    + "страницу выводит Хранитель по этой выдержке");

            return await RunIngestAsync(new WikiIngestService.Request(
                "session", $"session:{chat.Id}#m{from}-{to}", cap.CardRelPath!, cap.SourceText!, home, null),
                ownerId, ct);
        }
        catch (Exception e)
        {
            return Deny($"сбой заявки: {e.Message}");
        }
    }

    private McpToolCallResult WikiQueryCall(
        JsonObject arguments, string ownerId, Session chat, Persona? persona)
    {
        try
        {
            if (spheres is null || projects is null)
                return Deny("Горизонталь вики не собрана (подсистема Notes отключена).");
            var question = StringArg(arguments, "question");
            if (question.Length is < 2 or > 500 || question.Any(char.IsControl))
                return Deny("вопрос — строка от 2 до 500 символов без управляющих символов");
            if (WikiRootOrRefusal(ownerId, out var root) is { } notReady) return Deny(notReady);

            // Набор видимых разделов — только WikiVisibility (ADR-036, решение 2): контекст
            // вызова берётся из хвоста маршрута — сессия → проект → персона.
            var visible = WikiVisibility.SectionsFor(ownerId,
                new WikiVisibility.CallContext(chat.ProjectId, persona), spheres, projects);

            var words = QuestionWords(question);
            var found = new List<(int Score, string Section, string Rel, string Text)>();
            var sb = new StringBuilder();
            sb.Append("Видимые разделы: ").Append(string.Join(", ", visible)).Append('\n');

            foreach (var key in visible)
            {
                if (WikiSections.Folder(key) is not { } folder) continue;
                // Строки каталога — из _index этого раздела (ADR-035, решение 8): просят
                // его, пересобирать его здесь значило бы вторую точку правды.
                if (WikiFileWriter.Read(root!, "pages/" + folder + WikiProjections.IndexFileName) is { } index)
                    sb.Append('\n').Append(WikiVisibility.RedactLinks(index, visible));

                var dir = SafePath.JoinBelowRoot(root!, "pages/" + folder);
                if (!Directory.Exists(dir)) continue;
                var allRels = Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                    .Where(rel => !WikiPaths.IsProjection(rel))
                    .OrderBy(rel => rel, StringComparer.Ordinal)
                    .ToList();
                // Усечение обхода названо вслух: молчание о том, что в разделе есть и
                // другие страницы, читалось бы как «их нет» (тот же принцип, что у _index).
                if (allRels.Count > MaxQueryScannedFiles)
                    sb.Append("\n…ещё ").Append(allRels.Count - MaxQueryScannedFiles)
                        .Append(" файлов раздела «").Append(key).Append("» не просмотрены — полнотекст: notes_search.");
                foreach (var rel in allRels.Take(MaxQueryScannedFiles))
                {
                    if (WikiFileWriter.Read(root!, "pages/" + folder + rel) is not { } text) continue;
                    var score = ScoreText(text, words);
                    if (score > 0) found.Add((score, key, folder + rel, text));
                }
            }

            var top = found
                .OrderByDescending(p => p.Score)
                .ThenBy(p => p.Rel, StringComparer.Ordinal)
                .Take(MaxQueryPages)
                .ToList();
            if (top.Count == 0)
                return Text(sb.Append("\nСтраниц по этому вопросу нет. Полнотекст по сырью — notes_search.").ToString());

            foreach (var p in top)
            {
                // И строки каталога, и текст страницы — через RedactLinks: ссылка на раздел
                // вне набора и её заголовок не должны доходить до модели (ADR-036, §2).
                var body = WikiVisibility.RedactLinks(p.Text, visible);
                sb.Append("\n—— ").Append(p.Section).Append(" / ").Append(p.Rel).Append(" ——\n")
                    .Append(body.Length > MaxQueryPageChars ? body[..MaxQueryPageChars] + "\n…(обрезано)" : body);
            }
            return Text(sb.Append("\nПолнотекст по сырью — notes_search.").ToString());
        }
        catch (Exception e)
        {
            return Deny($"сбой вопроса: {e.Message}");
        }
    }

    private static IReadOnlyList<string> QuestionWords(string question) =>
        Regex.Split(question, @"[^\p{L}\p{N}]+")
            .Where(w => w.Length >= 3)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

    private static int ScoreText(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return 0;
        var hay = text.ToLowerInvariant();
        var score = 0;
        foreach (var w in words)
            if (hay.Contains(w, StringComparison.Ordinal)) score++;
        return score;
    }

    private McpToolCallResult WikiStatusCall(JsonObject arguments, string ownerId, Session session, Persona? persona)
    {
        try
        {
            if (WikiRootOrRefusal(ownerId, out var root) is { } notReady) return Deny(notReady);
            // Пакеты раздела вне набора хода не называются: ни их id, ни их раздел
            // (ADR-036, решение 2). open/limit — состояние всей вики: лимит заявок
            // тоже по всей вике (OpenPackagesRefusal), число обязано совпадать с отказом.
            var visible = VisibleSet(ownerId, session, persona);
            if (OptionalArg(arguments, "packageId") is { } id)
            {
                if (!WikiPackage.IsSafeId(id)) return Deny("id пакета некорректен");
                var read = WikiPackageStore.TryRead(root!, id);
                if (read.State != WikiPackageFileState.Valid || read.Package is not { } one)
                    return Deny("пакета с таким id нет");
                if (!visible.Contains(one.Section)) return Deny("пакета с таким id нет");
                return Json(BriefPackage(one));
            }
            var list = WikiPackageStore.List(root!);
            return Json(new
            {
                open = WikiPackageStore.CountOpen(root!),
                limit = WikiPackage.MaxOpenPackages,
                packages = list.Packages
                    .Where(p => WikiPackage.IsOpen(p.Status) && visible.Contains(p.Section))
                    .Select(BriefPackage).ToList(),
                unreadableFiles = list.InvalidFiles,
            });
        }
        catch (Exception e)
        {
            return Deny($"сбой: {e.Message}");
        }
    }

    // Сводка пакета для агента: где, что ждёт, сколько в нём спорного. Тел операций и цитат
    // нет: их смотрит человек в «Входящих», агент их не принимает (ADR-035, решение 5).
    private static object BriefPackage(WikiPackage p) => new
    {
        p.Id,
        p.Section,
        p.Status,
        reason = p.StatusReason,
        createdAt = p.CreatedAt,
        operations = p.Operations.Count,
        contested = p.Operations.Count(o => o.Decision == WikiPackage.DecisionInbox && o.HumanDecision is null),
    };

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

    // Инструменты вики (S6c): имена и порядок — часть отпечатка tools/list, их сторож —
    // McpToolsetStabilityTests. В замороженной stdio-ветке отката их нет намеренно (там нет
    // вертикали), поэтому WikiTools — отдельный список, не часть CoreTools: парность
    // NotesToolsetParityTests этого требует. Ни один из них не принимает и не откатывает
    // пакет, не повышает страницу и не меняет политику применения (ADR-035, решение 5).
    internal static readonly IReadOnlyList<McpToolSchema> WikiTools =
    [
        Tool("wiki_ingest",
            "Поставить источник в очередь разбора вики. source — только ССЫЛКА: id заметки | путь файла проекта | session:{id}#m{a}-{b} | session:{id}. Текст источника вместо ссылки — отказ. target=\"common\" — только для источника вне проекта.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "source" },
                ["properties"] = new JsonObject
                {
                    ["source"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Ссылка: id заметки, путь файла проекта или session:{id}#m{a}-{b} / session:{id}",
                    },
                    ["target"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JsonArray { "common" },
                        ["description"] = "Явный выбор человека: источник вне проекта — в «Общее»; проектным нельзя",
                    },
                },
            }),
        Tool("wiki_add_fact",
            "Явное «запиши это» этого чата: выдержать указанный диапазон его сообщений и поставить заявку разбора. Факт обязан быть подтверждён дословной цитатой из этой выдержки; страницу пишет Хранитель.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "fact", "quote", "sessionRange" },
                ["properties"] = new JsonObject
                {
                    ["fact"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Что закрепить: одна строка до 600 символов",
                    },
                    ["quote"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Дословная цитата из этого чата, 8–300 символов (скопируй из его сообщений)",
                    },
                    ["sessionRange"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Диапазон сообщений этого чата в форме ссылки: m{a}-{b}, например m12-17",
                    },
                },
            }),
        Tool("wiki_query",
            "Спросить вики без модели: каталоги (_index) видимых в этом ходу разделов плюс до трёх подходящих страниц. Строки и ссылки вне видимого набора отдаются как [скрыто].",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "question" },
                ["properties"] = new JsonObject
                {
                    ["question"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Вопрос по базе знаний, 2–500 символов",
                    },
                },
            }),
        Tool("wiki_status",
            "Состояние вики: сколько открытых пакетов (лимит 20) и какие; по packageId — сводка одного пакета. Принять или откатить пакет может только человек.",
            new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["packageId"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "id пакета из списка — его сводка; пусто — состояние всей вики",
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
