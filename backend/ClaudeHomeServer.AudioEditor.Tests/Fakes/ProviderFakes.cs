using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Higgsfield;
using ClaudeHomeServer.Services.Media;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.AudioEditor.Tests.Fakes;

// Фейковый HTTP для Core-клиента Higgsfield: маршрут по функции, журнал запросов с телами
internal sealed class FakeHttp(Func<FakeHttp.Call, HttpResponseMessage> route) : HttpMessageHandler, IHttpClientFactory
{
    public sealed record Call(HttpMethod Method, string Url, string Body);

    public ConcurrentQueue<Call> Calls { get; } = new();

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var call = new Call(request.Method, request.RequestUri!.ToString(), body);
        Calls.Enqueue(call);
        return route(call);
    }

    public static HttpResponseMessage Bytes(byte[] bytes, string? contentType) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = contentType is null ? null : new(contentType) } },
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

    public static string? Tool(Call call) =>
        call.Body.StartsWith('{') && JsonNode.Parse(call.Body)?["params"]?["name"]?.ToString() is { } name ? name : null;

    public static JsonObject? Arguments(Call call) =>
        JsonNode.Parse(call.Body)?["params"]?["arguments"] as JsonObject;

    public static HiggsfieldMcpClient Client(FakeHttp http, string? token = "admin-token") =>
        new(http, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Higgsfield:McpUrl"] = "https://mcp.test/mcp" })
            .Build(), new FakeHiggsfieldAccess(token));
}

internal sealed class FakeHiggsfieldAccess(string? token) : IHiggsfieldAccess
{
    public string? AccessToken() => token;
}

internal sealed class StepTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

// Шов Яндекса: голоса и амплуа как у каталога SpeechKit, синтез отвечает заданным итогом
internal sealed class FakeTts : ITtsEngine
{
    public bool Configured { get; init; } = true;
    public TtsSynthesis Result { get; init; } = new([7, 7, 7], 0.3252, null);
    public List<(string Text, string Voice, string? Role, double Speed)> Calls { get; } = [];

    public IReadOnlyList<TtsEngineVoice> Voices { get; } =
    [
        new("alena", "Алёна", ["neutral", "good"]),
        new("filipp", "Филипп", []),
        new("jane", "Джейн", ["neutral", "good", "evil"]),
    ];

    public IReadOnlyList<string> Roles => ["neutral", "good", "evil"];
    public int MaxChars => 3000;
    public double MinSpeed => 0.1;
    public double MaxSpeed => 3.0;

    public Task<TtsSynthesis> SynthesizeAsync(string text, string voice, string? role, double speed, CancellationToken ct)
    {
        Calls.Add((text, voice, role, speed));
        return Task.FromResult(Result);
    }
}
