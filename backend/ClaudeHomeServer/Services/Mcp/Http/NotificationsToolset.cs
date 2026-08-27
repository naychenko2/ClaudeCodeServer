using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Уведомления владельца (notifications_*) поверх HTTP-транспорта — переехавший с node
/// сервер (ADR-012, фаза 2 волна 3). Раньше это был mcp/notifications-server: тонкий
/// JSON-RPC-фасад, ходивший в наш же бэкенд сервисным JWT. Здесь вызовы идут напрямую
/// в NotificationService/NotificationStore — HTTP-хоп через собственный Kestrel не нужен.
///
/// Маршрут — <c>POST /mcp/notifications/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ,
/// по которой тулсет резолвит персону чата (роль env NOTIFICATIONS_SELF_PERSONA_ID —
/// лицо персоны на создаваемом уведомлении). Сессия обязана принадлежать владельцу
/// токена (GetOwned) — чужая это отказ и пустой tools/list (fail-closed).
/// Гейт NotificationsEnabled (привязка tool:notifications / модуль автоматизации по роли)
/// проверяется дважды — в составе (ToolsFor) и на вызове (defense-in-depth, урок приёмки
/// волны 2: гейт только в составе пропускал платный вызов при выключенной привязке).
///
/// Состав постоянный (4 инструмента) и от свойств хода не зависит (инвариант IMcpToolset).
/// Сторож парности со stdio-веткой отката — NotificationsToolsetParityTests (index.js
/// заморожен).
/// </summary>
public sealed class NotificationsToolset(
    NotificationStore store,
    NotificationService notif,
    SessionManager sessions,
    PersonaManager personas,
    PersonaBindingsService bindings) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/notifications/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession)
    public const string ServerName = "notifications";

    // Ответы — как у stdio-ветки (JSON.stringify): camelCase, кириллица без юникод-экранирования
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    // У параметризованного тулсета состава без хвоста не существует: контроллер на
    // /mcp/notifications без хвоста отвечает 404 до диспетчера
    public IReadOnlyList<McpToolSchema> Tools => [];

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _, out _) ? AllTools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Права проверяются на КАЖДЫЙ вызов (гейт в составе недостаточен — см. шапку)
        if (!TryResolve(context, out var session, out _, out var routeError))
            return Deny(routeError);

        switch (tool)
        {
            case "notifications_create":
            {
                var title = StringArg(arguments, "title").Trim();
                var body = StringArg(arguments, "body").Trim();
                if (title.Length == 0 || body.Length == 0)
                    return Deny("Нужны title и body — заголовок и текст уведомления");
                // Лицо персоны: как stdio-ветка — персона СЕССИИ подставляется, только если
                // модель не указала personaId явно. Резолв живой, из сессии на момент вызова
                var req = new CreateNotificationRequest
                {
                    Kind = StringArg(arguments, "kind") is { Length: > 0 } kind ? kind : "info",
                    Type = StringArg(arguments, "type") is { Length: > 0 } type ? type : "system",
                    Title = title,
                    Body = body,
                    Url = OptionalArg(arguments, "url"),
                    ProjectId = OptionalArg(arguments, "projectId"),
                    SessionId = OptionalArg(arguments, "sessionId"),
                    TaskId = OptionalArg(arguments, "taskId"),
                    Source = OptionalArg(arguments, "source"),
                    Tag = OptionalArg(arguments, "tag"),
                    PersonaId = OptionalArg(arguments, "personaId") ?? session.PersonaId,
                };
                // Push для важных — та же формула, что в NotificationsController.Create
                var sendPush = req.Kind is "reminder" or "claude" or "success";
                var id = await notif.SendAsync(context.OwnerId, req, sendPush);
                var item = await store.GetByIdAsync(context.OwnerId, id);
                return Json(item);
            }

            case "notifications_list":
            {
                var limit = Math.Clamp(IntArg(arguments, "limit") ?? 20, 1, 100);
                var offset = Math.Max(0, IntArg(arguments, "offset") ?? 0);
                var kind = StringArg(arguments, "kind");
                var unreadOnly = arguments["unreadOnly"] is JsonValue u
                    && u.TryGetValue<bool>(out var b) && b;
                var result = await store.GetListWithCountsAsync(context.OwnerId,
                    kind is { Length: > 0 } and not "all" ? kind : null,
                    unreadOnly ? true : null, limit, offset);
                return Json(result);
            }

            case "notifications_mark_read":
            {
                if (arguments["all"] is JsonValue a && a.TryGetValue<bool>(out var all) && all)
                {
                    var count = await store.MarkAllReadAsync(context.OwnerId);
                    return Json(new { marked = count });
                }
                var id = StringArg(arguments, "id");
                if (id.Length == 0) return Deny("Need id or all=true");
                return await store.MarkReadAsync(context.OwnerId, id)
                    ? Text("OK")
                    : Deny($"Уведомление {id} не найдено.");
            }

            case "notifications_delete":
            {
                var id = StringArg(arguments, "id");
                if (id.Length == 0) return Deny("Нужен id уведомления");
                return await store.DeleteAsync(context.OwnerId, id)
                    ? Text("OK")
                    : Deny($"Уведомление {id} не найдено.");
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
    /// Резолв хвоста в сессию ВЛАДЕЛЬЦА токена + живой гейт сервера уведомлений
    /// (PersonaBindingsService.NotificationsEnabled — та же точка, что решает подключение
    /// сервера в SessionManager.BuildNotificationsContext). Чужая сессия или выключенный
    /// сервер — отказ, а не пустой результат: права проверяются на каждый вызов.
    /// </summary>
    private bool TryResolve(McpToolCallContext context,
        out Session session, out Persona? persona,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        persona = null;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера уведомлений — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к уведомлениям закрыт.";
            return false;
        }
        session = owned;
        persona = session.PersonaId is { } pid ? personas.Get(pid, context.OwnerId) : null;
        if (!bindings.NotificationsEnabled(context.OwnerId, persona))
        {
            error = "Сервер уведомлений недоступен этой персоне (привязка tool:notifications выключена). "
                + "Попроси пользователя включить её.";
            return false;
        }
        error = null;
        return true;
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

    // Полный состав: 4 инструмента, схемы — порт mcp/notifications-server/index.js
    // (источник контракта — здесь, index.js заморожен; сторож — NotificationsToolsetParityTests)
    internal static IReadOnlyList<McpToolSchema> AllTools { get; } =
    [
        new("notifications_create",
            "Создать уведомление пользователю. Используй когда нужно привлечь внимание: задача выполнена, "
            + "персона ответила, готов дайджест, требуется действие. kind указывает иконку/цвет: "
            + "reminder (⏰) — Напоминания о задачах, сроках, событиях; claude (●) — Ответы агентов, "
            + "результаты задач, сообщения персон; info (ℹ) — Системные: дайджесты, саммари, конвейеры; "
            + "success (✓) — Успешное завершение: задача выполнена, процесс окончен; meeting (🏁) — "
            + "Совещания: завершены, готовы итоги. tag — краткая метка: Напоминание, Персона, Исполнитель, "
            + "Дайджест, Саммари, Совещание, Конвейер, Планировщик, Система. url — hash-диплинк для перехода по клику.",
            Obj(new JsonObject
            {
                ["title"] = Str("Заголовок уведомления (коротко, ёмко)"),
                ["body"] = Str("Текст уведомления (1-2 предложения)"),
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("reminder", "claude", "info", "success", "meeting"),
                    ["description"] = "Категория для иконки/цвета",
                    ["default"] = "info",
                },
                ["type"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Подтип для классификации: system, agent_reply, task_done, briefing, summary, meeting_complete, pipeline_complete, custom",
                    ["default"] = "system",
                },
                ["url"] = Str("Hash-диплинк для перехода: #/chats/{id}, #/project/{pid}/task/{tid}, #/notes/{nid}"),
                ["source"] = Str("Источник: название проекта, чата, персоны"),
                ["tag"] = Str("Краткая метка: Напоминание, Персона, Исполнитель, Дайджест, Саммари, Совещание, Конвейер, Планировщик, Система"),
                ["projectId"] = Str("ID проекта (если уведомление про проект)"),
                ["sessionId"] = Str("ID сессии/чата (для ссылки)"),
                ["taskId"] = Str("ID задачи (для ссылки)"),
            }, "title", "body")),
        new("notifications_list",
            "Получить список уведомлений пользователя с фильтрацией и пагинацией.",
            Obj(new JsonObject
            {
                ["limit"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Сколько вернуть (1-100)",
                    ["default"] = 20,
                },
                ["offset"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Смещение от начала",
                    ["default"] = 0,
                },
                ["kind"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = StrEnum("all", "reminder", "claude", "info", "success", "meeting"),
                    ["description"] = "Фильтр по категории",
                    ["default"] = "all",
                },
                ["unreadOnly"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Только непрочитанные",
                    ["default"] = false,
                },
            })),
        new("notifications_mark_read",
            "Отметить уведомление как прочитанное.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID уведомления"),
                ["all"] = new JsonObject
                {
                    ["type"] = "boolean",
                    ["description"] = "Отметить все как прочитанные",
                },
            })),
        new("notifications_delete",
            "Удалить уведомление.",
            Obj(new JsonObject
            {
                ["id"] = Str("ID уведомления"),
            }, "id")),
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
