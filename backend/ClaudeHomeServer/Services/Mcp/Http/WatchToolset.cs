using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Watchdog;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Серверные сторожа чатов (watch_*) поверх HTTP-транспорта (ADR-013, по форме —
/// ADR-012 волна 2). Модель декларирует «дожидайся условия и разбуди этот чат»: цикл
/// опроса живёт в WatchdogService и переживает ходы, рестарты и смерть процесса CLI.
///
/// Маршрут — <c>POST /mcp/watch/{sessionId}</c>: хвост несёт СЕССИЮ-ВЫЗЫВАТЕЛЬ, по ней
/// тулсет знает владельца, проект (WorkingDirectory опроса) и будимый чат. Сторож будит
/// ТОЛЬКО чат-постановщика (будильник чужому чату — «не входим» плана). Имя тулсета —
/// сознательно не «monitor»: правило именования запрещает однокоренные имена с
/// пересекающейся семантикой (Monitor CLI уже есть в том же tools/list).
///
/// DelegatedTurnGate НЕ ставится (решение плана): гейт в образце (tasks) стоит только на
/// тул, ЗАПУСКАЮЩИЙ ход (tasks_run_executor); watch_start ход не запускает, а гейт отрезал
/// бы полезный кейс «агент-исполнитель сторожит своё условие».
///
/// ИНВАРИАНТ состава (IMcpToolset): tools/list зависит только от сессии-вызывателя
/// (свойство сессии — разрешено ADR-012), от свойств хода не зависит.
/// stdio-ветки отката НЕТ (node-сервера никогда не существовало): при
/// Mcp:HttpTransport=false тулсет недоступен — осознанное упрощение.
/// </summary>
public sealed class WatchToolset(
    WatchdogStore store,
    SessionManager sessions) : IMcpParameterizedToolset
{
    // Имя сервера = первый сегмент маршрута POST /mcp/watch/{sessionId}. Константа —
    // единственная точка правды для URL конфига хода (ClaudeSession)
    public const string ServerName = "watch";

    // Ответы — как у соседних тулсетов (JSON.stringify): camelCase, кириллица без \u
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Name => ServerName;
    public string Version => "1.0.0";

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context) =>
        TryResolve(context, out _, out _) ? Tools : [];

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        // Право на чат — внутри TryResolve, на каждый вызов: чужая сессия не видит
        // ни состава, ни инструментов
        if (!TryResolve(context, out var session, out var error))
            return Deny(error!);

        switch (tool)
        {
            case "watch_start":
            {
                var name = StringArg(arguments, "name");
                var command = StringArg(arguments, "poll_command");
                var created = store.Create(context.OwnerId, session.Id, session.ProjectId,
                    name, command,
                    NullableIntArg(arguments, "interval_seconds"),
                    NullableIntArg(arguments, "timeout_minutes"),
                    out var createError);
                if (created is null) return Deny(createError!);
                return Json(new
                {
                    created.Id,
                    created.Name,
                    created.IntervalSeconds,
                    created.TimeoutMinutes,
                    note = "Сторож поставлен: сервер опрашивает условие фоном, переживая ходы и рестарты. "
                        + "При выполнении (или истечении потолка жизни) чат получит сообщение-будильник. "
                        + "Статус — watch_list, снятие — watch_cancel.",
                });
            }

            case "watch_list":
                return Json(store.GetBySession(session.Id).Select(Brief).ToList());

            case "watch_cancel":
            {
                var id = StringArg(arguments, "watch_id");
                // Идущий poll убивается немедленно: стор эмитит ActiveCancelled, сервис
                // отменяет per-сторож токен, раннер Kill'ит процесс. Статус уже Cancelled —
                // исход опроса не перезапишет его (guard в WatchdogService.PollOneAsync)
                if (store.Cancel(id, context.OwnerId, out var cancelError) is null)
                    return Deny(cancelError ?? $"Сторож {id} не найден.");
                return Text($"Сторож {id} снят.");
            }

            default:
                throw new ArgumentException($"Неизвестный инструмент: {tool}", nameof(tool));
        }
    }

    // Компактное представление сторожа для watch_list
    private static object Brief(WatchdogRecord w) => new
    {
        w.Id, w.Name, w.Status,
        w.IntervalSeconds, w.TimeoutMinutes,
        w.LastPollAt, w.FiredAt,
        // Признак недоставленного будильника: терминальный сторож, чей будильник не дошёл
        // после всех ретраев (отдельного статуса нет — план, недоставка флагом)
        undelivered = w.Status is not (WatchdogStatus.Active or WatchdogStatus.Cancelled)
            && w.DeliveredAt is null,
        w.LastOutput,
    };

    // --- Маршрут: /mcp/watch/{sessionId} ---

    /// <summary>URL эндпоинта в конфиге хода: базовый адрес + маршрут тулсета с хвостом.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpHttpTransport.EndpointFor(apiUrl, ServerName) + "/" + sessionId;

    // Один сегмент — id сессии; форма как у тулсетов волны 2 (белый список resumeSessionId)
    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128 || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    // Резолв хвоста в сессию ВЛАДЕЛЬЦА токена (свойство сессии — составу tools/list
    // зависеть от хода нельзя, ADR-012). Любой отказ — текстом: право на чат проверяется
    // на КАЖДЫЙ вызов и tools/list.
    private bool TryResolve(McpToolCallContext context,
        out Session session, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error)
    {
        session = null!;
        error = null;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
        {
            error = "Некорректный маршрут сервера сторожей — вызов отклонён.";
            return false;
        }
        var owned = sessions.GetOwned(sessionId, context.OwnerId);
        if (owned is null)
        {
            error = "Чат-вызыватель не найден или принадлежит другому владельцу — доступ к сторожам закрыт.";
            return false;
        }
        session = owned;
        return true;
    }

    // --- Ответы и аргументы ---

    private static McpToolCallResult Text(string text) => new(text);

    private static McpToolCallResult Json<T>(T value) =>
        new(JsonSerializer.Serialize(value, JsonOpts));

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);

    private static string StringArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static int? NullableIntArg(JsonObject arguments, string name) =>
        arguments[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static readonly System.Lazy<IReadOnlyList<McpToolSchema>> _tools =
        new(() => [.. Schemas()]);

    internal static IReadOnlyList<McpToolSchema> Tools => _tools.Value;

    private static IEnumerable<McpToolSchema> Schemas()
    {
        yield return Tool("watch_start",
            "Поставить серверного сторожа: раз в интервал сервер выполнит poll_command " +
            "в рабочем каталоге проекта чата; код возврата 0 — условие выполнено, любое другое — «ещё нет». " +
            "Выполнилось (или истёк потолок жизни) — этот чат получит сообщение-будильник. " +
            "Для долгих ожиданий, переживающих ход: Monitor и фоновые задачи живут внутри процесса CLI и умирают вместе с ним. " +
            "Короткие ожидания в пределах хода оставь Monitor'у.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "name", "poll_command" },
                ["properties"] = new JsonObject
                {
                    ["name"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Короткое имя сторожа (узнается в будильнике)",
                    },
                    ["poll_command"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Команда опроса: exit 0 = дождались, exit != 0 = ещё нет " +
                            "(не завершает сторожа). Один запуск ограничен 60 секундами",
                    },
                    ["interval_seconds"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = WatchdogLimits.MinIntervalSeconds,
                        ["maximum"] = WatchdogLimits.MaxIntervalSeconds,
                        ["description"] = "Период между запусками, сек (30–600, по умолчанию 60)",
                    },
                    ["timeout_minutes"] = new JsonObject
                    {
                        ["type"] = "integer",
                        ["minimum"] = 1,
                        ["maximum"] = WatchdogLimits.MaxTimeoutMinutes,
                        ["description"] = "Потолок жизни сторожа, мин (по умолчанию 240, максимум 1440); " +
                            "истёк — будильник «не дождались»",
                    },
                },
            });

        yield return Tool("watch_list",
            "Сторожа этого чата: статус, последний опрос и признак недоставленного будильника " +
            "(undelivered=true — терминальный сторож, чьё сообщение не дошло).",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() });

        yield return Tool("watch_cancel",
            "Снять сторожа по id (из watch_list). Идущий прямо сейчас опрос будет прерван немедленно.",
            new JsonObject
            {
                ["type"] = "object",
                ["required"] = new JsonArray { "watch_id" },
                ["properties"] = new JsonObject
                {
                    ["watch_id"] = new JsonObject { ["type"] = "string", ["description"] = "ID сторожа" },
                },
            });
    }

    private static McpToolSchema Tool(string name, string description, JsonObject schema) =>
        new(name, description, schema);
}
