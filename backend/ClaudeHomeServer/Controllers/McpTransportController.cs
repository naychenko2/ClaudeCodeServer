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
    public async Task<IActionResult> Handle(string name, [FromBody] JsonNode? body, CancellationToken ct)
    {
        var toolset = registry.Find(name);
        if (toolset is null) return NotFound(new { error = "unknown_mcp_server", message = $"Нет MCP-сервера «{name}»" });

        // DefaultMapInboundClaims = false → sub не ремапится в NameIdentifier, читаем напрямую
        var ownerId = User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(ownerId)) return Unauthorized();
        var context = new McpToolCallContext(ownerId,
            Request.Headers.TryGetValue("X-Caller-Session-Id", out var caller) ? caller.ToString() : null);

        if (body is null)
            return JsonRpc(Error(null, -32700, "Пустое тело запроса"));

        // Батч JSON-RPC: CLI его обычно не шлёт, но спецификация допускает, а тихо
        // отвечать на первый элемент хуже, чем поддержать десятком строк
        if (body is JsonArray batch)
        {
            var answers = new JsonArray();
            foreach (var item in batch)
            {
                if (item is not JsonObject msg) continue;
                if (await DispatchAsync(toolset, msg, context, ct) is { } answer) answers.Add(answer);
            }
            // Батч из одних уведомлений — отвечать нечем (спецификация: 202 без тела)
            return answers.Count == 0 ? Accepted() : JsonRpc(answers);
        }

        if (body is not JsonObject request)
            return JsonRpc(Error(null, -32600, "Ожидался объект JSON-RPC"));

        var result = await DispatchAsync(toolset, request, context, ct);
        // Уведомление (без id) ответа не имеет — CLI шлёт notifications/initialized сразу
        // после рукопожатия и ждёт именно 202, а не тело с null-id
        return result is null ? Accepted() : JsonRpc(result);
    }

    private async Task<JsonObject?> DispatchAsync(IMcpToolset toolset, JsonObject request,
        McpToolCallContext context, CancellationToken ct)
    {
        var method = request["method"] is JsonValue m && m.TryGetValue<string>(out var s) ? s : null;
        var id = request["id"]?.DeepClone();
        NameCallForLog(toolset, request, method);
        if (method is null) return id is null ? null : Error(id, -32600, "Не указан method");
        // Уведомления (id нет) отрабатываем молча: ответ на них — нарушение протокола
        if (id is null) return null;

        switch (method)
        {
            case "initialize":
                return Ok(id, new JsonObject
                {
                    // Версию протокола эхом от клиента: свою навязывать нечем, а несовпадение
                    // строки CLI трактует как несовместимость сервера
                    ["protocolVersion"] = request["params"]?["protocolVersion"]?.DeepClone()
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
                var toolName = request["params"]?["name"] is JsonValue n && n.TryGetValue<string>(out var tn)
                    ? tn : null;
                if (toolName is null) return Error(id, -32602, "Не указано имя инструмента");
                var args = request["params"]?["arguments"]?.DeepClone() as JsonObject ?? [];
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

    /// <summary>
    /// Имя вызова для журнала MCP (GET /api/mcp/calls, алерт 04-mcp-errors). У stdio-серверов
    /// заголовок X-Mcp-Tool ставит сам сервер на каждый запрос к бэкенду; при http запрос
    /// приходит от CLI, и имя инструмента лежит в ТЕЛЕ JSON-RPC — достаём его и кладём во
    /// входящий заголовок. Запись сделает та же McpCallLogMiddleware (она читает заголовки
    /// уже после контроллера) — второй точки записи заводить не нужно, а без этого журнал по
    /// переехавшим серверам ослеп бы на «(без имени)».
    /// </summary>
    private void NameCallForLog(IMcpToolset toolset, JsonObject request, string? method)
    {
        var tool = method == "tools/call"
            && request["params"]?["name"] is JsonValue name && name.TryGetValue<string>(out var toolName)
            ? toolName
            // Служебные методы протокола показываем отдельными строками: «(без имени) /mcp/…»
            // в таблице диагностики не отличить от чужого клиента с тем же заголовком
            : $"{toolset.Name}/{method ?? "(без метода)"}";
        Request.Headers[Services.Mcp.McpCallLogMiddleware.ToolHeader] = tool;
    }

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
