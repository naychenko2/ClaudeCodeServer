using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Базы знаний Dify (dify: search_knowledge + CRUD датасетов/документов/сегментов) поверх
/// HTTP-транспорта — последний продуктовый сервер фазы 2 (ADR-012, волна 4). Раньше это был
/// mcp-dify (TypeScript со сборкой, единственный с внешней зависимостью @modelcontextprotocol/
/// sdk), объявлявшийся НЕ нашим кодом, а внешним базовым конфигом McpConfigPath. Волна 4
/// перенесла объявление в код: источник правды ключа и адреса — секция Dify appsettings,
/// общая с KnowledgeService/Notes/Memory; 274 строки TS-клиента схлопнулись в существующий
/// C# (KnowledgeService), а DIFY_API_KEY перестал уезжать в env процесса — ключ живёт только
/// на бэкенде. stdio-ветка отката (Mcp:HttpTransport) поднимает mcp-dify/dist/index.js тем же
/// ключом из секции; если dist не собран, ход продолжает ехать записью внешнего конфига.
///
/// Маршрут — <c>POST /mcp/dify/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ (как волны 2–3).
/// По ней тулсет живьём резолвит: владельца (GetOwned), его username (классификация датасетов),
/// проект чата и ДЕФОЛТНЫЙ датасет (WorkspaceKnowledgeStore по рабочей папке сессии — той же
/// формулой EffectiveRoot, что и инжекция env в stdio-ветке). Датасет может появиться у проекта
/// в середине жизни чата, поэтому резолв — на каждый tools/list и вызов, без запекания.
///
/// Состав = f(сессия), не f(ход): у проекта чата есть база → search-only (4 инструмента,
/// эквивалент env DIFY_SEARCH_ONLY=true stdio-ветки), нет базы или чат вне проекта → полный
/// состав (12). Отпечаток для сигнатуры запуска (shapes["dify"]) строится по той же формуле.
///
/// Изоляция — проверки stdio/REST-веток сохранены на КАЖДЫЙ вызов:
/// - <see cref="DifyOptions.Namespace"/> (контуры Dev/Prod на одном Dify) — внутри
///   KnowledgeService.ListDatasetsAsync/CreateDatasetAsync, логические имена без префикса;
/// - релевантность датасета пользователю — KnowledgeBaseCatalogService.ResolveReadableAsync
///   (своя или публичная — доступна, чужая помеченная — нет), как каждый {id}-эндпоинт REST
///   (KnowledgeBasesController). У stdio-сервера этого гейта НЕ БЫЛО: TS-клиент ходил в Dify
///   ключом инстанса и видел ВСЕ датасеты — перенос ужесточил доступ, а не перенёс брешь;
/// - форма document_id — белый список IsValidDifyId ДО резолва датасета и любого HTTP:
///   dot-segment-пейлоад («../../{uuid}/documents/{doc}») раньше резолвился HttpClient'ом
///   в чужой датасет под общим ключом workspace (блокер приёмки 4.1); парная защита
///   REST-пути — экранирование сегментов в KnowledgeService.
///
/// Ключ Dify — секрет: наружу не уезжает ни в env, ни в конфиг хода; тексты ошибок для модели
/// собираются из статуса/тела ответа Dify (ключ в них не возвращается — он только в заголовке
/// Authorization). Сторож парности со stdio-веткой отката — DifyToolsetParityTests.
/// </summary>
public sealed class DifyToolset(
    KnowledgeService knowledge,
    KnowledgeBaseCatalogService catalog,
    SessionManager sessions,
    ProjectManager projects,
    UserStore users,
    WorkspaceKnowledgeStore workspaceStore) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/dify/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession)
    public const string ServerName = "dify";

    public string Name => ServerName;
    public string Version => "1.0.0";

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        !knowledge.IsConfigured || !TryResolve(context, out _, out _, out var searchOnly, out _)
            ? []
            : searchOnly ? SearchOnlyTools : AllTools;

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Права и контекст — на КАЖДЫЙ вызов (см. шапку): хвост → сессия владельца токена →
        // username/проект/дефолтный датасет живьём
        if (!TryResolve(context, out var username, out var defaultDatasetId, out var searchOnly, out var error))
            return Deny(error);
        if (!knowledge.IsConfigured)
            return Deny("Базы знаний не настроены: задайте Dify:ApiUrl и Dify:ApiKey в конфигурации сервера.");

        // search-only (у проекта чата есть база): write-инструменты недоступны и в составе,
        // и на вызове — defense-in-depth, урок приёмки волны 2
        if (searchOnly && !SearchOnlyNames.Contains(tool))
            return Deny("У проекта чата есть своя база знаний — сервер работает в режиме поиска "
                + "(DIFY_SEARCH_ONLY): управление базами и документами недоступно.");

        try
        {
            switch (tool)
            {
                case "search_knowledge":
                {
                    var query = StringArg(arguments, "query").Trim();
                    if (query.Length == 0) return Deny("Нужен параметр query");
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    var topK = Math.Clamp(IntArg(arguments, "top_k") ?? 5, 1, 20);
                    var method = OptionalArg(arguments, "search_method");
                    var thresholdEnabled = BoolArg(arguments, "score_threshold_enabled");
                    var threshold = Math.Clamp(DoubleArg(arguments, "score_threshold") ?? 0.5, 0, 1);
                    var chunks = await knowledge.RetrieveAsync(ds.Id, query, topK,
                        searchMethod: method, scoreThresholdEnabled: thresholdEnabled,
                        scoreThreshold: threshold);
                    return Json(new
                    {
                        query = KnowledgeService.TrimQuery(query),
                        datasetId = ds.Id,
                        items = chunks.Select(c => new
                        {
                            content = c.Content,
                            score = c.Score,
                            documentId = c.DocumentId,
                            documentName = c.DocumentName,
                            metadata = c.Metadata,
                        }),
                    });
                }

                case "list_datasets":
                {
                    var (_, items) = await catalog.ListForUserAsync(username);
                    return Json(new
                    {
                        configured = true,
                        items = items.Select(i => new
                        {
                            i.Id, i.Title, i.Type, i.Visibility, i.DocumentCount, i.Description,
                        }),
                    });
                }

                case "create_dataset":
                {
                    var name = StringArg(arguments, "name").Trim();
                    if (name.Length == 0) return Deny("Нужен параметр name");
                    // Семантика REST (KnowledgeBasesController.Create), не сырой TS-клиент:
                    // личная база получает префикс "{username}:kb:", публичная живёт без
                    // префикса — двоеточие в её названии маскировало бы базу под чужую личную
                    var isPublic = StringArg(arguments, "permission") == "all_team_members";
                    if (isPublic && name.Contains(':'))
                        return Deny("Двоеточие в названии публичной базы недопустимо: имя живёт в общем "
                            + "пространстве, и двоеточие маскировало бы её под чужую личную базу.");
                    var logicalName = isPublic ? name : $"{username}:kb:{name}";
                    var permission = isPublic ? "all_team_members" : "only_me";
                    // Лимит Dify: имя датасета ≤ 40 символов (вместе с префиксом владельца) —
                    // режем сами с внятным текстом, иначе модель получит сырую 400-ю pydantic.
                    // Публичная база живёт без префикса — её планка и подсказка полные 40
                    if (logicalName.Length > 40)
                    {
                        var maxName = isPublic ? 40 : 40 - username.Length - 4;
                        return Deny(isPublic
                            ? $"Название слишком длинное: лимит Dify — 40 символов. Сократи название "
                            + $"минимум до {maxName} символов."
                            : $"Название слишком длинное: лимит Dify — 40 символов вместе с префиксом "
                            + $"владельца («{username}:kb:»). Сократи название минимум до {maxName} символов.");
                    }
                    var description = OptionalArg(arguments, "description");
                    // Явная проверка коллизии: Dify на дубль имени отвечает невнятной ошибкой,
                    // а совпадение с чужой личной базой нельзя доводить до создания
                    var existing = await knowledge.ListDatasetsAsync();
                    if (existing.Any(d => string.Equals(d.Name, logicalName, StringComparison.OrdinalIgnoreCase)))
                        return Deny("База с таким названием уже существует.");
                    var datasetId = await knowledge.CreateDatasetAsync(logicalName, permission, description,
                        ValidateIndexing(OptionalArg(arguments, "indexing_technique")));
                    return Json(new { id = datasetId, title = name, visibility = isPublic ? "public" : "personal" });
                }

                case "delete_dataset":
                {
                    var id = StringArg(arguments, "dataset_id").Trim();
                    if (id.Length == 0) return Deny("Нужен параметр dataset_id");
                    var dataset = await catalog.ResolveReadableAsync(username, id);
                    if (dataset is null) return Deny($"База {id} не найдена или недоступна.");
                    // Привязанные базы (заметки/проекты/память персон) не удаляются и из UI —
                    // их удаляют разделы-владельцы; публичные — только админу
                    if (!catalog.IsDeletable(dataset, username, IsAdmin(context.OwnerId)))
                        return Deny("Удаление этой базы недоступно: она привязана к другому разделу "
                            + "(заметки/проект/память персоны), а для публичной нужны права администратора.");
                    await knowledge.DeleteDatasetAsync(id);
                    return Text($"База знаний {id} удалена");
                }

                case "list_documents":
                {
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    var page = Math.Clamp(IntArg(arguments, "page") ?? 1, 1, 100);
                    var limit = Math.Clamp(IntArg(arguments, "limit") ?? 20, 1, 100);
                    var keyword = OptionalArg(arguments, "keyword");
                    var docs = await knowledge.ListDocumentsAsync(ds.Id, page, limit, keyword: keyword);
                    return Json(new
                    {
                        data = docs.Data.Select(d => new { d.Id, d.Name, d.IndexingStatus, d.WordCount }),
                        docs.Total, docs.HasMore,
                        page,
                    });
                }

                case "create_document_by_text":
                {
                    var name = StringArg(arguments, "name").Trim();
                    var text = StringArg(arguments, "text");
                    if (name.Length == 0) return Deny("Нужен параметр name");
                    if (text.Length == 0) return Deny("Пустой text");
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    var doc = await knowledge.IndexFileByTextAsync(ds.Id, name, text,
                        indexingTechnique: ValidateIndexing(OptionalArg(arguments, "indexing_technique")),
                        processRuleMode: ValidateProcessRule(OptionalArg(arguments, "process_rule_mode")));
                    return Json(new { document = new { doc.Id, doc.Name, doc.IndexingStatus } });
                }

                case "create_document_by_file":
                {
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    if (!TryFileArgs(arguments, out var fileName, out var bytes, out var fileError))
                        return Deny(fileError);
                    var doc = await knowledge.IndexFileByBytesAsync(ds.Id, fileName, bytes,
                        indexingTechnique: ValidateIndexing(OptionalArg(arguments, "indexing_technique")),
                        processRuleMode: ValidateProcessRule(OptionalArg(arguments, "process_rule_mode")));
                    return Json(new { document = new { doc.Id, doc.Name, doc.IndexingStatus } });
                }

                case "update_document_by_text":
                {
                    if (!TryDocumentId(arguments, out var documentId, out var documentIdError))
                        return Deny(documentIdError);
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    var doc = await knowledge.UpdateDocumentByTextAsync(ds.Id, documentId,
                        OptionalArg(arguments, "name"), OptionalArg(arguments, "text"),
                        ValidateProcessRule(OptionalArg(arguments, "process_rule_mode")));
                    return Json(new { document = new { doc.Id, doc.Name, doc.IndexingStatus } });
                }

                case "update_document_by_file":
                {
                    if (!TryDocumentId(arguments, out var documentId, out var documentIdError))
                        return Deny(documentIdError);
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    if (!TryFileArgs(arguments, out var fileName, out var bytes, out var fileError))
                        return Deny(fileError);
                    var doc = await knowledge.UpdateDocumentByFileAsync(ds.Id, documentId, bytes, fileName,
                        ValidateProcessRule(OptionalArg(arguments, "process_rule_mode")));
                    return Json(new { document = new { doc.Id, doc.Name, doc.IndexingStatus } });
                }

                case "delete_document":
                {
                    if (!TryDocumentId(arguments, out var documentId, out var documentIdError))
                        return Deny(documentIdError);
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    await knowledge.DeleteDocumentAsync(resolved.Id, documentId);
                    return Text($"Документ {documentId} удалён");
                }

                case "list_segments":
                {
                    if (!TryDocumentId(arguments, out var documentId, out var documentIdError))
                        return Deny(documentIdError);
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    var segments = await knowledge.ListSegmentsAsync(resolved.Id, documentId,
                        OptionalArg(arguments, "keyword"), OptionalArg(arguments, "status"));
                    return Json(new
                    {
                        data = segments.Select(s => new { s.Id, s.Position, s.Content, s.WordCount }),
                    });
                }

                case "add_segments":
                {
                    if (!TryDocumentId(arguments, out var documentId, out var documentIdError))
                        return Deny(documentIdError);
                    var resolved = await ResolveDatasetAsync(username, arguments, defaultDatasetId);
                    if (resolved is not { } ds) return DatasetDenied(arguments, defaultDatasetId);
                    var drafts = SegmentDrafts(arguments);
                    if (drafts.Count == 0) return Deny("Нужен непустой массив segments");
                    var created = await knowledge.AddSegmentsAsync(resolved.Id, documentId, drafts);
                    return Json(new
                    {
                        data = created.Select(s => new { s.Id, s.Position, s.Content, s.WordCount }),
                    });
                }

                default:
                    return Deny($"Неизвестный инструмент: {tool}");
            }
        }
        catch (HttpRequestException ex)
        {
            // Текст для модели: статус и тело ответа Dify (там нет ключа — он живёт только
            // в заголовке Authorization), сырые исключения наружу не выпускаем
            return Deny($"Dify недоступен или отклонил запрос: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return Deny(ex.Message);
        }
    }

    /// <summary>Хвост маршрута для конфига хода: единая точка с TryParseRoute.</summary>
    internal static string RouteTail(string sessionId) => sessionId;

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + RouteTail(sessionId);

    // Имена search-only ядра: та же четвёрка, что у stdio-ветки при DIFY_SEARCH_ONLY=true
    internal static readonly HashSet<string> SearchOnlyNames =
        ["search_knowledge", "list_datasets", "list_documents", "list_segments"];

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

    // Идентификаторы Dify (документы/датасеты — UUID) — белый список формы, как у
    // TryParseRoute/resumeSessionId. Гейт стоит ДО резолва датасета и любого HTTP:
    // dot-segment-пейлоад в document_id раньше резолвился HttpClient'ом в чужой датасет
    // под общим ключом workspace (блокер приёмки волны 4.1) — теперь он получает внятный
    // отказ, не доезжая до Dify
    internal static bool IsValidDifyId(string id) =>
        id.Length is >= 1 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // document_id из аргументов: непустой и прошедший белый список формы
    private static bool TryDocumentId(JsonObject arguments,
        out string documentId,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        documentId = StringArg(arguments, "document_id").Trim();
        if (documentId.Length == 0) { error = "Нужен параметр document_id"; return false; }
        if (!IsValidDifyId(documentId))
        {
            error = "Некорректный document_id: это идентификатор документа Dify из латиницы, "
                + "цифр, «-» и «_» — пути и относительные сегменты («/», «..») в нём не допускаются.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// Резолв хвоста в контекст вызова: сессия владельца токена → username (классификация
    /// датасетов) → дефолтный датасет проекта чата. Датасет ищется по рабочей папке сессии
    /// (EffectiveRoot — worktree чата приоритетнее корня проекта), той же формулой, что
    /// инжекция DIFY_DEFAULT_DATASET_ID в stdio-ветке и shape в ClaudeSession: расхождение
    /// формул означало бы холостой перезапуск CLI (урок приёмки волны 2). Чат вне проекта —
    /// НЕ отказ: дефолтной базы нет, состав полный (как у stdio без DIFY_DEFAULT_DATASET_ID).
    /// </summary>
    private bool TryResolve(McpToolCallContext context,
        out string username, out string? defaultDatasetId, out bool searchOnly,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        username = "";
        defaultDatasetId = null;
        searchOnly = false;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера баз знаний — вызов отклонён.";
            return false;
        }
        var session = sessions.GetOwned(sessionId, context.OwnerId);
        if (session is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к базам знаний закрыт.";
            return false;
        }
        // Username — из стора (сервисный JWT может не нести claim Name), как у wsp
        username = users.GetById(context.OwnerId)?.Username ?? context.OwnerId;
        if (session.ProjectId is { } pid && projects.GetById(pid) is { OwnerId: var owner } project
            && owner == context.OwnerId)
        {
            var root = SessionManager.EffectiveRoot(session, project.RootPath);
            defaultDatasetId = workspaceStore.GetByPath(root)?.DifyDatasetId;
        }
        searchOnly = !string.IsNullOrEmpty(defaultDatasetId);
        error = null;
        return true;
    }

    // Датасет вызова: явный dataset_id или дефолт проекта чата — оба через проверку
    // релевантности (ResolveReadableAsync): свой/публичный проходит, чужой помеченный — нет.
    private async Task<DifyDatasetListItem?> ResolveDatasetAsync(string username,
        JsonObject arguments, string? defaultDatasetId)
    {
        var id = StringArg(arguments, "dataset_id").Trim();
        if (id.Length == 0) id = defaultDatasetId ?? "";
        return id.Length == 0 ? null : await catalog.ResolveReadableAsync(username, id);
    }

    // Отказ с подсказкой, когда датасет не резолвился (не задан / чужой / Dify лежит)
    private static McpToolCallResult DatasetDenied(JsonObject arguments, string? defaultDatasetId)
    {
        var explicitId = arguments["dataset_id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (string.IsNullOrEmpty(explicitId) && string.IsNullOrEmpty(defaultDatasetId))
            return Deny("dataset_id не указан, и у проекта чата нет своей базы знаний "
                + "(DIFY_DEFAULT_DATASET_ID не задан) — укажи dataset_id явно (список: list_datasets).");
        return Deny("База знаний не найдена или недоступна: датасет чужого пользователя скрыт. "
            + "Доступны свои и публичные базы — список: list_datasets.");
    }

    private bool IsAdmin(string ownerId) => users.GetById(ownerId)?.Role == "admin";

    // base64-файл из аргументов: пустой/кривой base64 — внятный отказ, а не исключение
    private static bool TryFileArgs(JsonObject arguments,
        out string fileName, out byte[] bytes, out string error)
    {
        fileName = StringArg(arguments, "file_name").Trim();
        bytes = [];
        if (fileName.Length == 0) { error = "Нужен параметр file_name"; return false; }
        var base64 = StringArg(arguments, "file_base64");
        if (base64.Length == 0) { error = "Нужен параметр file_base64"; return false; }
        try
        {
            bytes = Convert.FromBase64String(base64);
            if (bytes.Length == 0) { error = "Файл пуст"; return false; }
        }
        catch (FormatException)
        {
            error = "file_base64 не является корректной кодировкой Base64";
            return false;
        }
        error = "";
        return true;
    }

    private static List<KnowledgeService.DifySegmentDraft> SegmentDrafts(JsonObject arguments)
    {
        var result = new List<KnowledgeService.DifySegmentDraft>();
        if (arguments["segments"] is not JsonArray array) return result;
        foreach (var node in array)
        {
            if (node is not JsonObject seg) continue;
            var content = StringArg(seg, "content");
            if (content.Length == 0) continue;
            var keywords = new List<string>();
            if (seg["keywords"] is JsonArray kw)
                foreach (var k in kw)
                    if (k is JsonValue kv && kv.TryGetValue<string>(out var s) && s.Length > 0)
                        keywords.Add(s);
            result.Add(new KnowledgeService.DifySegmentDraft(content,
                OptionalArg(seg, "answer"), keywords.Count > 0 ? keywords : null));
        }
        return result;
    }

    // Значения enum-параметров Dify: неизвестное тихо превращаем в дефолт инстанса/авто —
    // грубое значение от модели не должно ронять запрос целиком
    private static string? ValidateIndexing(string? value) =>
        value is "high_quality" or "economy" ? value : null;

    private static string? ValidateProcessRule(string? value) =>
        value is "automatic" or "custom" ? value : null;

    private static int? IntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static double? DoubleArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static bool BoolArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name);
        return value.Length == 0 ? null : value;
    }

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    // --- Полный состав: 12 инструментов, порт схем mcp-dify/src/tools/* ---
    // (источник контракта — здесь, mcp-dify заморожен как ветка отката; сторож —
    // DifyToolsetParityTests). Схемы объявлены ВЫШЕ списков: инициализаторы статических
    // полей идут в текстовом порядке, список выше схемы забрал бы null

    // Описание дефолтного dataset_id: env DIFY_DEFAULT_DATASET_ID у stdio-ветки, здесь —
    // база проекта чата (та же семантика, имя env в скобках для паритета текста)
    private const string DatasetIdDescription =
        "ID базы знаний (по умолчанию — база проекта текущего чата, DIFY_DEFAULT_DATASET_ID)";

    private static readonly McpToolSchema SearchKnowledgeTool = new("search_knowledge",
        "Семантический поиск по базе знаний Dify. Возвращает релевантные фрагменты с оценкой схожести.",
        Obj(new JsonObject
        {
            ["query"] = Str("Текст поискового запроса"),
            ["dataset_id"] = Str(DatasetIdDescription),
            ["top_k"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Максимальное число результатов (по умолчанию 5)",
                ["default"] = 5,
            },
            ["search_method"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("semantic_search", "keyword_search", "full_text_search"),
                ["description"] = "Метод поиска: semantic_search, keyword_search или full_text_search "
                    + "(по умолчанию — гибридный с фолбэком на семантический)",
            },
            ["score_threshold_enabled"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Включить фильтрацию по минимальному score",
            },
            ["score_threshold"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Минимальный score (0–1), работает при score_threshold_enabled=true",
            },
        }, "query"));

    private static readonly McpToolSchema ListDatasetsTool = new("list_datasets",
        "Получить список баз знаний (Knowledge Bases) в Dify с пагинацией.",
        Obj(new JsonObject
        {
            ["page"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Номер страницы",
                ["default"] = 1,
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Записей на странице",
                ["default"] = 20,
            },
        }));

    private static readonly McpToolSchema ListDocumentsTool = new("list_documents",
        "Получить список документов в базе знаний.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["page"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Номер страницы",
                ["default"] = 1,
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Записей на странице",
                ["default"] = 20,
            },
            ["keyword"] = Str("Фильтр по ключевому слову в названии документа"),
        }));

    private static readonly McpToolSchema ListSegmentsTool = new("list_segments",
        "Получить список сегментов (чанков) документа.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["document_id"] = Str("ID документа"),
            ["keyword"] = Str("Фильтр по ключевому слову в тексте сегмента"),
            ["status"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("indexed", "waiting", "indexing", "error"),
                ["description"] = "Фильтр по статусу сегмента",
            },
        }, "document_id"));

    private static readonly McpToolSchema CreateDatasetTool = new("create_dataset",
        "Создать новую базу знаний (Knowledge Base) в Dify.",
        Obj(new JsonObject
        {
            ["name"] = Str("Название базы знаний"),
            ["description"] = Str("Описание базы знаний"),
            ["indexing_technique"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("high_quality", "economy"),
                ["description"] = "Метод индексации: high_quality (embedding) или economy "
                    + "(инвертированный индекс); по умолчанию — настройка инстанса",
            },
            ["permission"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("only_me", "all_team_members"),
                ["description"] = "Права доступа: only_me (личная, по умолчанию) или "
                    + "all_team_members (публичная)",
            },
        }, "name"));

    private static readonly McpToolSchema DeleteDatasetTool = new("delete_dataset",
        "Удалить базу знаний по ID. Операция необратима.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str("ID базы знаний для удаления"),
        }, "dataset_id"));

    private static readonly McpToolSchema CreateDocumentByTextTool = new("create_document_by_text",
        "Создать документ в базе знаний из текстового содержимого.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["name"] = Str("Название документа"),
            ["text"] = Str("Текстовое содержимое документа"),
            ["indexing_technique"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("high_quality", "economy"),
                ["description"] = "Метод индексации: high_quality или economy",
            },
            ["process_rule_mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("automatic", "custom"),
                ["description"] = "Режим обработки: automatic или custom",
            },
        }, "name", "text"));

    private static readonly McpToolSchema CreateDocumentByFileTool = new("create_document_by_file",
        "Создать документ в базе знаний из файла. Файл передаётся в кодировке Base64.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["file_base64"] = Str("Содержимое файла в кодировке Base64"),
            ["file_name"] = Str("Имя файла с расширением (например, report.pdf)"),
            ["indexing_technique"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("high_quality", "economy"),
                ["description"] = "Метод индексации: high_quality или economy",
            },
            ["process_rule_mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("automatic", "custom"),
                ["description"] = "Режим обработки: automatic или custom",
            },
        }, "file_base64", "file_name"));

    private static readonly McpToolSchema UpdateDocumentByTextTool = new("update_document_by_text",
        "Обновить существующий документ новым текстовым содержимым.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["document_id"] = Str("ID документа для обновления"),
            ["name"] = Str("Новое название документа"),
            ["text"] = Str("Новое текстовое содержимое"),
            ["process_rule_mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("automatic", "custom"),
                ["description"] = "Режим обработки: automatic или custom",
            },
        }, "document_id"));

    private static readonly McpToolSchema UpdateDocumentByFileTool = new("update_document_by_file",
        "Заменить файл существующего документа. Новый файл передаётся в кодировке Base64.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["document_id"] = Str("ID документа для обновления"),
            ["file_base64"] = Str("Новое содержимое файла в кодировке Base64"),
            ["file_name"] = Str("Имя файла с расширением"),
            ["process_rule_mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = StrEnum("automatic", "custom"),
                ["description"] = "Режим обработки: automatic или custom",
            },
        }, "document_id", "file_base64", "file_name"));

    private static readonly McpToolSchema DeleteDocumentTool = new("delete_document",
        "Удалить документ из базы знаний. Операция необратима.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["document_id"] = Str("ID документа для удаления"),
        }, "document_id"));

    private static readonly McpToolSchema AddSegmentsTool = new("add_segments",
        "Добавить сегменты (чанки) в существующий документ базы знаний.",
        Obj(new JsonObject
        {
            ["dataset_id"] = Str(DatasetIdDescription),
            ["document_id"] = Str("ID документа"),
            ["segments"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["description"] = "Список сегментов для добавления",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["content"] = Str("Текст сегмента"),
                        ["answer"] = Str("Ответ для QA-режима документа"),
                        ["keywords"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "Ключевые слова сегмента",
                        },
                    },
                    ["required"] = StrEnum("content"),
                },
            },
        }, "document_id", "segments"));

    // Составы собираются ПОСЛЕ объявлений схем (порядок статической инициализации).
    // Порядок — как у stdio-ветки (порядок регистрации в index.ts: search → datasets →
    // documents → segments); сторож порядка — DifyToolsetParityTests
    internal static readonly IReadOnlyList<McpToolSchema> SearchOnlyTools =
    [
        SearchKnowledgeTool, ListDatasetsTool, ListDocumentsTool, ListSegmentsTool,
    ];

    internal static readonly IReadOnlyList<McpToolSchema> AllTools =
    [
        SearchKnowledgeTool,
        ListDatasetsTool, CreateDatasetTool, DeleteDatasetTool,
        ListDocumentsTool, CreateDocumentByTextTool, CreateDocumentByFileTool,
        UpdateDocumentByTextTool, UpdateDocumentByFileTool, DeleteDocumentTool,
        ListSegmentsTool, AddSegmentsTool,
    ];

    // --- Хелперы схем (как у CodeGraphToolset) ---

    private static JsonArray StrEnum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Str(string? description = null) =>
        description is null
            ? new JsonObject { ["type"] = "string" }
            : new JsonObject { ["type"] = "string", ["description"] = description };

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (required.Length > 0) schema["required"] = StrEnum(required);
        schema["properties"] = properties;
        return schema;
    }
}
