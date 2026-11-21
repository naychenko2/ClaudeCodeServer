using System.Net;
using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Т2f: GET /api/admin/child-routes — список допустимых маршрутов пикера модели (разрез §5).
/// В тестовом пуле — три вида записей: чистый API-ключ (законна), только OAuth-токен (подписка),
/// смешанная (ключ + токен). В списке должна быть только первая — тот же критерий, что у
/// боевого запуска (ChildRoute.Resolve). Каталог тестового хоста — Fallback без опроса
/// CLI ("default" + алиасы семейств): "default" в models не попадает, отсутствие маркеров
/// "tier:"/"preset:" проверяется прямо по ответу (инвариант).
/// </summary>
[Trait("Category", "Host")]
public class ChildRoutesControllerTests : IClassFixture<ChildRoutesControllerTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts()
        {
            // Законные записи: с ключом — с DisplayName и без него (подпись откатывается на ключ)
            ExtraConfig["ClaudeSubscriptions:kids-api:ApiKey"] = "sk-ant-test-kids";
            ExtraConfig["ClaudeSubscriptions:kids-api:DisplayName"] = "Детский ключ";
            ExtraConfig["ClaudeSubscriptions:plain-key:ApiKey"] = "sk-ant-plain";
            // Незаконные: подписка без ключа; смешанная — токен мог бы уехать в детский ход,
            // боевой запуск такую отказывает, значит и выбором в пикере она быть не может
            ExtraConfig["ClaudeSubscriptions:oauth-sub:OAuthToken"] = "oauth-token";
            ExtraConfig["ClaudeSubscriptions:both-keys:ApiKey"] = "sk-ant-both";
            ExtraConfig["ClaudeSubscriptions:both-keys:OAuthToken"] = "oauth-both";
            // Провайдеры моделей (Т5a): облако с ценой, облако без цены (в список не входит), локальный
            ExtraConfig["LlmProviders:cl-priced:AnthropicBaseUrl"] = "https://api.priced.test/anthropic";
            ExtraConfig["LlmProviders:cl-priced:ApiKey"] = "sk-priced";
            ExtraConfig["LlmProviders:cl-priced:DisplayName"] = "Облако";
            ExtraConfig["LlmProviders:cl-priced:Models:0:Id"] = "cl-pro";
            ExtraConfig["LlmProviders:cl-priced:Models:0:DisplayName"] = "Cl Pro";
            ExtraConfig["LlmProviders:cl-priced:Models:0:PriceInMissPer1M"] = "0.5";
            ExtraConfig["LlmProviders:cl-priced:Models:0:PriceOutPer1M"] = "1.0";
            ExtraConfig["LlmProviders:cl-priced:Models:1:Id"] = "cl-free-price";
            ExtraConfig["LlmProviders:cl-free:AnthropicBaseUrl"] = "https://api.free.test/anthropic";
            ExtraConfig["LlmProviders:cl-free:ApiKey"] = "sk-free";
            ExtraConfig["LlmProviders:cl-free:Models:0:Id"] = "cl-nopriced";
            ExtraConfig["LlmProviders:loc-test:AnthropicBaseUrl"] = "http://127.0.0.1:18999";
            ExtraConfig["LlmProviders:loc-test:IsLocal"] = "true";
            ExtraConfig["LlmProviders:loc-test:Models:0:Id"] = "loc-test-27b";
        }
    }

    private readonly Hosts _hosts;
    private readonly HttpClient _admin;
    private readonly ClaudeSubscriptionPool _pool;

    public ChildRoutesControllerTests(Hosts hosts)
    {
        _hosts = hosts;
        _admin = _hosts.CreateAuthenticatedClient();
        _pool = _hosts.Services.GetRequiredService<ClaudeSubscriptionPool>();
    }

    private async Task<JsonElement> GetAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/admin/child-routes");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
    }

    private static List<JsonElement> RoutesOf(JsonElement root) =>
        root.GetProperty("routes").EnumerateArray().ToList();

    private static List<string> IdsOf(JsonElement route) =>
        route.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Get_ВсеЗаписиПула_ВПорядкеПула()
    {
        var root = await GetAsync(_admin);
        var keys = RoutesOf(root).Select(r => r.GetProperty("providerKey").GetString()!).ToList();

        // По И2″ (решение 25) маршрутом годится любая запись пула: токен, ключ или оба.
        // Порядок — порядок пула, не сортировка.
        var poolKeys = RoutesOf(root).Where(r => r.GetProperty("kind").GetString() == "pool")
            .Select(r => r.GetProperty("providerKey").GetString()!).ToList();
        poolKeys.Should().Equal(_pool.All.Select(s => s.Key));
        keys.Should().Contain(new[] { "kids-api", "oauth-sub", "both-keys" });

        foreach (var r in RoutesOf(root).Where(r => r.GetProperty("kind").GetString() == "pool"))
        {
            r.GetProperty("priced").GetBoolean().Should().BeTrue();
            r.GetProperty("requiresAck").GetBoolean().Should().BeTrue("по И2″ согласие профиля нужно любому маршруту, включая пул");
        }
    }

    // Т5a: провайдеры отдаются по тем же правилам ChildRoute: облако — только модели с ценой, нужно согласие;
    // локальный — priced=false, нужно согласие; провайдер без цены ни одной модели не даёт — в списке его нет.
    [Fact]
    public async Task Get_Провайдеры_cloudСЦеной_local_БезОблакаБезЦены()
    {
        var routes = RoutesOf(await GetAsync(_admin))
            .ToDictionary(r => r.GetProperty("providerKey").GetString()!);

        var cloud = routes["cl-priced"];
        cloud.GetProperty("kind").GetString().Should().Be("cloud");
        cloud.GetProperty("label").GetString().Should().Be("Облако");
        cloud.GetProperty("priced").GetBoolean().Should().BeTrue();
        cloud.GetProperty("requiresAck").GetBoolean().Should().BeTrue();
        IdsOf(cloud).Should().Equal("cl-pro");

        var local = routes["loc-test"];
        local.GetProperty("kind").GetString().Should().Be("local");
        local.GetProperty("priced").GetBoolean().Should().BeFalse("бюджет у локальной не тратится");
        local.GetProperty("requiresAck").GetBoolean().Should().BeTrue();
        IdsOf(local).Should().Equal("loc-test-27b");

        routes.Should().NotContainKey("cl-free", "облачная модель без цены не пройдёт валидатор — и пикер её не отдаёт");
    }

    [Fact]
    public async Task Get_Модели_алиасыСемейств_безЗаглушекИМаркеров()
    {
        var root = await GetAsync(_admin);
        var routes = RoutesOf(root).Where(r => r.GetProperty("kind").GetString() == "pool").ToList();
        routes.Should().NotBeEmpty("в тестовом пуле есть запись с ключом");

        foreach (var r in routes)
        {
            var models = r.GetProperty("models").EnumerateArray().ToList();
            models.Should().NotBeEmpty("каталог тестового хоста отдаёт Claude-fallback");
            var ids = models.Select(m => m.GetProperty("id").GetString()!).ToList();

            // Только алиасы известных семейств — то же значение, что боевой запуск отдаёт в --model
            ids.Should().OnlyContain(id => ClaudeModelFamily.All.Any(f => f.Alias == id));
            ids.Should().Contain("sonnet");

            // Инвариант шага: ни "default"/"claude" (легаси-значения маршрута), ни маркеров
            // "tier:"/"preset:" — пикер показывает только конкретные модели
            ids.Should().NotContain("default");
            ids.Should().NotContain("claude");
            ids.Should().OnlyContain(id => !id.StartsWith("tier:") && !id.StartsWith("preset:"));

            // Подпись модели есть у каждого пункта (под ней пикер рисует пункт списка)
            models.Should().OnlyContain(m => !string.IsNullOrWhiteSpace(m.GetProperty("label").GetString()));
        }
    }

    [Fact]
    public async Task Get_ПодписьЗаписи_DisplayName_или_ключ()
    {
        var root = await GetAsync(_admin);
        var labels = RoutesOf(root).ToDictionary(
            r => r.GetProperty("providerKey").GetString()!,
            r => r.GetProperty("label").GetString()!);

        labels["kids-api"].Should().Be("Детский ключ");
        // DisplayName не задан — подпись равна ключу: он и так человекочитаем (имя в конфиге),
        // пустая подпись сделать пункт пикера неразборчивым
        labels["plain-key"].Should().Be("plain-key");
    }

    [Fact]
    public async Task Get_НеАдмин_403()
    {
        var client = _hosts.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var resp = await client.GetAsync("/api/admin/child-routes");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
