using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ImageEditor;

namespace ClaudeHomeServer.Services.ImageEditor;

// Тонкий JSON-RPC-клиент бэкенда к mcp.higgsfield.ai/mcp (ADR-017, раздел 2а). REST API под
// наш OAuth-токен у Higgsfield нет, MCP-эндпоинт и есть их API. Прокси хода
// HiggsfieldToolset не переиспользуем: он привязан к сессии чата. initialize не нужен —
// сервер отвечает без Mcp-Session-Id.
//
// Токен берётся через шов IHiggsfieldAccess на КАЖДЫЙ вызов: задача живёт минуты, а
// обновление токена идёт само. null — интеграция отключена админом.
public sealed class HiggsfieldMcpClient(IHttpClientFactory http, IConfiguration config, IHiggsfieldAccess? access = null)
{
    public const string HttpClientName = "higgsfield-editor";
    private const string DefaultUrl = "https://mcp.higgsfield.ai/mcp";

    private readonly string _url = config["Higgsfield:McpUrl"] ?? DefaultUrl;
    private int _rpcId;

    public bool Available => Token() is not null;

    public async Task<HiggsfieldCall> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct)
    {
        var token = Token();
        if (token is null) return HiggsfieldCall.NoAccess("Higgsfield отключён администратором");

        var rpc = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref _rpcId),
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(rpc.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Streamable HTTP: без text/event-stream апстрим вправе ответить 406
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");

        string text;
        try
        {
            using var resp = await Client().SendAsync(req, ct);
            text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return HiggsfieldCall.NoAccess("Higgsfield не принял токен доступа");
            if (!resp.IsSuccessStatusCode)
                return HiggsfieldCall.NoAccess($"Higgsfield ответил {(int)resp.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return HiggsfieldCall.NoAccess("Higgsfield не ответил: " + ex.Message);
        }

        var envelope = ParseEnvelope(text);
        if (envelope is null) return HiggsfieldCall.Error("Непонятный ответ Higgsfield");
        if (envelope["error"] is JsonObject err)
            return HiggsfieldCall.Error(err["message"]?.ToString() ?? "Ошибка Higgsfield");
        if (envelope["result"] is not JsonObject result) return HiggsfieldCall.Error("Пустой ответ Higgsfield");

        var body = new StringBuilder();
        if (result["content"] is JsonArray content)
            foreach (var item in content.OfType<JsonObject>())
                if (item["text"]?.ToString() is { Length: > 0 } t)
                    body.Append(body.Length > 0 ? "\n" : "").Append(t);
        var isError = result["isError"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e;
        return new HiggsfieldCall(!isError, false, body.ToString(), result["structuredContent"]);
    }

    public async Task<bool> PutAsync(string url, byte[] bytes, string contentType, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        try
        {
            using var resp = await Client().SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public Task<EditedImage?> DownloadAsync(string url, CancellationToken ct) =>
        ImageDownload.FetchAsync(Client(), url, null, ct);

    // Ответ бывает и JSON, и SSE (строки data:)
    internal static JsonObject? ParseEnvelope(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try { return JsonNode.Parse(trimmed) as JsonObject; }
            catch (JsonException) { return null; }
        }
        foreach (var line in text.Split('\n'))
        {
            var l = line.TrimStart();
            if (!l.StartsWith("data:", StringComparison.Ordinal)) continue;
            try
            {
                if (JsonNode.Parse(l["data:".Length..].Trim()) is JsonObject obj) return obj;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private string? Token()
    {
        try
        {
            return access?.AccessToken();
        }
        catch
        {
            return null;
        }
    }

    private HttpClient Client()
    {
        var client = http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }
}

// Ok — tools/call прошёл и isError=false; NoAccess — поставщик недоступен (нет токена,
// 401, сеть); иначе — содержательная ошибка инструмента в Text
public sealed record HiggsfieldCall(bool Ok, bool Unavailable, string Text, JsonNode? Structured)
{
    public static HiggsfieldCall NoAccess(string reason) => new(false, true, reason, null);
    public static HiggsfieldCall Error(string text) => new(false, false, text, null);

    // Тело ответа как JSON: structuredContent, если есть, иначе JSON в тексте
    public JsonNode? Json()
    {
        if (Structured is not null) return Structured;
        var start = Text.IndexOf('{');
        var end = Text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(Text[start..(end + 1)]); }
        catch (JsonException) { return null; }
    }
}
