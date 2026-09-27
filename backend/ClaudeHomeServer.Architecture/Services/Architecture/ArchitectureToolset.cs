using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Mcp.Http;

namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// C4-модель проекта (arch_*) поверх MCP-over-HTTP (ADR-012) — сервер, рождённый сразу в
/// Kestrel: stdio-ветки отката нет, как у watch/websearch. Работает поверх того же файла
/// и того же хранилища, что раздел «Архитектура» (<see cref="ArchitectureModelStore"/>):
/// запись — со сверкой версии, чужая правка между чтением и записью — честный конфликт,
/// а не молчаливое затирание.
///
/// Маршрут — <c>POST /mcp/architecture/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЯ
/// (образец волны 2–3 — codegraph), по ней тулсет резолвит проект чата. Файл модели — в
/// корне ПРОЕКТА, а не worktree сессии: раздел читает именно его, иначе правка персоны
/// была бы в разделе не видна.
///
/// Изоляция: сессия из хвоста обязана принадлежать владельцу токена, проект — тому же
/// владельцу. Гейт состава (свойство персоны сессии, не хода) — Off-привязка персоны
/// <c>tool:architecture</c>; фич-флага нет, раздел включается конфигом модуля. Проверяется и в
/// составе, и на каждом вызове (defense-in-depth, урок приёмки волны 2). Персона с профилем
/// «Только чтение» пишущие инструменты видит (состав постоянный), но вызов отклоняется —
/// рядом стоит и запрет CLI (PersonaAccessPolicy).
///
/// DelegatedTurnGate не ставится: правка модели — не делегирование и не рассылка (как у
/// заметок и сторожей); гейт делегирования защищает от каскадов задач/сообщений.
/// </summary>
public sealed class ArchitectureToolset(
    ArchitectureModelStore store,
    IMcpSessionAccessor sessions,
    IProjectManager projects,
    IPersonaResolver personas,
    IPersonaServerToolGate toolGate) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.ArchitectureName;

    /// <summary>Tool-ключ Off-привязки персоны (та же строка, что в SessionManager).</summary>
    public const string ToolKey = "architecture";

    /// <summary>Инструменты, меняющие файл модели — их режет профиль «Только чтение».</summary>
    public static readonly IReadOnlySet<string> WriteTools =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "arch_create_element", "arch_update_element", "arch_delete_element", "arch_set_connection",
        };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? AllTools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolve(context, out var project, out var persona, out var error))
            return Deny(error);
        if (WriteTools.Contains(tool) && persona?.Access == PersonaAccess.ReadOnly)
            return Deny("Правка модели недоступна персоне с профилем «Только чтение».");

        var snapshot = await store.ReadAsync(project.RootPath, ct);
        // Файла нет: создание заводит пустую модель генераторской формы и кладёт в неё первый
        // элемент; запись идёт с baseVersion = null, поэтому второй «первый» создатель получит
        // конфликт, а не затрёт модель соседа. Остальным инструментам читать нечего.
        if (snapshot.Content is null && tool != "arch_create_element")
            return Deny("Модели архитектуры у проекта ещё нет — её собирают кнопкой «Собрать из кода» "
                + "в разделе «Архитектура» (или файлом " + ArchitectureModelGenerator.ModelRelPath + "), "
                + "либо заводят с нуля: arch_create_element с level=system.");

        try
        {
            var doc = snapshot.Content is null
                ? ArchitectureModelEditor.NewDocument()
                : ArchitectureModelEditor.Parse(snapshot.Content);
            switch (tool)
            {
                case "arch_context":
                    return Text($"Модель проекта «{project.Name}», version={snapshot.Version}"
                        + AuthorNote(snapshot.Author) + "\n"
                        + ArchitectureModelEditor.RenderContext(doc));

                case "arch_search":
                {
                    var query = StringArg(arguments, "query");
                    var level = OptionalArg(arguments, "level");
                    var limit = IntArg(arguments, "limit") is { } l && l > 0 ? Math.Min(l, 100) : 20;
                    var found = ArchitectureModelEditor.Search(doc, query, level, limit);
                    return Text(found.Count == 0
                        ? $"По запросу «{query}» элементов не найдено."
                        : $"Найдено {found.Count} (version={snapshot.Version}):\n"
                            + ArchitectureModelEditor.RenderSearch(doc, found));
                }

                case "arch_get_element":
                {
                    var id = StringArg(arguments, "id").Trim();
                    if (id.Length == 0) return Deny("Нужен параметр id");
                    var element = ArchitectureModelEditor.Find(doc, id);
                    return element is null
                        ? Deny($"Элемент «{id}» в модели не найден — возьми id из arch_search или arch_context.")
                        : Text($"version={snapshot.Version}\n" + ArchitectureModelEditor.RenderElement(doc, element));
                }

                case "arch_create_element":
                {
                    var level = StringArg(arguments, "level").Trim();
                    var name = StringArg(arguments, "name").Trim();
                    if (level.Length == 0 || name.Length == 0) return Deny("Нужны параметры level и name");
                    if (snapshot.Content is null && level != "system")
                        return Deny("Модели архитектуры у проекта ещё нет, родителю взяться неоткуда — "
                            + "первым создай элемент level=system, потом вкладывай в него остальное.");
                    if (VersionMismatch(arguments, snapshot) is { } stale) return stale;
                    var created = ArchitectureModelEditor.CreateElement(doc, new ArchitectureElementDraft(
                        Level: level,
                        Name: name,
                        ParentId: OptionalArg(arguments, "parentId"),
                        Description: OptionalRaw(arguments, "description"),
                        Technology: OptionalRaw(arguments, "technology"),
                        Url: OptionalRaw(arguments, "url"),
                        Tags: TagsArg(arguments),
                        External: BoolArg(arguments, "external") ?? false));
                    return await SaveAsync(project, persona, doc, snapshot,
                        $"Элемент «{created.Name}» [{created.Level}] создан, id={created.Id}.", ct);
                }

                case "arch_delete_element":
                {
                    var id = StringArg(arguments, "id").Trim();
                    if (id.Length == 0) return Deny("Нужен параметр id");
                    if (VersionMismatch(arguments, snapshot) is { } stale) return stale;
                    var removed = ArchitectureModelEditor.DeleteElement(doc, id,
                        cascade: BoolArg(arguments, "cascade") ?? false);
                    var nested = removed.Elements.Count - 1;
                    var names = string.Join(", ", removed.Elements.Take(10).Select(e => $"«{e.Name}» ({e.Id})"))
                        + (removed.Elements.Count > 10 ? $" и ещё {removed.Elements.Count - 10}" : "");
                    return await SaveAsync(project, persona, doc, snapshot,
                        $"Удалено элементов: {removed.Elements.Count} (сам и вложенных: {nested}), "
                        + $"связей: {removed.Connections}, документов: {removed.Documents}. Удалены: {names}.", ct);
                }

                case "arch_update_element":
                {
                    var id = StringArg(arguments, "id").Trim();
                    if (id.Length == 0) return Deny("Нужен параметр id");
                    if (VersionMismatch(arguments, snapshot) is { } stale) return stale;
                    var patch = new ArchitectureElementPatch(
                        Name: OptionalRaw(arguments, "name"),
                        Description: OptionalRaw(arguments, "description"),
                        Technology: OptionalRaw(arguments, "technology"),
                        Url: OptionalRaw(arguments, "url"),
                        Tags: TagsArg(arguments),
                        External: BoolArg(arguments, "external"));
                    var changed = ArchitectureModelEditor.UpdateElement(doc, id, patch);
                    if (changed.Count == 0)
                        return Text($"Элемент «{id}» уже в таком виде — модель не менялась (version={snapshot.Version}).");
                    return await SaveAsync(project, persona, doc, snapshot,
                        $"Элемент «{id}» обновлён: {string.Join(", ", changed)}.", ct);
                }

                case "arch_set_connection":
                {
                    var source = StringArg(arguments, "sourceId").Trim();
                    var target = StringArg(arguments, "targetId").Trim();
                    if (source.Length == 0 || target.Length == 0) return Deny("Нужны параметры sourceId и targetId");
                    if (VersionMismatch(arguments, snapshot) is { } stale) return stale;
                    string summary;
                    if (arguments["remove"] is JsonValue rv && rv.TryGetValue<bool>(out var remove) && remove)
                    {
                        if (!ArchitectureModelEditor.RemoveConnection(doc, source, target))
                            return Text($"Связи {source} → {target} в модели нет — удалять нечего.");
                        summary = $"Связь {source} → {target} удалена.";
                    }
                    else
                    {
                        var outcome = ArchitectureModelEditor.UpsertConnection(doc, source, target,
                            new ArchitectureConnectionPatch(
                                Label: OptionalRaw(arguments, "label"),
                                Technology: OptionalRaw(arguments, "technology"),
                                Description: OptionalRaw(arguments, "description")));
                        if (outcome == "unchanged")
                            return Text($"Связь {source} → {target} уже в таком виде — модель не менялась (version={snapshot.Version}).");
                        summary = outcome == "created"
                            ? $"Связь {source} → {target} создана."
                            : $"Связь {source} → {target} обновлена.";
                    }
                    return await SaveAsync(project, persona, doc, snapshot, summary, ct);
                }

                default:
                    return Deny($"Неизвестный инструмент: {tool}");
            }
        }
        catch (ArchitectureEditException ex)
        {
            return Deny(ex.Message);
        }
        catch (ArchitectureModelInvalidException ex)
        {
            return Deny("Модель не сохранена: " + ex.Message);
        }
    }

    // Запись — через общее хранилище со сверкой версии: между нашим чтением и записью файл
    // могли поменять раздел (другая вкладка) или «Собрать из кода» — тогда честный конфликт
    private async Task<McpToolCallResult> SaveAsync(Project project, Persona? persona, JsonObject doc,
        ArchitectureModelSnapshot basis, string summary, CancellationToken ct)
    {
        var author = persona?.Name is { Length: > 0 } name ? name : "Claude";
        var outcome = await store.WriteAsync(project.RootPath, ArchitectureModelEditor.Serialize(doc),
            basis.Version, author, ct);
        return outcome.Saved
            ? Text($"{summary} Сохранено, version={outcome.Current.Version}. "
                + "Раздел «Архитектура» покажет правку после перезагрузки модели.")
            : Conflict(outcome.Current);
    }

    // Модель передала version (из arch_context/arch_get_element) — правка опирается на то,
    // что она видела; файл успели поменять → отказ, а не правка вслепую поверх чужой.
    // Файла нет — сверять не с чем: конфликт с пустой «текущей version» только сбивал бы
    // агента, а появление файла до записи честно поймает сверка в хранилище
    private static McpToolCallResult? VersionMismatch(JsonObject arguments, ArchitectureModelSnapshot snapshot) =>
        snapshot.Content is not null
            && OptionalArg(arguments, "version") is { } expected
            && !string.Equals(expected, snapshot.Version, StringComparison.OrdinalIgnoreCase)
            ? Conflict(snapshot)
            : null;

    private static McpToolCallResult Conflict(ArchitectureModelSnapshot current) =>
        Deny($"Конфликт версий: модель успели изменить{AuthorNote(current.Author)}, текущая version={current.Version}. "
            + "Перечитай элемент (arch_get_element) и повтори правку.");

    private static string AuthorNote(ArchitectureModelAuthor author) =>
        author.UpdatedAt is { } at
            ? $" (обновлена {at:yyyy-MM-dd HH:mm} UTC{(author.UpdatedBy is { } by ? ", " + by : "")})"
            : "";

    /// <summary>
    /// Хвост → сессия владельца токена → проект той же владельческой цепочки → флаг
    /// владельца → Off-привязка персоны. Всё — свойства владельца и сессии, не хода.
    /// </summary>
    private bool TryResolve(McpToolCallContext context, [NotNullWhen(true)] out Project? project,
        out Persona? persona, [NotNullWhen(false)] out string? error)
    {
        project = null;
        persona = null;
        error = null;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера архитектуры — вызов отклонён.";
            return false;
        }
        var session = sessions.GetOwned(sessionId, context.OwnerId);
        if (session is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к модели закрыт.";
            return false;
        }
        if (session.ProjectId is not { } projectId)
        {
            error = "Модель архитектуры доступна только в чате проекта — текущий чат вне проекта.";
            return false;
        }
        var found = projects.GetById(projectId);
        if (found is null || found.OwnerId != context.OwnerId)
        {
            error = "Проект чата не найден или принадлежит другому владельцу — доступ к модели закрыт.";
            return false;
        }
        persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        if (!toolGate.IsServerToolEnabled(context.OwnerId, persona, ToolKey))
        {
            error = "Модель архитектуры недоступна этой персоне (привязка tool:architecture выключена). "
                + "Попроси пользователя включить её.";
            return false;
        }
        project = found;
        return true;
    }

    // Один сегмент — id сессии (форма как у codegraph: хвост строим мы, но он едет в URL)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Contains('/')) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static string? OptionalArg(JsonObject arguments, string name)
    {
        var value = StringArg(arguments, name).Trim();
        return value.Length == 0 ? null : value;
    }

    // Поле правки: отсутствует — null («не трогать»), пустая строка — «очистить»
    private static string? OptionalRaw(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? IntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool? BoolArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    // Теги: отсутствуют — null («не трогать»), [] — «снять все»
    private static List<string>? TagsArg(JsonObject arguments) =>
        arguments["tags"] is JsonArray tags
            ? tags.Select(t => t is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
                .OfType<string>().ToList()
            : null;

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    // Состав постоянный (3 чтения + 4 записи): зависит только от свойств владельца/сессии
    internal static IReadOnlyList<McpToolSchema> AllTools { get; } =
    [
        new("arch_context",
            "Сводка C4-модели архитектуры проекта (раздел «Архитектура», файл "
            + ArchitectureModelGenerator.ModelRelPath + "): счётчики и дерево «система → контейнеры → "
            + "компоненты» с id элементов и текущая version модели. Начинай с неё, чтобы понять устройство проекта.",
            Obj(new JsonObject())),
        new("arch_search",
            "Найти элементы C4-модели по подстроке в имени, описании, технологии, тегах или id. "
            + "Возвращает id — по нему читай элемент (arch_get_element) и правь его.",
            Obj(new JsonObject
            {
                ["query"] = Str("Подстрока поиска (без учёта регистра)"),
                ["level"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("system", "container", "component", "code"),
                    ["description"] = "Оставить только элементы этого уровня C4",
                },
                ["limit"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Сколько элементов вернуть (1-100, по умолчанию 20)",
                    ["default"] = 20,
                },
            }, "query")),
        new("arch_get_element",
            "Элемент C4-модели целиком: поля, путь в иерархии, вложенные элементы, входящие и исходящие "
            + "связи и прикреплённые документы (полный markdown). Отдаёт version модели для последующей правки.",
            Obj(new JsonObject
            {
                ["id"] = Str("id элемента (из arch_search или arch_context)"),
            }, "id")),
        new("arch_create_element",
            "Создать элемент C4-модели. Родитель строго на ступень выше: контейнер — в системе, компонент — "
            + "в контейнере, код — в компоненте; у системы родителя нет. Встаёт в первую свободную клетку "
            + "у родителя; имя, уже занятое у того же родителя, — отказ. Если модели у проекта ещё нет, "
            + "первым создаётся system — файл модели заведётся сам, дальше вкладывай в неё контейнеры. "
            + "Возвращает id нового элемента и новую version. Передавай version из последнего чтения.",
            Obj(new JsonObject
            {
                ["level"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("system", "container", "component", "code"),
                    ["description"] = "Уровень C4 нового элемента",
                },
                ["name"] = Str("Имя элемента"),
                ["parentId"] = Str("id родителя уровнем выше (обязателен для всех, кроме system)"),
                ["version"] = Str("version модели из arch_context/arch_get_element (рекомендуется)"),
                ["description"] = Str($"Короткое описание карточки (до {ArchitectureModelEditor.DescriptionMax} символов)"),
                ["technology"] = Str("Технология (например «ASP.NET Core», «PostgreSQL»)"),
                ["url"] = Str("Ссылка элемента"),
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = $"Теги (до {ArchitectureModelEditor.MaxTags})",
                },
                ["external"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Внешний элемент (вне границ системы)",
                },
            }, "level", "name")),
        new("arch_update_element",
            "Изменить поля элемента C4-модели. Меняются только переданные поля; пустая строка очищает поле. "
            + "Позиция на схеме, документы и прочие поля остаются как были. Передавай version из последнего "
            + "чтения — если модель успели изменить, правка отклонится конфликтом, а не затрёт чужую.",
            Obj(new JsonObject
            {
                ["id"] = Str("id элемента"),
                ["version"] = Str("version модели из arch_context/arch_get_element (рекомендуется)"),
                ["name"] = Str("Новое имя"),
                ["description"] = Str($"Короткое описание карточки (до {ArchitectureModelEditor.DescriptionMax} символов)"),
                ["technology"] = Str("Технология (например «ASP.NET Core», «PostgreSQL»)"),
                ["url"] = Str("Ссылка элемента"),
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = $"Теги целиком, заменяют прежние (до {ArchitectureModelEditor.MaxTags}); [] — снять все",
                },
                ["external"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Внешний элемент (вне границ системы)",
                },
            }, "id")),
        new("arch_delete_element",
            "Удалить элемент C4-модели вместе со всеми вложенными, их связями (входящими и исходящими) и "
            + "прикреплёнными документами. Элемент любого уровня с вложенными (система с контейнерами, "
            + "контейнер с компонентами…) удаляется только с cascade=true — без него отказ с перечнем "
            + "вложенных. В ответе — сколько элементов, связей и документов удалено. "
            + "Передавай version из последнего чтения.",
            Obj(new JsonObject
            {
                ["id"] = Str("id удаляемого элемента"),
                ["version"] = Str("version модели из arch_context/arch_get_element (рекомендуется)"),
                ["cascade"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "true — подтвердить удаление элемента вместе со всем, что в нём лежит",
                },
            }, "id")),
        new("arch_set_connection",
            "Создать, изменить или удалить связь между элементами C4-модели (одна связь на пару "
            + "источник → цель). Без remove — создаёт связь или правит переданные поля существующей; "
            + "remove=true — удаляет. Передавай version из последнего чтения.",
            Obj(new JsonObject
            {
                ["sourceId"] = Str("id элемента-источника"),
                ["targetId"] = Str("id элемента-цели"),
                ["version"] = Str("version модели из arch_context/arch_get_element (рекомендуется)"),
                ["label"] = Str("Подпись связи (что происходит: «читает заказы», «шлёт события»)"),
                ["technology"] = Str("Протокол/технология связи (HTTP, gRPC, Kafka…)"),
                ["description"] = Str("Описание связи"),
                ["remove"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "true — удалить связь",
                },
            }, "sourceId", "targetId")),
    ];

    private static JsonArray StrEnum(params string[] values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static JsonObject Str(string description) =>
        new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Obj(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject { ["type"] = "object" };
        if (required.Length > 0) schema["required"] = StrEnum(required);
        schema["properties"] = properties;
        return schema;
    }
}
