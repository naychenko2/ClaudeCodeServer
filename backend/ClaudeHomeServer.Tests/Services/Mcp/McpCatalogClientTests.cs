using System.Net;
using ClaudeHomeServer.Services.Mcp.Catalog;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services.Mcp;

// Клиент официального реестра MCP (план «Каталог MCP-серверов», волна 1, шаг 1):
// любая беда сети/ответа — доменная ошибка, кэш по (q, cursor) с TTL на инжектируемых
// часах и потолком записей. Список кейсов — тест-план плана.
public class McpCatalogClientTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    // Стаб транспорта: считает вызовы, помнит последний запрос, отвечает заготовкой
    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? LastRequest;
        public Func<HttpRequestMessage, HttpResponseMessage> Respond =
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler)
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 64 * 1024,
        };
    }

    private static string PageJson(params string[] names)
    {
        var servers = string.Join(",", names.Select(n =>
            "{\"server\":{\"name\":\"" + n + "\",\"version\":\"1.0.0\"}," +
            "\"_meta\":{\"io.modelcontextprotocol.registry/official\":" +
            "{\"status\":\"active\",\"isLatest\":true}}}"));
        return "{\"servers\":[" + servers + "],\"metadata\":{\"count\":" + names.Length + ",\"nextCursor\":null}}";
    }

    private static (McpCatalogClient Client, StubHandler Handler, MutableTimeProvider Time) Create(
        string body = "{}", HttpStatusCode status = HttpStatusCode.OK,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHandler();
        if (respond is not null) handler.Respond = respond;
        else handler.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent(body) };
        var time = new MutableTimeProvider();
        var options = Options.Create(new McpCatalogOptions
        {
            BaseUrl = "https://registry.example",
            PageSize = 20,
            CacheMinutes = 30,
            CacheMaxEntries = 8,
            MaxQueryLength = 100,
        });
        return (new McpCatalogClient(new StubFactory(handler), options, time), handler, time);
    }

    // --- нормальный путь ---

    [Fact]
    public async Task Поиск_возвращает_карточки_и_кладёт_в_кэш()
    {
        var (client, handler, _) = Create(PageJson("io.github.o/one", "io.github.o/two"));
        var page1 = await client.SearchAsync("one", null);
        var page2 = await client.SearchAsync("one", null);
        page1.Items.Should().HaveCount(2);
        page1.Items[0].Name.Should().Be("io.github.o/one");
        page2.Should().BeSameAs(page1); // второй запрос — из кэша
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void Пустой_адрес_каталог_выключен()
    {
        var client = new McpCatalogClient(new StubFactory(new StubHandler()),
            Options.Create(new McpCatalogOptions()));
        client.IsEnabled.Should().BeFalse();
    }

    // --- беды реестра → доменная ошибка ---

    [Fact]
    public async Task Ответ_500_доменная_ошибка()
    {
        var (client, _, _) = Create(status: HttpStatusCode.InternalServerError);
        var act = () => client.SearchAsync("q", null);
        await act.Should().ThrowAsync<McpCatalogUnavailableException>()
            .WithMessage("*500*");
    }

    [Fact]
    public async Task Битый_JSON_доменная_ошибка()
    {
        var (client, _, _) = Create("это не json вовсе");
        var act = () => client.SearchAsync("q", null);
        await act.Should().ThrowAsync<McpCatalogUnavailableException>()
            .WithMessage("*не разобран*");
    }

    [Fact]
    public async Task Огромное_тело_доменная_ошибка()
    {
        // MaxResponseContentBufferSize режет ответ до разбора
        var (client, _, _) = Create(body: new string('a', 128 * 1024));
        var act = () => client.SearchAsync("q", null);
        await act.Should().ThrowAsync<McpCatalogUnavailableException>();
    }

    [Fact]
    public async Task Таймаут_доменная_ошибка()
    {
        var slow = new StubHandler
        {
            Respond = _ => throw new TaskCanceledException("запрос не уложился в таймаут"),
        };
        var client = new McpCatalogClient(new StubFactory(slow),
            Options.Create(new McpCatalogOptions { BaseUrl = "https://registry.example" }));
        var act = () => client.SearchAsync("q", null);
        await act.Should().ThrowAsync<McpCatalogUnavailableException>()
            .WithMessage("*не отвечает*");
    }

    // --- TTL и потолок кэша ---

    [Fact]
    public async Task Запись_кэша_протухает_по_часам()
    {
        var (client, handler, time) = Create(PageJson("io.github.o/x"));
        await client.SearchAsync("q", null);
        time.Now += TimeSpan.FromMinutes(31);
        await client.SearchAsync("q", null);
        handler.Calls.Should().Be(2); // после протухания — снова в сеть
    }

    [Fact]
    public async Task Кэш_не_растёт_без_предела()
    {
        var (client, handler, _) = Create(PageJson("io.github.o/x"));
        // CacheMaxEntries = 8: 20 разных запросов — вытеснение вместо роста памяти,
        // никаких ошибок CapacityOverflow
        for (var i = 0; i < 20; i++)
            await client.SearchAsync("q" + i, null);
        handler.Calls.Should().Be(20);
    }

    // --- нормализация запроса ---

    [Fact]
    public async Task Длинный_запрос_обрезается_и_чистится()
    {
        var (client, handler, _) = Create(PageJson("io.github.o/x"));
        await client.SearchAsync("  " + new string('a', 200) + "  ", null);
        handler.LastRequest!.RequestUri!.Query.Should()
            .Contain(Uri.EscapeDataString(new string('a', 100)));
    }
}
