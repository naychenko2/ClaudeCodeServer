using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Mcp.Http;

/// <summary>
/// Прокси-тулсет Higgsfield к mcp.higgsfield.ai/mcp с белым списком из 10 инструментов.
/// Состав конфига хода строится из кэша с валидностью 30 мин, fail-open на предыдущем снимке.
/// Доступ к прокси через хвост /mcp/higgsfield/{sessionId}, fail-closed на чужой сессии (обязательно свой).
/// При отсутствии инстансного подключения (пустой EnsureFresh) список пуст — сервер не объявляется ходу.
///
/// Проблема Mcp-Session-Id (ревью Глеба): Higgsfield сейчас отвечает без заголовка Mcp-Session-Id.
/// Если он когда-либо перейдёт на Streamable HTTP с сессиями,
/// этот прокси должен упасть первым, а не молча ломаться о проде. См. тест на pitfall в HiggsfieldToolsetTests.
/// </summary>
public sealed class HiggsfieldToolset(
    HiggsfieldOAuthService higgsfield,
    IHttpClientFactory clientFactory,
    SessionManager sessions,
    ILogger<HiggsfieldToolset> log) : IMcpParameterizedToolset
{
    public const string ServerName = McpEndpoints.HiggsfieldName;

    /// <summary>Имя HTTP-клиента для тихих запросов к Higgsfield.</summary>
    public const string HttpClientName = "higgsfield-mcp";

    /// <summary>Базовый URL эндпоинта Higgsfield MCP.</summary>
    public const string UpstreamUrl = HiggsfieldIntegration.Url;

    /// <summary>TTL кэша списка tools/list: 30 минут.</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Белый список инструментов — единственный источник правды для состава хода.
    /// Higgsfield отдаёт 88+ инструментов; в конфиг едут только эти 10.
    /// Остальные (подписки, деплой, sandbox, TikTok) являются шумом и риском для модели.
    /// </summary>
    internal static readonly string[] Whitelist =
    [
        "generate_image",
        "generate_video",
        "generate_audio",
        "job_status",
        "jobs_wait",
        "show_generation_by_ids",
        "models_explore",
        "media_import_url",
        "media_upload",
        "media_upload_widget",
    ];

    private static readonly HashSet<string> WhitelistSet = new(Whitelist, StringComparer.Ordinal);

    public string Name => ServerName;
    public string Version => "1.0.0";

    // Кэш: последний успешный снимок + время. Fail-open при отказе обновления.
    private readonly object _cacheLock = new();
    private IReadOnlyList<McpToolSchema>? _cachedTools;
    private DateTime _cachedAt = DateTime.MinValue;

    // ---- Состав ----

    public IReadOnlyList<McpToolSchema> ToolsFor(McpToolCallContext context)
    {
        if (!TryResolveSession(context, out _)) return [];
        return GetCachedTools();
    }

    private IReadOnlyList<McpToolSchema> GetCachedTools()
    {
        lock (_cacheLock)
        {
            var expired = _cachedAt + CacheTtl < DateTime.UtcNow;
            if (!expired) return _cachedTools ?? [];
            try
            {
                var fresh = FetchToolsList();
                if (fresh is not null)
                {
                    _cachedTools = fresh;
                    _cachedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Higgsfield tools/list: refresh failed - fail-open on last snapshot");
            }
            return _cachedTools ?? [];
        }
    }

    // ---- Вызов ----

    public async Task<McpToolCallResult> CallAsync(string tool, JsonObject arguments,
        McpToolCallContext context, CancellationToken ct)
    {
        if (!TryResolveSession(context, out _))
            return Deny("Chat invoker not found or belongs to another owner.");
        var token = higgsfield.EnsureFresh();
        if (token is null)
            return Deny("Higgsfield is not connected on this instance - contact the admin.");
        if (!WhitelistSet.Contains(tool))
            return Deny($"Tool {tool} is not part of the Higgsfield toolset on this instance.");
        try
        {
            var body = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = tool,
                    ["arguments"] = arguments.DeepClone(),
                },
            };
            var response = await PostToHiggsfieldAsync(token, body, ct);
            return response is null
                ? Deny("Higgsfield did not respond (connection failure or timeout).")
                : ExtractResult(response);
        }
        catch (HttpRequestException ex)
        {
            return Deny($"Higgsfield unavailable: {OneLine(ex.Message)}");
        }
        catch (TaskCanceledException) when (ct is not { IsCancellationRequested: true })
        {
            return Deny("Higgsfield timed out. You may retry.");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Higgsfield tools/call {Tool} failed", tool);
            return Deny($"Internal proxy error: {OneLine(ex.Message)}");
        }
    }

    // ---- HTTP / SSE ----

    private async Task<JsonObject?> PostToHiggsfieldAsync(string token, JsonObject body,
        CancellationToken ct, int timeoutMs = 120_000)
    {
        var client = clientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
        var request = new HttpRequestMessage(HttpMethod.Post, UpstreamUrl)
        {
            Content = new StringContent(body.ToJsonString(JsonOpts),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        var text = await new System.IO.StreamReader(stream).ReadToEndAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("Higgsfield responded HTTP {Status}: {Body}",
                (int)response.StatusCode, OneLine(text[..Math.Min(text.Length, 200)]));
            return null;
        }
        var data = ExtractSseData(text);
        if (data is null)
        {
            log.LogWarning("Higgsfield SSE: data line not found. Body: {Text}", OneLine(text));
            return null;
        }
        return data;
    }

    private IReadOnlyList<McpToolSchema>? FetchToolsList()
    {
        var token = higgsfield.EnsureFresh();
        if (token is null) return null;
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 0,
            ["method"] = "tools/list",
            ["params"] = new JsonObject(),
        };
        var client = clientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(15);
        var request = new HttpRequestMessage(HttpMethod.Post, UpstreamUrl)
        {
            Content = new StringContent(body.ToJsonString(JsonOpts),
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = client.Send(request);
        using var stream = response.Content.ReadAsStream();
        var text = new System.IO.StreamReader(stream).ReadToEnd();
        if (!response.IsSuccessStatusCode)
        {
            log.LogWarning("Higgsfield tools/list: HTTP {Status}", (int)response.StatusCode);
            return null;
        }
        var data = ExtractSseData(text);
        if (data?["result"] is not JsonObject result) return null;
        if (result["tools"] is not JsonArray tools) return null;
        var filtered = new List<McpToolSchema>();
        foreach (var node in tools)
        {
            if (node is not JsonObject t) continue;
            var name = t["name"] is JsonValue nv ? nv.ToString() : null;
            if (name is null || !WhitelistSet.Contains(name)) continue;
            filtered.Add(new McpToolSchema(
                name,
                t["description"] is JsonValue dv ? dv.ToString() : "",
                t["inputSchema"] as JsonObject ?? new JsonObject()));
        }
        return filtered;
    }

    /// <summary>
    /// Извлекает JSON из SSE-ответа Higgsfield.
    /// PITFALL: если Higgsfield когда-либо введёт сессию, этот парсер сломается.
    /// </summary>
    internal static JsonObject? ExtractSseData(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = trimmed["data:".Length..].Trim();
            if (json.Length == 0) continue;
            try
            {
                var node = JsonNode.Parse(json);
                if (node is JsonObject obj) return obj;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static McpToolCallResult ExtractResult(JsonObject rpc)
    {
        if (rpc["result"] is JsonObject result)
        {
            if (result["content"] is JsonArray content && content.Count > 0
                && content[0] is JsonObject first
                && first["text"] is JsonValue tv)
            {
                var isError = result["isError"] is JsonValue ev && ev.GetValue<bool>();
                return new McpToolCallResult(tv.ToString(), isError);
            }
            return new McpToolCallResult(result.ToJsonString());
        }
        if (rpc["error"] is JsonObject error)
        {
            var msg = error["message"] is JsonValue mv ? mv.ToString() : "Unknown Higgsfield error";
            return Deny($"Higgsfield: {msg}");
        }
        return Deny("Higgsfield returned an unexpected response.");
    }

    private bool TryResolveSession(McpToolCallContext context, out Models.Session? session)
    {
        session = null;
        if (!TryParseRoute(context.RouteTail, out var sessionId))
            return false;
        session = sessions.GetOwned(sessionId, context.OwnerId);
        return session is not null;
    }

    private static bool TryParseRoute(string? route, out string sessionId)
    {
        sessionId = "";
        if (route is null || route.Split('/').Length != 1) return false;
        if (route.Length is < 1 or > 128
            || !route.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        sessionId = route;
        return true;
    }

    /// <summary>URL эндпоинта для конфига хода: /mcp/higgsfield/{sessionId}.</summary>
    public static string EndpointFor(string apiUrl, string sessionId) =>
        McpEndpoints.EndpointFor(apiUrl, ServerName) + "/" + sessionId;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static McpToolCallResult Deny(string text) => new(text, IsError: true);
    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");
}
