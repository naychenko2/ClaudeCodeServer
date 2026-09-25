using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.ImageEditor.Spending;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.ImageEditor.Fakes;

// Фейковый HTTP: маршрутизация по функции, журнал всех запросов с телами
internal sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(HttpMethod Method, string Url, string Body, string? Auth);

    private readonly Func<Call, HttpResponseMessage> _route;
    public ConcurrentQueue<Call> Calls { get; } = new();

    public FakeHttp(Func<Call, HttpResponseMessage> route) => _route = route;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var call = new Call(request.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString());
        Calls.Enqueue(call);
        return _route(call);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType = "image/png") =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(contentType) } },
        };

    // Ответ MCP tools/call в SSE, как отвечает mcp.higgsfield.ai
    public static HttpResponseMessage McpText(string text, bool isError = false)
    {
        var rpc = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = isError,
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {rpc.ToJsonString()}\n\n", Encoding.UTF8, "text/event-stream"),
        };
    }

    // Имя инструмента из тела JSON-RPC
    public static string? Tool(Call call) =>
        call.Body.StartsWith('{') && JsonNode.Parse(call.Body)?["params"]?["name"]?.ToString() is { } name ? name : null;

    public static JsonObject? Arguments(Call call) =>
        JsonNode.Parse(call.Body)?["params"]?["arguments"] as JsonObject;
}

internal sealed class FakeHiggsfieldAccess(string? token = "admin-token") : IHiggsfieldAccess
{
    public string? Token { get; set; } = token;
    public int Calls;

    public string? AccessToken()
    {
        Interlocked.Increment(ref Calls);
        return Token;
    }
}

internal sealed class RecordingBroadcaster : ISessionBroadcaster
{
    public ConcurrentQueue<(string OwnerId, ServerMessage Message)> ToOwnerCalls { get; } = new();

    public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
    public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
    public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
    public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;

    public Task ToOwner(string ownerId, ServerMessage message)
    {
        ToOwnerCalls.Enqueue((ownerId, message));
        return Task.CompletedTask;
    }
}

internal sealed class MemorySpendStore : IImageEditSpendStore
{
    public ConcurrentQueue<ImageEditSpendRecord> Records { get; } = new();

    public void Record(ImageEditSpendRecord record) => Records.Enqueue(record);

    public IReadOnlyList<ImageEditSpendRecord> Query(string ownerId, bool isAdmin) =>
        [.. Records.Where(r => isAdmin || r.OwnerId == ownerId)];
}

internal static class TestImages
{
    // Минимальный PNG-заголовок с IHDR заданного размера — размеры читаются, пиксели не нужны
    public static byte[] Png(int width, int height, byte tail = 0)
    {
        var b = new byte[33];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(b, 0);
        b[11] = 13;
        "IHDR"u8.ToArray().CopyTo(b, 12);
        WriteBe(b, 16, width);
        WriteBe(b, 20, height);
        b[32] = tail;
        return b;
    }

    public static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static void WriteBe(byte[] b, int at, int v)
    {
        b[at] = (byte)(v >> 24);
        b[at + 1] = (byte)(v >> 16);
        b[at + 2] = (byte)(v >> 8);
        b[at + 3] = (byte)v;
    }
}
