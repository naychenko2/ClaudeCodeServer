using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Mcp;
using ClaudeHomeServer.Services.Mcp.Catalog;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClaudeHomeServer.Tests.Controllers;

// POST api/mcp/catalog/revision (план «Каталог MCP-серверов», волна 2): батч-сверка
// импортированных записей с живым реестром. Инварианты: эндпоинт ОТДЕЛЬНЫЙ от списка
// записей (лежащий реестр не роняет открытие раздела), проверяются только имена
// владельца с CatalogRef (изоляция), плашка «отозван» — только явный статус.
public class McpCatalogRevisionControllerTests
{
    // Фейковый транспорт: реальный путь ReviseAsync (кэш, разбор, статусы) поверх
    // заготовленных ответов versions/latest. Считает вызовы — «чужое имя не дошло
    // до реестра» проверяется по счётчику
    private sealed class FakeCatalogClient(string latestJson = "{}", bool enabled = true)
        : McpCatalogClient(new StubFactory(), new McpCatalogOptions
        {
            BaseUrl = enabled ? "https://registry.example" : "",
        })
    {
        public int Fetches;

        protected override Task<string> FetchLatestAsync(string name, CancellationToken ct)
        {
            Fetches++;
            return Task.FromResult(latestJson);
        }
    }

    private sealed class ThrowingCatalogClient : McpCatalogClient
    {
        public ThrowingCatalogClient() : base(new StubFactory(),
            new McpCatalogOptions { BaseUrl = "https://registry.example" }) { }

        protected override Task<string> FetchLatestAsync(string name, CancellationToken ct) =>
            throw new McpCatalogUnavailableException("Реестр MCP не отвечает");
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static string LatestJson(string status = "active", string version = "2.0.0") =>
        "{\"server\":{\"name\":\"x\",\"version\":\"" + version + "\"}," +
        "\"_meta\":{\"io.modelcontextprotocol.registry/official\":" +
        "{\"status\":\"" + status + "\",\"isLatest\":true}}}";

    private sealed class RevisionFactory : TestWebApplicationFactory
    {
        public IReadOnlyDictionary<string, string?>? Config { get; set; }
        public FakeCatalogClient? Fake { get; set; }
        public ThrowingCatalogClient? Thrower { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (Config is not null)
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Config));
            builder.ConfigureServices(services =>
            {
                if (Fake is not null || Thrower is not null)
                {
                    services.RemoveAll<McpCatalogClient>();
                    if (Fake is not null) services.AddSingleton<McpCatalogClient>(Fake);
                    else services.AddSingleton<McpCatalogClient>(Thrower!);
                }
            });
        }
    }

    private static string UserIdOf(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<UserStore>().GetFirst()!.Id;

    private static void EnableFlag(TestWebApplicationFactory factory) =>
        factory.Services.GetRequiredService<UserStore>()
            .SetFeatureFlag(UserIdOf(factory), FeatureFlagKeys.McpCatalog, true).Should().BeTrue();

    private static async Task<JsonElement> CreateCatalogServerAsync(HttpClient client,
        string name, string version)
    {
        var resp = await client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "cat-" + Guid.NewGuid().ToString("N")[..8],
            ["transport"] = "stdio",
            ["command"] = "node",
            ["catalogRef"] = new { name, version },
        });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReviseAsync(HttpClient client, params string[] names)
    {
        var resp = await client.PostAsJsonAsync("/api/mcp/catalog/revision", new { names });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    // --- основной путь ---

    [Fact]
    public async Task Ревизия_возвращает_статус_и_флаг_новой_версии()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson("active", "2.0.0")) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/one", "1.0.0");

        var body = await ReviseAsync(client, "io.github.o/one");
        var item = body.GetProperty("items")[0];
        item.GetProperty("name").GetString().Should().Be("io.github.o/one");
        item.GetProperty("status").GetString().Should().Be("active");
        item.GetProperty("deprecated").GetBoolean().Should().BeFalse();
        item.GetProperty("hasNewerVersion").GetBoolean().Should().BeTrue();
        item.GetProperty("latestVersion").GetString().Should().Be("2.0.0");
        item.GetProperty("checkFailed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Явный_deprecated_ставит_плашку()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson("deprecated", "1.0.0")) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/dep", "1.0.0");

        var body = await ReviseAsync(client, "io.github.o/dep");
        body.GetProperty("items")[0].GetProperty("deprecated").GetBoolean().Should().BeTrue();
        body.GetProperty("items")[0].GetProperty("checkFailed").GetBoolean().Should().BeFalse();
    }

    // СТОРОЖ: беда реестра — «проверить не удалось», НЕ «отозван»
    [Fact]
    public async Task Лежащий_реестр_проверить_не_удалось_но_не_отозван()
    {
        using var factory = new RevisionFactory { Thrower = new ThrowingCatalogClient() };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/dead", "1.0.0");

        var body = await ReviseAsync(client, "io.github.o/dead");
        var item = body.GetProperty("items")[0];
        item.GetProperty("checkFailed").GetBoolean().Should().BeTrue();
        item.GetProperty("deprecated").GetBoolean().Should().BeFalse();
        item.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
    }

    // --- инвариант отдельности: список записей не ждёт и не роняет реестр ---

    [Fact]
    public async Task Список_серверов_жив_при_лежащем_реестре()
    {
        using var factory = new RevisionFactory { Thrower = new ThrowingCatalogClient() };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/alive", "1.0.0");

        var resp = await client.GetAsync("/api/mcp/servers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        list.GetArrayLength().Should().BeGreaterThan(0);
        // и запись видна с её catalogRef — ревизия тут не участвует вовсе
        list[0].TryGetProperty("catalogRef", out _).Should().BeTrue();
    }

    // --- фильтрация по владельцу ---

    [Fact]
    public async Task Чужие_имена_и_записи_без_CatalogRef_в_ревизию_не_попадают()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson()) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/mine", "1.0.0");
        // ручная запись того же владельца — без указателя на каталог
        var manual = await client.PostAsJsonAsync("/api/mcp/servers", new Dictionary<string, object?>
        {
            ["key"] = "manual-" + Guid.NewGuid().ToString("N")[..8],
            ["transport"] = "stdio",
            ["command"] = "node",
        });
        manual.EnsureSuccessStatusCode();
        // запись второго пользователя — чужая
        var secondId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        factory.Services.GetRequiredService<McpRegistry>().Create(secondId, new McpServerRecord
        {
            Key = "their-" + Guid.NewGuid().ToString("N")[..8],
            Transport = McpTransport.Stdio,
            Command = "node",
            CatalogRef = new McpCatalogRef { Name = "io.github.o/theirs", Version = "1.0.0" },
        });

        var body = await ReviseAsync(client, "io.github.o/mine", "io.github.o/theirs",
            "io.github.o/unknown");
        var names = body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToList();
        names.Should().Equal("io.github.o/mine"); // чужое и незнакомое молча выпали
        factory.Fake!.Fetches.Should().Be(1); // в реестр ушло одно имя — своё
    }

    [Fact]
    public async Task Изоляция_владельца_второй_юзер_видит_только_своё()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson()) };
        EnableFlag(factory);
        var first = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(first, "io.github.o/first", "1.0.0");
        // флаг включается первому пользователю — второму включаем отдельно
        var users = factory.Services.GetRequiredService<UserStore>();
        var secondId = users.FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;
        users.SetFeatureFlag(secondId, FeatureFlagKeys.McpCatalog, true).Should().BeTrue();
        factory.Services.GetRequiredService<McpRegistry>().Create(secondId, new McpServerRecord
        {
            Key = "second-" + Guid.NewGuid().ToString("N")[..8],
            Transport = McpTransport.Stdio,
            Command = "node",
            CatalogRef = new McpCatalogRef { Name = "io.github.o/second", Version = "1.0.0" },
        });

        var second = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var body = await ReviseAsync(second, "io.github.o/first", "io.github.o/second");
        body.GetProperty("items").EnumerateArray().Single()
            .GetProperty("name").GetString().Should().Be("io.github.o/second");
    }

    [Fact]
    public async Task Две_записи_одного_имени_один_запрос_и_старшая_версия()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson("active", "2.0.0")) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/dup", "1.0.0");
        await CreateCatalogServerAsync(client, "io.github.o/dup", "2.0.0");

        var body = await ReviseAsync(client, "io.github.o/dup");
        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(1); // имя одно — и запрос в реестр один
        factory.Fake!.Fetches.Should().Be(1);
        // сверка со старшей из подключённых: 2.0.0 не «новее» 2.0.0
        items[0].GetProperty("hasNewerVersion").GetBoolean().Should().BeFalse();
    }

    // --- гейты запроса ---

    [Fact]
    public async Task Флаг_выключен_404()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson()) };
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/mcp/catalog/revision",
            new { names = new[] { "io.github.o/x" } });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Не_настроен_503()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson(), enabled: false) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/mcp/catalog/revision",
            new { names = new[] { "io.github.o/x" } });
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task Пустой_список_имён_400(int? count)
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson()) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        var names = count is null
            ? null
            : Array.Empty<string>();
        var resp = await client.PostAsJsonAsync("/api/mcp/catalog/revision", new { names });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Потолок_батча_400()
    {
        using var factory = new RevisionFactory { Fake = new FakeCatalogClient(LatestJson()) };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        var names = Enumerable.Range(0, 51).Select(i => "io.github.o/n" + i).ToArray();
        var resp = await client.PostAsJsonAsync("/api/mcp/catalog/revision", new { names });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- проводка опций в клиент (регрессия волны 1) ---
    //
    // Раньше Program регистрировал прямые опции, а клиент просил IOptions<> — DI
    // отдавал клиенту дефолтный инстанс, и BaseUrl не доезжал: каталог всегда считался
    // «не настроенным». Настоящий клиент без подмены + заменённая регистрация опций:
    // если проводка сломана снова, клиент увидит дефолт и ответит 503, а не сходит
    // в сеть (checkFailed по недоступному адресу)
    [Fact]
    public async Task Опции_регистрации_доезжают_до_клиента_через_DI()
    {
        using var factory = new RevisionFactory();
        factory.ExtraServices = services =>
        {
            services.RemoveAll<McpCatalogOptions>();
            services.AddSingleton(new McpCatalogOptions { BaseUrl = "https://127.0.0.1:1" });
        };
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();
        await CreateCatalogServerAsync(client, "io.github.o/wired", "1.0.0");

        factory.Services.GetRequiredService<McpCatalogClient>().IsEnabled.Should().BeTrue();

        var body = await ReviseAsync(client, "io.github.o/wired");
        body.GetProperty("items")[0].GetProperty("checkFailed").GetBoolean().Should().BeTrue();
    }
}
