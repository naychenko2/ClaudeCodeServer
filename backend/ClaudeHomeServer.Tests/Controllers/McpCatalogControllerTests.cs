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
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Controllers;

// GET api/mcp/catalog/search (план «Каталог MCP-серверов», волна 1, шаг 3): флаг,
// «пустой адрес = выключен», доменный отказ при лежащем реестре, потолок запросов
// на бэке и изоляция по авторизации. Записей отсюда не создаётся — это только поиск.
public class McpCatalogControllerTests : IClassFixture<TestWebApplicationFactory>
{
    // Фейковый клиент: FetchAndMapAsync переопределён, кэш не задействован
    private sealed class FakeCatalogClient(McpCatalogSearchResult page, bool enabled = true,
        Exception? failWith = null)
        : McpCatalogClient(new StubFactory(), Options.Create(new McpCatalogOptions
        {
            BaseUrl = enabled ? "https://registry.example" : "",
        }))
    {
        protected override Task<McpCatalogSearchResult> FetchAndMapAsync(
            string q, string? cursor, CancellationToken ct)
        {
            if (failWith is not null) throw failWith;
            return Task.FromResult(page);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    // Фабрика с точечной подменой конфига поверх базовой: InMemory-источник добавляется
    // ПОСЛЕ базовых, поэтому выигрывает (порядок источников у конфигурации — последний прав)
    private sealed class CatalogFactory : TestWebApplicationFactory
    {
        public IReadOnlyDictionary<string, string?>? Config { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            if (Config is not null)
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Config));
        }
    }

    private static TestWebApplicationFactory FactoryWith(
        McpCatalogSearchResult? page = null, bool enabled = true, Exception? failWith = null,
        int? rateLimit = null)
    {
        var factory = new CatalogFactory
        {
            Config = rateLimit is { } limit
                ? new Dictionary<string, string?> { ["Mcp:Catalog:RateLimit"] = limit.ToString() }
                : null,
        };
        factory.ExtraServices = services =>
        {
            services.RemoveAll<McpCatalogClient>();
            services.AddSingleton<McpCatalogClient>(new FakeCatalogClient(
                page ?? new McpCatalogSearchResult([], null), enabled, failWith));
        };
        return factory;
    }

    private static void EnableFlag(TestWebApplicationFactory factory)
    {
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetFeatureFlag(users.GetFirst()!.Id, FeatureFlagKeys.McpCatalog, true).Should().BeTrue();
    }

    private static readonly McpCatalogSearchResult SamplePage = new(
    [
        new McpCatalogCardDto("io.github.o/one", "One", "Описание", "https://repo",
            "1.0.0", DateTime.Parse("2026-05-18T13:28:59Z"), "active", true,
            true, null,
            new McpCatalogPrefillDto("one", "One", "Описание", "stdio", "npx",
                ["-y", "one-mcp@1.0.0"], null, [])),
    ], "next-cursor");

    [Fact]
    public async Task Поиск_возвращает_карточки_и_курсор()
    {
        using var factory = FactoryWith(SamplePage);
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/mcp/catalog/search?q=one");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("items")[0].GetProperty("name").GetString().Should().Be("io.github.o/one");
        body.GetProperty("items")[0].GetProperty("prefill").GetProperty("args")[1].GetString()
            .Should().Be("one-mcp@1.0.0");
        body.GetProperty("nextCursor").GetString().Should().Be("next-cursor");
    }

    [Fact]
    public async Task Флаг_выключен_404()
    {
        using var factory = FactoryWith(SamplePage);
        // Флаг не включаем — дефолт выключен (dark launch)
        var client = factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/mcp/catalog/search?q=one");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Пустой_адрес_каталога_503_с_текстом()
    {
        using var factory = FactoryWith(SamplePage, enabled: false);
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/mcp/catalog/search?q=one");
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Contain("не настроен");
    }

    [Fact]
    public async Task Лежащий_реестр_502_плашкой_а_не_исключением()
    {
        using var factory = FactoryWith(failWith: new McpCatalogUnavailableException("Реестр MCP ответил 503"));
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/mcp/catalog/search?q=one");
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("error").GetString().Should().Contain("Реестр");
    }

    [Fact]
    public async Task Без_авторизации_401()
    {
        using var factory = FactoryWith(SamplePage);
        EnableFlag(factory);
        var resp = await factory.CreateClient().GetAsync("/api/mcp/catalog/search?q=one");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Потолок_запросов_429()
    {
        using var factory = FactoryWith(SamplePage, rateLimit: 3);
        EnableFlag(factory);
        var client = factory.CreateAuthenticatedClient();

        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 6; i++)
            last = (await client.GetAsync("/api/mcp/catalog/search?q=one")).StatusCode;
        last.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
