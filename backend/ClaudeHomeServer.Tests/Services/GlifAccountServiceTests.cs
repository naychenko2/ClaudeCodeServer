using System.Net;
using System.Text;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сервис аккаунта glif.app: whoami через JSON-RPC tools/call. Живая форма ответа (проверена
// токеном 2026-07-31): { identity, session, billing:{ plan:{productId,status,periodEnd},
// credits:{available,subscription,extra}, spend:{last24h,last7d,last30d} } } — в structuredContent
// и дублируется JSON-строкой в text-блоке; ответ — SSE (event: message). Баланс — кредиты.
public class GlifAccountServiceTests
{
    // Пример взят из реального ответа whoami (значения подставлены тестовые)
    private const string LiveWhoami = """
        {"identity":{"userId":"u1","username":"tester"},"session":{"apiTokenLabel":null},"billing":{"plan":{"productId":"prod_X","status":"active","periodEnd":"2026-08-31T00:00:00.000Z"},"credits":{"available":1650,"subscription":1650,"extra":0},"spend":{"last24h":50,"last7d":120,"last30d":300}}}
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? LastRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static GlifAccountService CreateService(StubHandler handler, string? token = "glif_v1_test")
    {
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["Glif:McpToken"] = token,
        });
        return new GlifAccountService(new StubHttpFactory(handler), config);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // Обёртка MCP-ответа как у живого сервера: structuredContent + text-блок с тем же JSON
    private static string WhoamiResult(string innerJson)
    {
        var escaped = innerJson.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        return "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" +
               escaped + "\"}],\"structuredContent\":" + innerJson + "}}";
    }

    // SSE-кадр «event: message» — так отвечает продовый endpoint
    private static HttpResponseMessage Sse(string innerJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("event: message\ndata: " + WhoamiResult(innerJson) + "\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    [Fact]
    public async Task БезТокена_ФичаВыключена_ИСетевогоВызоваНет()
    {
        var handler = new StubHandler(_ => Json("{}"));
        var svc = CreateService(handler, token: null);

        svc.Enabled.Should().BeFalse();
        var resp = await svc.GetAsync(7);

        resp.Enabled.Should().BeFalse();
        resp.Plan.Should().BeNull();
        resp.Balance.Should().BeNull();
        handler.Calls.Should().Be(0, "без токена сеть не дёргаем");
    }

    [Fact]
    public async Task ЖивойОтвет_МаппитсяНаКонтракт()
    {
        var handler = new StubHandler(_ => Sse(LiveWhoami));
        var svc = CreateService(handler);

        var resp = await svc.GetAsync(7);

        resp.Enabled.Should().BeTrue();
        resp.Plan.Should().Be("prod_X");
        resp.PlanStatus.Should().Be("active");
        resp.Balance.Should().Be(1650);
        resp.Currency.Should().Be("credits", "баланс glif — кредиты, не USD");
        resp.Spend.Should().NotBeNull();
        resp.Spend!.Last24h.Should().Be(50);
        resp.Spend.Last7d.Should().Be(120);
        resp.Spend.Last30d.Should().Be(300);
    }

    [Fact]
    public async Task ДанныеИзTextБлока_КогдаНетStructuredContent()
    {
        var escaped = LiveWhoami.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"" +
                   escaped + "\"}]}}";
        var handler = new StubHandler(_ => Json(body));
        var svc = CreateService(handler);

        var resp = await svc.GetAsync(7);

        resp.Plan.Should().Be("prod_X");
        resp.Balance.Should().Be(1650);
        resp.Spend!.Last30d.Should().Be(300);
    }

    [Fact]
    public async Task ЧастичныйОтвет_ОтсутствующиеПоляNull()
    {
        const string whoami = """{"identity":{"userId":"u1"},"billing":{"plan":{"status":"trialing"}}}""";
        var handler = new StubHandler(_ => Json(WhoamiResult(whoami)));
        var svc = CreateService(handler);

        var resp = await svc.GetAsync(7);

        resp.Enabled.Should().BeTrue();
        resp.Plan.Should().BeNull("productId отсутствует");
        resp.PlanStatus.Should().Be("trialing");
        resp.Balance.Should().BeNull();
        resp.Currency.Should().BeNull("нет баланса — нет и пометки валюты");
        resp.Spend.Should().BeNull();
    }

    [Fact]
    public async Task ОтветБезBilling_ПоляNull()
    {
        var handler = new StubHandler(_ => Json(WhoamiResult("""{"identity":{"userId":"u1"}}""")));
        var svc = CreateService(handler);

        var resp = await svc.GetAsync(7);

        resp.Enabled.Should().BeTrue();
        resp.Plan.Should().BeNull();
        resp.Balance.Should().BeNull();
        resp.Spend.Should().BeNull();
    }

    [Fact]
    public async Task ОшибкаHttp_ПоляNull_НоФичаВключена()
    {
        var handler = new StubHandler(_ => Json("{}", HttpStatusCode.InternalServerError));
        var svc = CreateService(handler);

        var resp = await svc.GetAsync(7);

        resp.Enabled.Should().BeTrue();
        resp.Plan.Should().BeNull();
        resp.Balance.Should().BeNull();
        resp.Spend.Should().BeNull();
    }

    [Fact]
    public async Task ОшибкаJsonRpc_ПоляNull()
    {
        var handler = new StubHandler(_ => Json("""{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"boom"}}"""));
        var svc = CreateService(handler);

        var resp = await svc.GetAsync(7);

        resp.Enabled.Should().BeTrue();
        resp.Plan.Should().BeNull();
    }

    [Fact]
    public async Task Кэш60Секунд_ПовторныйВызовБезСети()
    {
        var handler = new StubHandler(_ => Json(WhoamiResult(LiveWhoami)));
        var svc = CreateService(handler);

        var first = await svc.GetAsync(7);
        var second = await svc.GetAsync(7);

        handler.Calls.Should().Be(1, "второй вызов в пределах TTL обязан отдать кэш");
        second.Balance.Should().Be(first.Balance);
    }

    [Fact]
    public async Task Запрос_ИдётНаMcpEndpointСBearerТокеном()
    {
        var handler = new StubHandler(_ => Json(WhoamiResult(LiveWhoami)));
        var svc = CreateService(handler, token: "glif_v1_abc");

        await svc.GetAsync(7);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Be("https://glif.app/api/mcp");
        handler.LastRequest.Headers.Authorization.Should().NotBeNull();
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("glif_v1_abc");
    }
}
