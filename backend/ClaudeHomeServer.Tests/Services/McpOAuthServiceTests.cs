using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// OAuth внешних MCP-серверов: discovery, PKCE, обмен кода и обновление токена.
/// Сеть подменена обработчиком — проверяем ровно то, что уходит чужому серверу
/// (challenge, resource, verifier) и что после этого лежит у нас (токены только в сторе).
/// </summary>
public class McpOAuthServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ccs-mcp-oauth-" + Guid.NewGuid().ToString("N")[..8]);
    private const string Owner = "owner1";
    private const string ServerUrl = "https://mcp.example.com/mcp";

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* уборка best-effort */ }
    }

    // ── PKCE ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Pkce_ChallengeСовпадаетСВекторомRfc7636()
    {
        // Пример из RFC 7636, приложение B
        McpPkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void Pkce_VerifierСлучаенИБезПаддинга()
    {
        var first = McpPkce.CreateVerifier();
        var second = McpPkce.CreateVerifier();

        first.Should().NotBe(second);
        first.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
    }

    // ── разбор метаданных ────────────────────────────────────────────────────────────

    [Fact]
    public void Discovery_АдресМетаданныхИзЗаголовка()
    {
        McpOAuthDiscovery.ResourceMetadataFrom(
                ["Bearer realm=\"mcp\", resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\""])
            .Should().Be("https://mcp.example.com/.well-known/oauth-protected-resource");
    }

    [Fact]
    public void Discovery_БезПараметра_ЗаголовокНеДаётАдреса()
    {
        McpOAuthDiscovery.ResourceMetadataFrom(["Bearer realm=\"mcp\""]).Should().BeNull();
        McpOAuthDiscovery.ResourceMetadataFrom(null).Should().BeNull();
    }

    [Fact]
    public void Discovery_КандидатыWellKnown_СначалаСПутёмСервера()
    {
        McpOAuthDiscovery.ProtectedResourceCandidates(new Uri(ServerUrl))
            .Should().Equal(
                "https://mcp.example.com/.well-known/oauth-protected-resource/mcp",
                "https://mcp.example.com/.well-known/oauth-protected-resource");
    }

    [Fact]
    public void Discovery_ОткрытаяКонфигурация_ФолбэкПослеOauthДокумента()
    {
        McpOAuthDiscovery.AuthorizationServerCandidates(new Uri("https://auth.example.com"))
            .Should().Equal(
                "https://auth.example.com/.well-known/oauth-authorization-server",
                "https://auth.example.com/.well-known/openid-configuration");
    }

    [Fact]
    public void Discovery_БезМетаданных_ДефолтныеПутиСпеки()
    {
        var endpoints = McpOAuthDiscovery.DefaultEndpoints(new Uri("https://auth.example.com"));

        endpoints.AuthorizationEndpoint.Should().Be("https://auth.example.com/authorize");
        endpoints.TokenEndpoint.Should().Be("https://auth.example.com/token");
        endpoints.RegistrationEndpoint.Should().Be("https://auth.example.com/register");
    }

    // ── сквозной вход ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Вход_ПроходитЦепочкуDiscoveryDcrИСохраняетТокены()
    {
        var (service, registry, secrets, _, http) = NewService();
        var record = NewRecord(registry);
        // Реестр отдаёт живой объект записи — версию до входа запоминаем значением
        var versionBefore = record.AuthVersion;

        var start = await service.StartAsync(Owner, record, Redirect, input: null);

        // Адрес окна провайдера собран по спеке: PKCE S256 + resource (RFC 8707)
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        start.AuthorizeUrl.Should().StartWith("https://auth.example.com/authorize?");
        query["code_challenge_method"].Should().Be("S256");
        query["code_challenge"].Should().NotBeNullOrEmpty();
        query["resource"].Should().Be("https://mcp.example.com/mcp");
        query["redirect_uri"].Should().Be(Redirect);
        query["client_id"].Should().Be("client-from-dcr");
        query["state"].Should().Be(start.State);

        // DCR регистрирует ровно тот redirect_uri, который поедет в /authorize и в обмен
        var registration = JsonDocument.Parse(http.BodyOf("https://auth.example.com/register")!);
        registration.RootElement.GetProperty("redirect_uris")[0].GetString().Should().Be(Redirect);

        var done = await service.CompleteAsync(start.State, "code-42");

        done.ServerKey.Should().Be("weather");
        var exchange = System.Web.HttpUtility.ParseQueryString(http.BodyOf("https://auth.example.com/token")!);
        exchange["grant_type"].Should().Be("authorization_code");
        exchange["code"].Should().Be("code-42");
        exchange["redirect_uri"].Should().Be(Redirect);
        exchange["resource"].Should().Be("https://mcp.example.com/mcp");
        // Verifier уходит серверу только на этом шаге — и он обязан сойтись с challenge
        McpPkce.Challenge(exchange["code_verifier"]!).Should().Be(query["code_challenge"]);

        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.Kind.Should().Be(McpAuthKind.OAuth2);
        saved.Auth.OAuth!.TokenEndpoint.Should().Be("https://auth.example.com/token");
        // Смена сигнатуры запуска обязательна: иначе живой CLI остался бы без токена
        saved.AuthVersion.Should().BeGreaterThan(versionBefore);

        var tokens = secrets.ResolveEntry(Owner, saved.Auth.OAuth!.AccessTokenRef)!;
        tokens.Value.Should().Be("access-1");
        tokens.RefreshToken.Should().Be("refresh-1");
        tokens.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddSeconds(3600), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Вход_ЧужойState_НеПринимается()
    {
        var (service, registry, _, _, http) = NewService();
        await service.StartAsync(Owner, NewRecord(registry), Redirect, input: null);

        var act = () => service.CompleteAsync("state-подделка", "code-42");

        await act.Should().ThrowAsync<McpOAuthException>()
            .WithMessage("*не найден или истёк*");
        http.BodyOf("https://auth.example.com/token").Should().BeNull("обмена кода быть не должно");
    }

    [Fact]
    public async Task Вход_ЧужойВладелец_НеПринимается()
    {
        var (service, registry, _, _, _) = NewService();
        var start = await service.StartAsync(Owner, NewRecord(registry), Redirect, input: null);

        var act = () => service.CompleteAsync(start.State, "code-42", expectedOwnerId: "owner2");

        await act.Should().ThrowAsync<McpOAuthException>();
    }

    [Fact]
    public async Task Вход_КодОдноразовый_ПовторОтвергается()
    {
        var (service, registry, _, _, _) = NewService();
        var start = await service.StartAsync(Owner, NewRecord(registry), Redirect, input: null);
        await service.CompleteAsync(start.State, "code-42");

        var act = () => service.CompleteAsync(start.State, "code-42");

        await act.Should().ThrowAsync<McpOAuthException>();
    }

    [Fact]
    public async Task Вход_ОтветПришёлНаДругойАдрес_Отвергается()
    {
        var (service, registry, _, _, http) = NewService();
        var start = await service.StartAsync(Owner, NewRecord(registry), Redirect, input: null);

        var act = () => service.CompleteAsync(start.State, "code-42",
            arrivedAt: "https://чужой-хост/api/mcp/oauth/callback");

        await act.Should().ThrowAsync<McpOAuthException>().WithMessage("*другой адрес*");
        http.BodyOf("https://auth.example.com/token").Should().BeNull();
    }

    [Fact]
    public async Task Вход_СвойRedirectUri_ЕдетИВРегистрациюИВЗапрос()
    {
        var (service, registry, _, _, http) = NewService();
        const string loopback = "http://127.0.0.1:33418/callback";

        var start = await service.StartAsync(Owner, NewRecord(registry), Redirect,
            new McpOAuthClientInput(null, null, null, loopback));

        start.RedirectUri.Should().Be(loopback);
        System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["redirect_uri"]
            .Should().Be(loopback);
        JsonDocument.Parse(http.BodyOf("https://auth.example.com/register")!)
            .RootElement.GetProperty("redirect_uris")[0].GetString().Should().Be(loopback);
    }

    [Fact]
    public async Task Вход_РучнойClientId_ОбходитсяБезРегистрации()
    {
        var (service, registry, _, _, http) = NewService();

        var start = await service.StartAsync(Owner, NewRecord(registry), Redirect,
            new McpOAuthClientInput("client-руками", null, null));

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["client_id"].Should().Be("client-руками");
        http.BodyOf("https://auth.example.com/register").Should().BeNull("DCR не нужен, client_id задан");
    }

    // ── обновление токена ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Рефреш_ИстёкшийТокенОбновляетсяПередХодом()
    {
        var (service, registry, secrets, _, http) = NewService();
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddSeconds(10));
        var versionBefore = record.AuthVersion;

        var fresh = await service.EnsureFreshAsync(Owner, record);

        fresh.Should().NotBeNull();
        var refresh = System.Web.HttpUtility.ParseQueryString(http.BodyOf("https://auth.example.com/token")!);
        refresh["grant_type"].Should().Be("refresh_token");
        refresh["refresh_token"].Should().Be("refresh-старый");
        refresh["resource"].Should().Be("https://mcp.example.com/mcp");

        var tokens = secrets.ResolveEntry(Owner, fresh!.Auth.OAuth!.AccessTokenRef)!;
        tokens.Value.Should().Be("access-1");
        fresh.AuthVersion.Should().BeGreaterThan(versionBefore);
    }

    [Fact]
    public async Task Рефреш_ЖивойТокен_НеТрогаетСеть()
    {
        var (service, registry, secrets, _, http) = NewService();
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));

        var fresh = await service.EnsureFreshAsync(Owner, record);

        fresh.Should().NotBeNull();
        fresh!.AuthVersion.Should().Be(record.AuthVersion);
        http.BodyOf("https://auth.example.com/token").Should().BeNull();
    }

    [Fact]
    public async Task Рефреш_Отказ401_СерверСнимаетсяСХодаИГоритНужденВход()
    {
        var (service, registry, secrets, statuses, _) = NewService(tokenStatus: HttpStatusCode.Unauthorized);
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddSeconds(5));

        var fresh = await service.EnsureFreshAsync(Owner, record);

        fresh.Should().BeNull("молча ходить с протухшим токеном нельзя");
        var status = statuses.Get(Owner, record.Key)!;
        status.Status.Should().Be(McpServerStatuses.NeedsAuth);
        status.Error.Should().Contain("вход");
    }

    [Fact]
    public async Task Рефреш_БезТокеновВовсе_НужденВход()
    {
        var (service, registry, _, statuses, _) = NewService();
        var record = NewRecord(registry);
        record.Auth = new McpAuthConfig { Kind = McpAuthKind.OAuth2, OAuth = new McpOAuthConfig() };

        var fresh = await service.EnsureFreshAsync(Owner, record);

        fresh.Should().BeNull();
        statuses.Get(Owner, record.Key)!.Status.Should().Be(McpServerStatuses.NeedsAuth);
    }

    // ── выдача наружу ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dto_ТокеновНеОтдаёт()
    {
        var (service, registry, _, _, _) = NewService();
        var record = NewRecord(registry);
        var start = await service.StartAsync(Owner, record, Redirect, input: null);
        await service.CompleteAsync(start.State, "code-42");

        var json = JsonSerializer.Serialize(McpServerMapper.ToDto(registry.Get(Owner, record.Id)!));

        json.Should().NotContain("access-1").And.NotContain("refresh-1");
        json.Should().Contain("\"HasTokens\":true");
    }

    // ── обвязка ──────────────────────────────────────────────────────────────────────

    private const string Redirect = "https://home.example.com/api/mcp/oauth/callback";

    private (McpOAuthService Service, McpRegistry Registry, McpSecretStore Secrets,
        McpStatusStore Statuses, StubHandler Http) NewService(
        HttpStatusCode tokenStatus = HttpStatusCode.OK)
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);
        var handler = new StubHandler(tokenStatus);
        var service = new McpOAuthService(registry, secrets, statuses,
            new StubHttpClientFactory(handler), config, NullLogger<McpOAuthService>.Instance);
        return (service, registry, secrets, statuses, handler);
    }

    private static McpServerRecord NewRecord(McpRegistry registry) =>
        registry.Create(Owner, new McpServerRecord
        {
            Key = "weather", Label = "Погода", Transport = McpTransport.Http, Url = ServerUrl,
        });

    // Запись, у которой вход уже пройден: токены в сторе, эндпоинты известны
    private static McpServerRecord Authorized(McpRegistry registry, McpSecretStore secrets,
        DateTime expiresAt)
    {
        var record = NewRecord(registry);
        var tokenRef = secrets.SetEntry(Owner, new McpSecretEntry
        {
            Value = "access-старый", RefreshToken = "refresh-старый", ExpiresAt = expiresAt,
        });
        record.Auth = new McpAuthConfig
        {
            Kind = McpAuthKind.OAuth2,
            OAuth = new McpOAuthConfig
            {
                AuthorizationServer = "https://auth.example.com",
                TokenEndpoint = "https://auth.example.com/token",
                ClientId = "client-from-dcr",
                AccessTokenRef = tokenRef,
                ExpiresAt = expiresAt,
                RedirectUri = Redirect,
            },
        };
        return registry.Update(Owner, record.Id, record)!;
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// Чужой сервер целиком: 401 с указанием метаданных, метаданные ресурса, метаданные
    /// authorization server, регистрация клиента и выдача токенов. Тела запросов запоминает —
    /// проверяем именно то, что ушло на провод.
    /// </summary>
    private sealed class StubHandler(HttpStatusCode tokenStatus) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        /// <summary>Тело запроса к адресу; null — обращения не было.</summary>
        public string? BodyOf(string url) => _bodies.GetValueOrDefault(url);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
            if (request.Content is not null)
                _bodies[url] = await request.Content.ReadAsStringAsync(ct);

            return url switch
            {
                ServerUrl => Unauthorized(),
                "https://mcp.example.com/.well-known/oauth-protected-resource/mcp" =>
                    Json("""{"resource":"https://mcp.example.com/mcp","authorization_servers":["https://auth.example.com"]}"""),
                "https://auth.example.com/.well-known/oauth-authorization-server" =>
                    Json("""
                         {"issuer":"https://auth.example.com",
                          "authorization_endpoint":"https://auth.example.com/authorize",
                          "token_endpoint":"https://auth.example.com/token",
                          "registration_endpoint":"https://auth.example.com/register",
                          "code_challenge_methods_supported":["S256"]}
                         """),
                "https://auth.example.com/register" =>
                    Json("""{"client_id":"client-from-dcr"}"""),
                "https://auth.example.com/token" => tokenStatus == HttpStatusCode.OK
                    ? Json("""{"access_token":"access-1","refresh_token":"refresh-1","expires_in":3600,"token_type":"Bearer"}""")
                    : new HttpResponseMessage(tokenStatus)
                    {
                        Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json"),
                    },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };

            static HttpResponseMessage Unauthorized()
            {
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.TryAddWithoutValidation("WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource/mcp\"");
                return response;
            }

            static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
