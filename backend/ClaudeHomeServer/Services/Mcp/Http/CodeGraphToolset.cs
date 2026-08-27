using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.CodeGraph;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Граф кода проекта (codegraph_find/neighbors/hubs) поверх HTTP-транспорта — переехавший
/// с node сервер (ADR-012, фаза 2 волна 3). Раньше это был mcp/codegraph-server: фасад к
/// CodeGraphController с сервисным JWT. Здесь вызовы идут напрямую в CodeGraphQueryService —
/// HTTP-хоп через собственный Kestrel не нужен.
///
/// Маршрут — <c>POST /mcp/codegraph/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ, по ней
/// тулсет резолвит проект чата и его рабочее дерево. Рабочее дерево (CODEGRAPH_ROOT_PATH
/// stdio-ветки) в маршрут НЕ кладётся осознанно: дерево — производная сессии (WorktreePath
/// ?? корень проекта), резолвится ЖИВЬЁМ на каждый вызов — подключение/отключение worktree
/// посреди сессии подхватывается без пересоздания адаптера, а адрес и состав от дерева
/// не зависят (инвариант IMcpToolset: в shapes дерево класть нельзя — мерцание сигнатуры
/// перезапускает CLI; фиксация решения — ADR-012, волна 3).
///
/// Изоляция: сессия из хвоста обязана принадлежать владельцу токена (GetOwned), проект —
/// тому же владельцу. Белый список деревьев из CodeGraphController.ResolveRoot здесь
/// выполнен по построению: корень — это либо корень проекта, либо WorktreePath САМОЙ
/// сессии-вызывателя, строка rootPath параметром не принимается вовсе.
/// Гейт tool:codegraph (Off-привязка персоны) проверяется дважды — в составе и на вызове
/// (defense-in-depth, урок приёмки волны 2).
///
/// Состав постоянный (3 чтения). Сторож парности со stdio-веткой отката —
/// CodeGraphNotificationsToolsetParityTests (index.js заморожен).
/// </summary>
public sealed class CodeGraphToolset(
    CodeGraphService graphs,
    CodeGraphQueryService queries,
    ProjectManager projects,
    SessionManager sessions,
    PersonaManager personas,
    PersonaBindingsService bindings,
    FileWatcherService watchers) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/codegraph/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession)
    public const string ServerName = "codegraph";

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? AllTools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Права — на КАЖДЫЙ вызов (гейт в составе недостаточен — см. шапку)
        if (!TryResolve(context, out var root, out var resolveError, out var projectless))
            return projectless
                // Как stdio-ветка: чат вне проекта — не отказ протокола, а честный текст
                ? new McpToolCallResult(resolveError)
                : Deny(resolveError);

        switch (tool)
        {
            case "codegraph_find":
            {
                var query = StringArg(arguments, "query").Trim();
                if (query.Length == 0) return Deny("Нужен параметр query");
                var limit = ClampLimit(IntArg(arguments, "limit"), 20);
                var result = await queries.FindAsync(root, query, limit, ct);
                return result is null
                    ? Building(root)
                    : Text(RenderFind(result, query, limit));
            }

            case "codegraph_neighbors":
            {
                var node = StringArg(arguments, "node").Trim();
                if (node.Length == 0) return Deny("Нужен параметр node");
                var limit = ClampLimit(IntArg(arguments, "limit"), 20);
                var outcome = await queries.NeighborsAsync(root, node,
                    OptionalArg(arguments, "direction"), OptionalArg(arguments, "relation"), limit, ct);
                if (!outcome.HasGraph) return Building(root);
                // «Узел не найден» — не сбой инструмента, а полезный ответ с кандидатами:
                // иначе модель пошла бы гадать имя вслепую (как stdio-ветка)
                if (outcome.Result is null)
                {
                    var message = $"Узел «{node}» в графе не найден — уточни имя через codegraph_find";
                    return Text(outcome.Candidates.Count > 0
                        ? $"{message}. Похожие узлы:\n"
                            + string.Join("\n", outcome.Candidates.Select(NodeLine))
                        : message);
                }
                return Text(RenderNeighbors(outcome.Result, limit));
            }

            case "codegraph_hubs":
            {
                var limit = ClampLimit(IntArg(arguments, "limit"), 10);
                var result = await queries.HubsAsync(root, limit, ct);
                return result is null
                    ? Building(root)
                    : Text(RenderHubs(result));
            }

            default:
                return Deny($"Неизвестный инструмент: {tool}");
        }
    }

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
    /// Резолв хвоста в РАБОЧЕЕ ДЕРЕВО сессии-вызывателя. Цепочка проверок на каждый вызов:
    /// хвост → сессия владельца токена → проект той же владельческой цепочки → живой гейт
    /// tool:codegraph (та же точка, что ServerToolEnabled в BuildCodeGraphContext) → дерево
    /// (WorktreePath сессии ?? корень проекта). Чужая сессия/проект — отказ; чат вне
    /// проекта — особый случай (projectless): не ошибка, а текст «граф недоступен», как
    /// у stdio-ветки.
    /// </summary>
    private bool TryResolve(McpToolCallContext context,
        out string root, out string? error, out bool projectless)
    {
        root = "";
        error = null;
        projectless = false;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера графа кода — вызов отклонён.";
            return false;
        }
        var session = sessions.GetOwned(sessionId, context.OwnerId);
        if (session is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к графу кода закрыт.";
            return false;
        }
        if (session.ProjectId is not { } projectId)
        {
            projectless = true;
            error = "Граф кода доступен только в чате проекта — текущий чат вне проекта.";
            return false;
        }
        var project = projects.GetById(projectId);
        if (project is null || project.OwnerId != context.OwnerId)
        {
            error = "Проект чата не найден или принадлежит другому владельцу — доступ к графу кода закрыт.";
            return false;
        }
        var persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        if (!bindings.ServerToolEnabled(context.OwnerId, persona, "codegraph"))
        {
            error = "Граф кода недоступен этой персоне (привязка tool:codegraph выключена). "
                + "Попроси пользователя включить её.";
            return false;
        }
        // Дерево сессии: отдельное worktree имеет СВОЙ граф (ADR-003). Watcher его файлов
        // поднимаем лениво — как CodeGraphController.ResolveRoot (первая дверь к графу
        // отдельного дерева). Резолв живой: смена WorktreePath подхватывается сама.
        if (session.WorktreePath is { Length: > 0 } worktree)
        {
            watchers.WatchPath("worktree:" + session.Id, worktree);
            root = worktree;
        }
        else
        {
            root = project.RootPath;
        }
        return true;
    }

    // «Графа нет» — фоновая постройка + честный текст (как stdio-ветка: 404 building)
    private McpToolCallResult Building(string root)
    {
        graphs.StartRebuildIfIdle(root);
        return Deny("Граф строится, повтори запрос через 1–2 минуты.");
    }

    private static int ClampLimit(int? value, int fallback) =>
        value is { } v && v > 0 ? Math.Min(v, 100) : fallback;

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name);
        return value.Length == 0 ? null : value;
    }

    private static int? IntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    // --- Компактный рендер ответов (порт stdio-ветки посимвольно) ---

    private static string NodeLine(CodeGraphNodeBriefDto node) =>
        $"{node.Fqn} [{node.Kind}] {(node.Location.Length > 0 ? node.Location : "?")} — {node.Degree} связей";

    private static string PluralFiles(int count)
    {
        var m10 = count % 10;
        var m100 = count % 100;
        if (m10 == 1 && m100 != 11) return "файл";
        if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return "файла";
        return "файлов";
    }

    // Хаб: метрика «используют N файлов» (уникальные файлы-импортёры) честнее сырого degree —
    // разворот «файл::*» надувает in-degree. У хаба без входящих — откат на обычную строку.
    private static string HubLine(CodeGraphNodeBriefDto node) =>
        node.Files is { } files && files > 0
            ? $"{node.Fqn} [{node.Kind}] {(node.Location.Length > 0 ? node.Location : "?")} — используют {files} {PluralFiles(files)}"
            : NodeLine(node);

    private static string StaleNote(bool isStale) =>
        isStale ? "\n(граф может быть устаревшим — файлы менялись после построения)" : "";

    // Пустой результат на устаревшем графе — не «ничего не найдено», а «проверено по старому
    // снимку»: иначе модель принимает пустоту за истину и не повторяет запрос.
    private static string StaleEmptyWarning() =>
        "⚠ Граф кода устарел и перестраивается фоном — поиск шёл по СТАРОМУ снимку.\n"
        + "Если тип обязан существовать (фронт только что добавлен и т.п.), повтори запрос через 1–2 минуты.";

    private static string RenderFind(CodeGraphFindResultDto result, string query, int limit)
    {
        var rows = result.Results;
        if (rows.Count == 0)
        {
            return result.IsStale
                ? $"{StaleEmptyWarning()}\nПо запросу «{query}» в старом снимке ничего не найдено."
                : $"По запросу «{query}» в графе кода ничего не найдено.";
        }
        var lines = rows.Select(NodeLine).ToList();
        var rest = result.Total - rows.Count;
        if (rest > 0) lines.Add($"… ещё {rest} (показаны первые {rows.Count}, limit={limit})");
        return $"Найдено {result.Total} по запросу «{query}»:\n{string.Join("\n", lines)}{StaleNote(result.IsStale)}";
    }

    private static string RenderNeighbors(CodeGraphNeighborsResultDto result, int limit)
    {
        var node = result.Node;
        var rows = result.Neighbors;
        var byRelation = string.Join(", ", result.ByRelation.Select(kv => $"{kv.Key} {kv.Value}"));
        var head = $"{node.Fqn} [{node.Kind}] {(node.Location.Length > 0 ? node.Location : "?")}\n"
            + $"Связей: {node.Degree} (входящих {result.TotalIn}, исходящих {result.TotalOut})"
            + (byRelation.Length > 0 ? $"; под фильтром {result.Total}: {byRelation}" : "");
        if (rows.Count == 0)
        {
            return result.IsStale
                ? $"{head}\n{StaleEmptyWarning()}\nПод фильтром связей в старом снимке нет."
                : $"{head}\nПод фильтром связей нет.";
        }
        // ← кто зависит от узла, → от чего зависит узел
        var lines = rows.Select(item =>
        {
            var arrow = item.Direction == "in" ? "←" : "→";
            var conf = item.Confidence == "Inferred" ? " (Inferred)" : "";
            var location = item.Location.Length > 0 ? "  " + item.Location : "";
            return $"{arrow} {item.Relation}{conf}  {item.Fqn}{location}";
        }).ToList();
        var rest = result.Total - rows.Count;
        if (rest > 0) lines.Add($"… ещё {rest} (показаны первые {rows.Count}, limit={limit})");
        return $"{head}\n{string.Join("\n", lines)}{StaleNote(result.IsStale)}";
    }

    private static string RenderHubs(CodeGraphHubsResultDto result)
    {
        var rows = result.Hubs;
        if (rows.Count == 0)
            return $"В графе кода нет связанных узлов (узлов {result.NodeCount}, рёбер {result.EdgeCount}).";
        var lines = rows.Select((node, i) => $"{i + 1}. {HubLine(node)}");
        return $"Хабы по связности (граф: {result.NodeCount} узлов, {result.EdgeCount} рёбер):\n"
            + string.Join("\n", lines) + StaleNote(result.IsStale);
    }

    // Полный состав: 3 чтения, схемы — порт mcp/codegraph-server/index.js
    // (источник контракта — здесь, index.js заморожен; сторож — CodeGraphNotificationsToolsetParityTests)
    internal static IReadOnlyList<McpToolSchema> AllTools { get; } =
    [
        new("codegraph_find",
            "Найти тип (класс/интерфейс/структуру/enum) в графе кода проекта по имени или части полного имени. "
            + "Отвечает на «где объявлен X» точнее Grep: возвращает файл со строкой, вид типа и степень связности "
            + "(сколько связей у типа). Ищи так, когда нужен именно тип, а не любое текстовое вхождение имени.",
            Obj(new JsonObject
            {
                ["query"] = Str("Имя типа («ServerMessage») или часть полного имени («Services.CodeGraph»)"),
                ["limit"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Сколько записей вернуть (1-100, по умолчанию 20)",
                    ["default"] = 20,
                },
            }, "query")),
        new("codegraph_neighbors",
            "Связи типа в графе кода: кто зависит от него (входящие) и от чего зависит он (исходящие), "
            + "с типом связи (Calls — вызовы, Implements — реализация/наследование, References — упоминание в полях, "
            + "параметрах, возвращаемых значениях) и уверенностью (Extracted — из символов Roslyn, Inferred — эвристика). "
            + "Отвечает на «кто зависит от X» с типизацией, которой Grep не даёт. Узел задаётся именем типа или полным именем.",
            Obj(new JsonObject
            {
                ["node"] = Str("Тип: имя («ServerMessage») или полное имя («ClaudeHomeServer.Protocol.ServerMessage»)"),
                ["direction"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("both", "in", "out"),
                    ["description"] = "in — кто зависит от типа; out — от чего зависит тип; both — обе стороны (по умолчанию)",
                    ["default"] = "both",
                },
                ["relation"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("Calls", "Implements", "References"),
                    ["description"] = "Оставить только связи этого типа (по умолчанию — все)",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Сколько связей вернуть (1-100, по умолчанию 20)",
                    ["default"] = 20,
                },
            }, "node")),
        new("codegraph_hubs",
            "Топ типов проекта по связности («god-узлы») — с чего начинать разбираться в незнакомом коде "
            + "и что ломается больнее всего при правке. Тот же список, что в системном промпте, но по запросу и любой длины.",
            Obj(new JsonObject
            {
                ["limit"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Сколько узлов вернуть (1-100, по умолчанию 10)",
                    ["default"] = 10,
                },
            })),
    ];

    // --- Хелперы схем (как у PersonasToolset.Schemas) ---

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
