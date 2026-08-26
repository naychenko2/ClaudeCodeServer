using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeHomeServer.Controllers;

/// <summary>
/// MCP-over-HTTP: продуктовые серверы, живущие внутри Kestrel вместо процесса node (ADR-012).
/// Один маршрут на все тулсеты — <c>POST /mcp/{name}</c>, JSON-RPC 2.0 в теле запроса.
///
/// Условия транспорта проверены разведкой фазы 0 и обязательны:
/// - только http на loopback (адрес даёт SessionManager.ResolveTasksApiUrl, он предпочитает
///   http-адрес Kestrel): по https с боевым сертом CLI упирается в ERR_TLS_CERT_ALTNAME_INVALID
///   и МОЛЧА прячет инструменты от модели;
/// - авторизация — обычный заголовок Authorization, туда ложится сервисный JWT владельца
///   (как у fal-ai/glif), поэтому здесь стандартный [Authorize] и владелец из claims;
/// - SSE не нужен: GET по маршруту отдаёт 405 (роутинг сам, метод не разрешён) — CLI это переживает;
/// - нестандартный server/discover, которым CLI зондирует сервер перед initialize, получает
///   -32601 и на работу не влияет.
/// </summary>
[ApiController]
[Route("mcp")]
[Authorize]
public sealed class McpTransportController(McpToolsetRegistry registry,
    ILogger<McpTransportController> logger) : ControllerBase
{
    // Потолок тела запроса: аргументы инструментов несопоставимо меньше (html виджета ≤64 КБ,
    // 1 МБ — запас на фазу 2), а дефолт Kestrel в 30 МБ ReadToEndAsync прочитает в память целиком
    private const long MaxBodyBytes = 1024 * 1024;

    // Потолок элементов батча JSON-RPC: ответ собирается DeepClone'ами схем инструментов,
    // и батч из сотен тысяч мелких запросов (укладывается в 30 МБ) строил бы гигабайтную
    // строку ответа — OOM гасит ходы всех пользователей инстанса
    private const int MaxBatchItems = 100;

    /// <summary>
    /// SSE-канал не реализован: клиент пробует GET на транспорт и спокойно живёт с 405
    /// (проверено живым CLI). Экшен нужен ради пометки «это не вызов инструмента» — иначе
    /// штатная проба на каждом ходу оседала бы отказом в GET /api/mcp/calls.
    /// </summary>
    [HttpGet("{name}")]
    public IActionResult NoSse(string name)
    {
        HttpContext.Items[Services.Mcp.McpCallLog.SkipItemKey] = true;
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    [HttpPost("{name}")]
    [RequestSizeLimit(MaxBodyBytes)] // тело читается в память целиком — см. MaxBodyBytes
    public async Task<IActionResult> Handle(string name, CancellationToken ct)
    {
        var toolset = registry.Find(name);
        if (toolset is null) return NotFound(new { error = "unknown_mcp_server", message = $"Нет MCP-сервера «{name}»" });

        // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
        var ownerId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
        var context = new McpToolCallContext(ownerId,
            Request.Headers.TryGetValue("X-Caller-Session-Id", out var caller) ? caller.ToString() : null);

        // Тело разбираем вручную, без [FromBody]: авто-400 [ApiController] на кривом JSON отдавал
        // problem+json ВМЕСТО кода -32700 и ложился в журнал MCP отказом инструмента, не доходя
        // до контроллера. Пустое тело — -32600, нераскрываемый JSON — -32700 (JSON-RPC 2.0)
        string raw;
        using (var reader = new StreamReader(Request.Body, System.Text.Encoding.UTF8))
            raw = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(raw))
            return JsonRpc(Error(null, -32600, "Пустое тело запроса"));
        JsonNode? body;
        try { body = JsonNode.Parse(raw); }
        catch (System.Text.Json.JsonException) { return JsonRpc(Error(null, -32700, "Тело не читается как JSON")); }

        // Батч JSON-RPC (CLI не шлёт, но спецификация допускает): пустой батч и невалидные
        // элементы — ошибка -32600, а не молчаливый пропуск. Имя для журнала — одно на батч:
        // HTTP-запрос один, заголовок один, элемент внутри не отличить
        if (body is JsonArray batch)
        {
            NameCallForLog(toolset, method: "batch");
            if (batch.Count == 0)
                return JsonRpc(Error(null, -32600, "Пустой батч JSON-RPC"));
            // Потолок — ДО входа в цикл: каждый элемент обрабатывается с DeepClone'ом схемы,
            // и oversized-батч обязан отваливаться сразу, не надувая память ответом
            if (batch.Count > MaxBatchItems)
                return JsonRpc(Error(null, -32600,
                    $"Батч JSON-RPC слишком велик: {batch.Count} элементов (потолок {MaxBatchItems})"));
            var answers = new JsonArray();
            foreach (var item in batch)
            {
                if (item is not JsonObject msg)
                {
                    answers.Add(Error(null, -32600, "Элемент батча не является объектом JSON-RPC"));
                    continue;
                }
                if (await DispatchAsync(toolset, msg, context, ct) is { } answer) answers.Add(answer);
            }
            // Батч из одних уведомлений — отвечать нечем (спецификация: 202 без тела)
            return answers.Count == 0 ? Accepted() : JsonRpc(answers);
        }

        if (body is not JsonObject request)
            return JsonRpc(Error(null, -32600, "Ожидался объект JSON-RPC"));

        NameCallForLog(toolset, request);
        var result = await DispatchAsync(toolset, request, context, ct);
        // Уведомление (без id) ответа не имеет — CLI шлёт notifications/initialized сразу
        // после рукопожатия и ждёт именно 202, а не тело с null-id
        return result is null ? Accepted() : JsonRpc(result);
    }

    /// <summary>
    /// Промах мимо шаблона <c>mcp/{name}</c> — вложенные пути вроде <c>/mcp/a/b</c>. Без этого
    /// экшена запрос уходит в SPA-фолбэк и получает 200 с index.html: клиент JSON-RPC обязан
    /// видеть честный 404, а не HTML-страницу. Верб-ограничений нет — фолбэк ловит любой метод.
    /// Совпадает только то, что не легло в более специфичные шаблоны ({name} предпочтительнее).
    /// </summary>
    [Route("{*path}")]
    public IActionResult RouteMiss()
    {
        // Промах маршрута — не вызов инструмента: отказу в таблице GET /api/mcp/calls
        // и алерту 04-mcp-errors он не место (как у пробы SSE выше)
        HttpContext.Items[Services.Mcp.McpCallLog.SkipItemKey] = true;
        return NotFound(new { error = "mcp_route_not_found", message = "Неизвестный маршрут MCP" });
    }

    private async Task<JsonObject?> DispatchAsync(IMcpToolset toolset, JsonObject request,
        McpToolCallContext context, CancellationToken ct)
    {
        var method = request["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        var id = request["id"]?.DeepClone();
        if (method is null) return id is null ? null : Error(id, -32600, "Не указан method");
        // Уведомления (id нет) отрабатываем молча: ответ на них — нарушение протокола
        if (id is null) return null;

        // Спецификация JSON-RPC допускает ПОЗИЦИОННЫЕ params (массив/скаляр), а индексатор
        // JsonNode[string] на таком бросает InvalidOperationException. Мы работаем только с
        // именованными: не-объект — протокольная ошибка -32602, а не упавший диспетчер
        // (голый 500 на initialize снял бы у клиента MCP ВЕСЬ набор инструментов сервера до
        // конца жизни процесса CLI — тот самый молчаливый отказ, против которого fail-closed)
        var rawParms = request["params"];
        if (rawParms is not null and not JsonObject)
            return Error(id, -32602, "params должен быть объектом — позиционные аргументы не поддерживаются");
        var parms = rawParms as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                    return Ok(id, new JsonObject
                    {
                        // Версию протокола эхом от клиента: свою навязывать нечем, а несовпадение
                        // строки CLI трактует как несовместимость сервера
                        ["protocolVersion"] = parms?["protocolVersion"]?.DeepClone()
                            ?? JsonValue.Create("2025-06-18"),
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = toolset.Name,
                            ["version"] = toolset.Version,
                        },
                    });

                case "tools/list":
                {
                    var tools = new JsonArray();
                    foreach (var tool in toolset.Tools)
                        tools.Add(new JsonObject
                        {
                            ["name"] = tool.Name,
                            ["description"] = tool.Description,
                            ["inputSchema"] = tool.InputSchema.DeepClone(),
                        });
                    return Ok(id, new JsonObject { ["tools"] = tools });
                }

                case "tools/call":
                {
                    var toolName = parms?["name"] is JsonValue n && n.TryGetValue<string>(out var tn)
                        ? tn : null;
                    if (toolName is null) return Error(id, -32602, "Не указано имя инструмента");
                    var args = parms?["arguments"]?.DeepClone() as JsonObject ?? [];
                    try
                    {
                        var result = await toolset.CallAsync(toolName, args, context, ct);
                        return Ok(id, ToolContent(result.Text, result.IsError));
                    }
                    catch (Exception ex)
                    {
                        // Ошибку вызова отдаём content'ом, а не -32603: у модели должен остаться
                        // читаемый текст, из которого видно, что делать дальше (так же ведут себя
                        // stdio-серверы продукта)
                        logger.LogWarning(ex, "MCP-инструмент {Server}.{Tool} упал", toolset.Name, toolName);
                        return Ok(id, ToolContent($"Ошибка: {ex.Message}", isError: true));
                    }
                }

                case "ping":
                    return Ok(id, new JsonObject());

                default:
                    return Error(id, -32601, $"Метод не поддерживается: {method}");
            }
        }
        catch (Exception ex)
        {
            // Неожиданный сбой диспетчера/тулсета — протокольная ошибка -32603, а не голый
            // HTTP 500: получив 500 (например, на initialize), клиент MCP снимает весь набор
            // инструментов сервера до конца жизни процесса CLI. Причину — в лог, ответ
            // клиенту остаётся JSON-RPC
            logger.LogWarning(ex, "Сбой диспетчера MCP {Server} ({Method})", toolset.Name, method);
            return Error(id, -32603, $"Внутренняя ошибка сервера: {ex.Message}");
        }
    }

    /// <summary>
    /// Имя вызова для журнала MCP (GET /api/mcp/calls, алерт 04-mcp-errors). У stdio-серверов
    /// заголовок X-Mcp-Tool ставит сам сервер на каждый запрос к бэкенду; при http запрос
    /// приходит от CLI, и имя инструмента лежит в ТЕЛЕ JSON-RPC — достаём его и кладём во
    /// входящий заголовок. Запись сделает та же McpCallLogMiddleware (она читает заголовки
    /// уже после контроллера) — второй точки записи заводить не нужно, а без этого журнал по
    /// переехавшим серверам ослеп бы на «(без имени)».
    ///
    /// Имя из тела — внешняя строка без лимита заголовков Kestrel, а дальше она становится
    /// ключом McpCallLog и подстановкой в логи: форму проверяем сами, негодное (мегабайтные
    /// строки, CRLF, кириллица) схлопываем в общую строку переполнения журнала.
    /// </summary>
    /// <summary>Имя одиночного вызова: инструмент из tools/call либо служебный метод.</summary>
    private void NameCallForLog(IMcpToolset toolset, JsonObject request)
    {
        var method = request["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        string tool;
        if (method == "tools/call"
            && request["params"] as JsonObject is { } parms
            && parms["name"] is JsonValue name && name.TryGetValue<string>(out var toolName))
        {
            tool = Telemetry.MetricTagGuard.IsToolShape(toolName) ? toolName : Services.Mcp.McpCallLog.Overflow;
        }
        else
        {
            // Служебные методы протокола показываем отдельными строками: «(без имени) /mcp/…»
            // в таблице диагностики не отличить от чужого клиента с тем же заголовком
            tool = $"{toolset.Name}/{(method is not null && IsMethodShape(method) ? method : Services.Mcp.McpCallLog.Overflow)}";
        }
        Request.Headers[Services.Mcp.McpCallLogMiddleware.ToolHeader] = tool;
    }

    /// <summary>Имя батча в журнале: HTTP-запрос один, элемент внутри не отличить.</summary>
    private void NameCallForLog(IMcpToolset toolset, string method) =>
        Request.Headers[Services.Mcp.McpCallLogMiddleware.ToolHeader] =
            $"{toolset.Name}/{(IsMethodShape(method) ? method : Services.Mcp.McpCallLog.Overflow)}";

    // Форма имени метода протокола: как IsToolShape, но со слэшем — сегменты методов MCP
    // соединяются им («tools/list», «notifications/initialized»), и в заголовок слэш
    // попадает изнутри, а не из тела запроса.
    private static bool IsMethodShape(string v) =>
        v.Length <= 64 && v.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/');

    private static JsonObject ToolContent(string text, bool isError)
    {
        var result = new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = text },
            },
        };
        if (isError) result["isError"] = true;
        return result;
    }

    private static JsonObject Ok(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    // Тело отдаём готовой строкой: JSON-RPC не должен зависеть от политики сериализации
    // приложения (camelCase и прочее переписали бы ключи JSON Schema инструментов)
    private ContentResult JsonRpc(JsonNode payload) =>
        Content(payload.ToJsonString(), "application/json");
}
