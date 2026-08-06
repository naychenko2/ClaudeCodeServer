using System.Text.Json;

namespace ClaudeHomeServer.Services.Mcp;

/// <summary>
/// Кадры JSON-RPC рукопожатия MCP и разбор ответов — чистая часть пробы (без процессов и HTTP),
/// поэтому проверяется юнит-тестами. Последовательность одна для обоих транспортов:
/// <c>initialize</c> → <c>notifications/initialized</c> → <c>tools/list</c>.
/// </summary>
public static class McpProbeProtocol
{
    /// <summary>Версия протокола в initialize. Сервер вправе ответить своей — мы не придираемся.</summary>
    public const string ProtocolVersion = "2025-06-18";

    public const int InitializeId = 1;
    public const int ToolsListId = 2;

    public static string InitializeRequest() =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + InitializeId + ",\"method\":\"initialize\",\"params\":{"
        + "\"protocolVersion\":\"" + ProtocolVersion + "\",\"capabilities\":{},"
        + "\"clientInfo\":{\"name\":\"claude-home-server\",\"version\":\"1.0\"}}}";

    public static string InitializedNotification() =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

    public static string ToolsListRequest() =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + ToolsListId + ",\"method\":\"tools/list\",\"params\":{}}";

    /// <summary>Разобранный кадр ответа: результат или текст ошибки сервера.</summary>
    public sealed record Frame(int Id, JsonElement? Result, string? Error);

    /// <summary>
    /// Пытается прочитать строку как ответ JSON-RPC с ожидаемым id. Мусор, уведомления и
    /// чужие id — не ошибка: stdout сервера содержит и логи, и служебные кадры, их пропускаем.
    /// </summary>
    public static bool TryParseFrame(string? line, int expectedId, out Frame frame)
    {
        frame = new Frame(expectedId, null, null);
        if (string.IsNullOrWhiteSpace(line)) return false;
        var text = line.Trim();
        if (text[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("id", out var idProp)
                || idProp.ValueKind != JsonValueKind.Number
                || idProp.GetInt32() != expectedId) return false;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                frame = new Frame(expectedId, null, message ?? "сервер вернул ошибку");
                return true;
            }
            // Клонируем: JsonDocument уйдёт вместе с using, а результат нужен вызывающему
            frame = new Frame(expectedId,
                root.TryGetProperty("result", out var result) ? result.Clone() : default(JsonElement?),
                null);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Имя сервера из ответа initialize (serverInfo.name); null — сервер не представился.</summary>
    public static string? ServerNameFrom(JsonElement? result)
    {
        if (result is not { } r || r.ValueKind != JsonValueKind.Object) return null;
        if (!r.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object) return null;
        var name = info.TryGetProperty("name", out var n) ? n.GetString() : null;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Имена инструментов из ответа tools/list.</summary>
    public static IReadOnlyList<string> ToolNamesFrom(JsonElement? result)
    {
        if (result is not { } r || r.ValueKind != JsonValueKind.Object) return [];
        if (!r.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) return [];
        return tools.EnumerateArray()
            .Select(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("name", out var n)
                ? n.GetString() ?? "" : "")
            .Where(n => n.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Строки-кадры из тела HTTP-ответа. Streamable HTTP отвечает либо голым JSON, либо
    /// потоком SSE (<c>data: {...}</c>) — разбираем оба, вызывающему разница не важна.
    /// </summary>
    public static IEnumerable<string> Frames(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) yield break;
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            yield return trimmed;
            yield break;
        }
        foreach (var line in body.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (text.StartsWith("data:", StringComparison.Ordinal))
                yield return text[5..].Trim();
        }
    }

    /// <summary>
    /// Статус по коду HTTP-ответа. 401/403 — «нужен вход», а не поломка: сервер жив, просто
    /// не пускает, и человеку надо не чинить конфиг, а авторизоваться.
    /// </summary>
    public static string StatusFromHttp(int statusCode) => statusCode switch
    {
        401 or 403 => McpServerStatuses.NeedsAuth,
        >= 200 and < 300 => McpServerStatuses.Connected,
        _ => McpServerStatuses.Failed,
    };
}
