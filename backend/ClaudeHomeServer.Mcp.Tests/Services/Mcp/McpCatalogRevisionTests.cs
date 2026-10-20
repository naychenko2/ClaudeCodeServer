using System.Net;
using ClaudeHomeServer.Services.Mcp.Catalog;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.Mcp;

// Ревизия импортированных записей каталога (план «Каталог MCP-серверов», волна 2):
// «отозван» — ТОЛЬКО явный status: deprecated/deleted в разобранном ответе; любая беда
// сети/ответа — «проверить не удалось», но НЕ «отзыв» (сторож: лежащий preview-сервис
// не должен превращаться в вердикт «выключи рабочие серверы»). Кэш — сутки на
// инжектируемых часах, неудача не кэшируется. Плюс semver-сравнение версий.
public class McpCatalogRevisionTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

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

    // Ответ GET /v0.1/servers/{name}/versions/latest
    private static string LatestJson(string status = "active", string version = "1.0.0") =>
        "{\"server\":{\"name\":\"io.github.o/x\",\"version\":\"" + version + "\"}," +
        "\"_meta\":{\"io.modelcontextprotocol.registry/official\":" +
        "{\"status\":\"" + status + "\",\"isLatest\":true}}}";

    private static (McpCatalogClient Client, StubHandler Handler, MutableTimeProvider Time) Create(
        string body = "{}", HttpStatusCode status = HttpStatusCode.OK,
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHandler();
        if (respond is not null) handler.Respond = respond;
        else handler.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent(body) };
        var time = new MutableTimeProvider();
        return (new McpCatalogClient(new StubFactory(handler),
            new McpCatalogOptions { BaseUrl = "https://registry.example" }, time), handler, time);
    }

    private static async Task<McpCatalogRevisionItem> OneAsync(McpCatalogClient client,
        string? importedVersion = "1.0.0")
    {
        var items = await client.ReviseAsync(
            [new McpCatalogRevisionQuery("io.github.o/x", importedVersion)]);
        return items[0];
    }

    // --- плашка «отозван»: только явный статус из разобранного ответа ---

    [Theory]
    [InlineData("deprecated")]
    [InlineData("deleted")]
    public async Task Явный_статус_отзывает(string status)
    {
        var (client, _, _) = Create(LatestJson(status: status, version: "2.0.0"));
        var item = await OneAsync(client);
        item.Deprecated.Should().BeTrue();
        item.CheckFailed.Should().BeFalse();
        item.Status.Should().Be(status);
        // Отзыв и свежесть независимы: у отозванного версия всё равно сверяется
        item.HasNewerVersion.Should().BeTrue();
    }

    // СТОРОЖ: 404/таймаут/5xx/битый JSON — «проверить не удалось», НЕ «отозван».
    // Реестр — preview-сервис, он имеет право лежать; молчаливое «отозван» в этот
    // момент заставило бы человека выключить рабочие серверы
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Беды_ответа_проверить_не_удалось_но_не_отозван(HttpStatusCode status)
    {
        var (client, _, _) = Create(status: status);
        var item = await OneAsync(client);
        item.CheckFailed.Should().BeTrue();
        item.Deprecated.Should().BeFalse();
        item.HasNewerVersion.Should().BeFalse();
        item.Error.Should().Contain(((int)status).ToString());
    }

    [Fact]
    public async Task Таймаут_проверить_не_удалось_но_не_отозван()
    {
        var slow = new StubHandler
        {
            Respond = _ => throw new TaskCanceledException("не уложился в таймаут"),
        };
        var client = new McpCatalogClient(new StubFactory(slow),
            new McpCatalogOptions { BaseUrl = "https://registry.example" });
        var item = await OneAsync(client);
        item.CheckFailed.Should().BeTrue();
        item.Deprecated.Should().BeFalse();
        item.Error.Should().Contain("не отвечает");
    }

    [Fact]
    public async Task Битый_JSON_проверить_не_удалось_но_не_отозван()
    {
        var (client, _, _) = Create("это не json вовсе");
        var item = await OneAsync(client);
        item.CheckFailed.Should().BeTrue();
        item.Deprecated.Should().BeFalse();
        item.Error.Should().Contain("не разобран");
    }

    [Fact]
    public async Task Активный_статус_без_плашек()
    {
        var (client, _, _) = Create(LatestJson(status: "active", version: "1.0.0"));
        var item = await OneAsync(client);
        item.Deprecated.Should().BeFalse();
        item.CheckFailed.Should().BeFalse();
        item.Status.Should().Be("active");
        item.HasNewerVersion.Should().BeFalse();
    }

    // --- сверка версий ---

    [Fact]
    public async Task Версия_новее_импортированной_флаг()
    {
        var (client, _, _) = Create(LatestJson(version: "2.0.0"));
        var item = await OneAsync(client, importedVersion: "1.9.9");
        item.HasNewerVersion.Should().BeTrue();
        item.LatestVersion.Should().Be("2.0.0");
    }

    [Fact]
    public async Task Без_импортированной_версии_сверки_нет()
    {
        var (client, _, _) = Create(LatestJson(version: "2.0.0"));
        var item = await OneAsync(client, importedVersion: null);
        item.HasNewerVersion.Should().BeFalse();
    }

    [Theory]
    // release выше prerelease; build-метаданные не считаются
    [InlineData("2.0.0", "2.0.0-beta.1", true)]
    [InlineData("2.0.0-beta.1", "2.0.0", false)]
    [InlineData("1.2.4", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.3+build.7", false)]
    // prerelease-идентификаторы: длинный префикс старше, beta старше alpha, число ниже буквы
    [InlineData("1.2.3-alpha.1", "1.2.3-alpha", true)]
    [InlineData("1.2.3-beta", "1.2.3-alpha", true)]
    [InlineData("1.2.3-alpha", "1.2.3-1", true)]
    [InlineData("1.2.4-alpha", "1.2.3-beta", true)]
    // непарсящаяся сторона — «не знаем», а не «нулевая версия»
    [InlineData("2.0", "1.0.0", false)]
    [InlineData("1.0.0", "latest", false)]
    [InlineData(null, "1.0.0", false)]
    public void SemVer_сравнение_по_правилам(string? latest, string? imported, bool expected) =>
        McpCatalogSemVer.IsNewer(latest, imported).Should().Be(expected);

    [Theory]
    [InlineData("1.0.0", null, "1.0.0")]
    [InlineData(null, "1.0.0", "1.0.0")]
    [InlineData("1.2.3", "1.2.4", "1.2.4")]
    [InlineData("x", "y", "x")]
    public void SemVer_старшая_из_двух(string? a, string? b, string expected) =>
        McpCatalogSemVer.MaxBySemVer(a, b).Should().Be(expected);

    // --- кэш: сутки, на инжектируемых часах ---

    [Fact]
    public async Task Кэш_держит_сутки_и_в_сеть_повторно_не_ходит()
    {
        var (client, handler, time) = Create(LatestJson());
        await OneAsync(client);
        await OneAsync(client); // повторный клик — из кэша
        time.Now += TimeSpan.FromHours(23);
        await OneAsync(client); // 23 часа — всё ещё из кэша
        handler.Calls.Should().Be(1);
        time.Now += TimeSpan.FromHours(2); // перевалили за сутки
        await OneAsync(client);
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Неудачная_проверка_не_кэшируется()
    {
        var (client, handler, _) = Create(status: HttpStatusCode.NotFound);
        (await OneAsync(client)).CheckFailed.Should().BeTrue();
        (await OneAsync(client)).CheckFailed.Should().BeTrue();
        // Лежащий реестр — не знание о сервере: каждый повторный клик идёт в сеть
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Дубль_имени_в_батче_бьёт_в_сеть_один_раз()
    {
        var (client, handler, _) = Create(LatestJson());
        var items = await client.ReviseAsync(
        [
            new McpCatalogRevisionQuery("io.github.o/x", "1.0.0"),
            new McpCatalogRevisionQuery("io.github.o/x", "1.0.0"),
        ]);
        items.Should().HaveCount(2);
        handler.Calls.Should().Be(1);
    }

    // --- адрес запроса ---

    [Fact]
    public async Task Имя_записи_URL_энкодится()
    {
        var (client, handler, _) = Create(LatestJson());
        await OneAsync(client);
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should()
            .Contain("/v0.1/servers/io.github.o%2Fx/versions/latest");
    }
}
