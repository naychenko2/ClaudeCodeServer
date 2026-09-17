using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        // registration_endpoint опционален по RFC 8414; если провайдер его не объявил —
        // выдумывать путь нельзя, иначе RegisterClientAsync отправит POST в никуда.
        endpoints.RegistrationEndpoint.Should().BeNull();
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

    // Регресс Higgsfield 2026-09-15: клиент зарегистрирован под общий callback
    // /api/mcp/oauth/callback, а вход теперь идёт через собственный /api/higgsfield/callback.
    // Без проверки authorize уехал бы со старым client_id и новым redirect_uri —
    // провайдер (Clerk) отбивает «redirect_uri does not match any pre-registered url»
    // и повторный «Войти» не помогает. Проверка закрывает весь класс: смена домена,
    // туннель, переезд пути.
    [Fact]
    public async Task Вход_РазошелсяRedirectUri_ПринудительнаяПеререгистрация()
    {
        var (service, registry, secrets, _, http) = NewService();
        // Запись с OAuth-конфигом и старым redirect_uri; refresh-токен уже в сторе
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));
        record.Auth.OAuth!.RedirectUri.Should().Be(Redirect);

        const string newRedirect = "https://home.example.com/api/higgsfield/callback";
        newRedirect.Should().NotBe(Redirect, "это и есть суть теста — разные адреса");

        var start = await service.StartAsync(Owner, record, newRedirect, input: null);

        // DCR дошёл до провайдера под новый redirect_uri — иначе authorize сломался бы
        http.BodyOf("https://auth.example.com/register").Should().NotBeNull(
            "при mismatch хранимый клиент непригоден — нужна перерегистрация");
        var registration = JsonDocument.Parse(http.BodyOf("https://auth.example.com/register")!);
        registration.RootElement.GetProperty("redirect_uris")[0].GetString().Should().Be(newRedirect);

        // authorize уехал с тем же новым redirect_uri
        System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["redirect_uri"]
            .Should().Be(newRedirect);

        // Запись обновилась; refresh-токен в той же McpSecretEntry — сброс ClientId его не задел
        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.RedirectUri.Should().Be(newRedirect);
        saved.Auth.OAuth!.AccessTokenRef.Should().NotBeNullOrEmpty(
            "AccessTokenRef копируется в новый McpOAuthConfig при сохранении");
        var tokens = secrets.ResolveEntry(Owner, saved.Auth.OAuth!.AccessTokenRef)!;
        tokens.RefreshToken.Should().Be("refresh-старый",
            "refresh-токен лежит в той же записи стора, что и access — перерегистрация его не трогает");
    }

    [Fact]
    public async Task Вход_СовпадаетRedirectUri_DcrНеЗапускается()
    {
        var (service, registry, secrets, _, http) = NewService();
        // Authorized: RedirectUri = Redirect, ClientId = "client-from-dcr"
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));

        var start = await service.StartAsync(Owner, record, Redirect, input: null);

        http.BodyOf("https://auth.example.com/register").Should().BeNull(
            "RedirectUri совпадает — хранимый клиент пригоден, плодить новых не надо");
        System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["client_id"]
            .Should().Be("client-from-dcr", "используется сохранённый client_id");
    }

    // Сбрасываем клиента — сбрасываем и его секрет. Старый код сбрасывал ClientId при
    // mismatch, но ClientSecretRef оставлял от прежнего клиента: новый публичный DCR
    // (Higgsfield через Clerk) не вернёт секрет, и при обмене кода уехал бы чужой.
    // Сейчас не стреляет (Higgsfield — публичный клиент), но логически обязательно
    // держать пары в унисон. Задача eefcb96a.
    [Fact]
    public async Task Вход_РазошелсяRedirectUri_СбрасываетClientSecretRef()
    {
        var (service, registry, secrets, _, _) = NewService();
        // Запись с заполненным ClientSecretRef — эмулируем прежний «секретный» клиент
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));
        var oldSecretRef = secrets.Set(Owner, "secret-старый");
        record.Auth.OAuth!.ClientSecretRef = oldSecretRef;
        record = registry.Update(Owner, record.Id, record)!;
        record.Auth.OAuth!.ClientSecretRef.Should().Be(oldSecretRef,
            "это и есть исходное состояние — сбрасывать есть что");

        const string newRedirect = "https://home.example.com/api/higgsfield/callback";
        await service.StartAsync(Owner, record, newRedirect, input: null);

        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.ClientSecretRef.Should().BeNullOrEmpty(
            "при mismatch идём в DCR — хранить чужой секрет нельзя, новый клиент может быть публичным");
        // Соседние секреты (access/refresh) лежат в одной записи стора, но ClientSecretRef
        // и AccessTokenRef — разные поля McpOAuthConfig; сброс одного не трогает другой.
        saved.Auth.OAuth!.AccessTokenRef.Should().NotBeNullOrEmpty(
            "access/refresh живут в AccessTokenRef — DCR их не задевает, только ClientSecretRef");
    }

    // Ручной client_id из формы — явное решение человека. Проверка redirect_uri
    // его не перебивает: дальше человек сам разбирается со своим провайдером
    // (его client_id зарегистрирован под конкретный redirect_uri — это его ответственность).
    [Fact]
    public async Task Вход_РучнойClientIdПриMismatch_НеТриггеритПеререгистрацию()
    {
        var (service, registry, secrets, _, http) = NewService();
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));
        const string newRedirect = "https://home.example.com/api/higgsfield/callback";

        var start = await service.StartAsync(Owner, record, newRedirect,
            new McpOAuthClientInput("client-руками", null, null));

        http.BodyOf("https://auth.example.com/register").Should().BeNull(
            "input.ClientId задан — DCR не запускается независимо от mismatch");
        System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["client_id"]
            .Should().Be("client-руками");
    }

    // Сервер без DCR: человек однажды вписал client_id руками, у провайдера
    // зарегистрированы оба адреса возврата (типичный кейс — статический клиент
    // стороннего MCP-сервера). Mcp:PublicBaseUrl не задан: redirectUri получается
    // из origin запроса и меняется при смене стенда (порт 5000 ↔ 5173 у Vite).
    // До правки код сбрасывал client_id на mismatch — вход падал на «впиши client_id
    // вручную». После правки откатываемся на сохранённый client_id с WARN; решает
    // провайдер. Задача 39a034c7.
    [Fact]
    public async Task Вход_РазошелсяRedirectUri_НетDcr_ОткатНаПрежнийClientId()
    {
        var (svc, registry, secrets, statuses, handler) = NewService();
        handler.IncludeRegistrationEndpoint = false;

        var log = new RecordingLogger<McpOAuthService>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        }).Build();
        var service = new McpOAuthService(registry, secrets, statuses,
            new StubHttpClientFactory(handler), config, log);

        // Прежний вход был с ручным client_id: эмулируем запись клиента как к
        // человек когда-то вписал client_id вручную.
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));
        const string manualClient = "client-руками-старый";
        record.Auth.OAuth!.ClientId = manualClient;
        record = registry.Update(Owner, record.Id, record)!;
        record.Auth.OAuth!.RedirectUri.Should().Be(Redirect,
            "исходное состояние — RedirectUri сохранён от первого входа");

        // Дев-стенд: Vite на 5173, бэк на 5000. Host остаётся 5173.
        const string newRedirect = "http://localhost:5173/api/mcp/oauth/callback";
        newRedirect.Should().NotBe(Redirect, "иначе это не сценарий mismatch");

        var start = await service.StartAsync(Owner, record, newRedirect, input: null);

        // Без DCR запроса быть не должно: провайдер не объявил registration_endpoint —
        // POST /register ушёл бы в никуда. Прежний client_id пригоден.
        handler.BodyOf("https://auth.example.com/register").Should().BeNull(
            "DCR недоступна — перерегистрировать нечем, используем прежний client_id");

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["client_id"].Should().Be(manualClient,
            "сохранённый client_id годен — у провайдера могут быть зарегистрированы оба адреса");
        query["redirect_uri"].Should().Be(newRedirect,
            "authorize едет на текущий redirect_uri — пусть провайдер решит сам");

        log.HasWarningContaining("DCR недоступна").Should().BeTrue(
            "WARN человеку: адрес разошёлся, перерегистрировать нечем, пробуем прежним client_id");
    }

    // Контраст: для серверов С DCR поведение прежнее — клиент сбрасывается, идём в
    // регистрацию, secret тоже сбрасывается. Сторож от регрессии при правке discovery.
    [Fact]
    public async Task Вход_РазошелсяRedirectUri_ЕстьDcr_СбрасываетClientIdКакРаньше()
    {
        var (service, registry, secrets, _, http) = NewService();
        // IncludeRegistrationEndpoint=true по умолчанию в StubHandler
        var record = Authorized(registry, secrets, expiresAt: DateTime.UtcNow.AddHours(1));
        record.Auth.OAuth!.ClientId.Should().NotBeNullOrEmpty("исходное состояние — клиент сохранён");

        const string newRedirect = "http://localhost:5173/api/mcp/oauth/callback";
        var start = await service.StartAsync(Owner, record, newRedirect, input: null);

        http.BodyOf("https://auth.example.com/register").Should().NotBeNull(
            "DCR объявлена — на mismatch прежний клиент непригоден, нужна перерегистрация");
        System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["client_id"]
            .Should().Be("client-from-dcr",
            "DCR вернул нового клиента — он и уехал в authorize");
    }

    // ── scope: источник правды — ответ DCR, не scopes_supported ─────────────────────

    [Fact]
    public async Task Вход_DcrВернулScope_ОнУходитВAuthorize()
    {
        var (service, registry, _, _, handler) = NewService();
        handler.RegistrationScope = "openid profile email";

        var record = NewRecord(registry);
        var start = await service.StartAsync(Owner, record, Redirect, input: null);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["scope"].Should().Be("openid profile email");
        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.Scopes.Should().Equal("openid", "profile", "email");
    }

    [Fact]
    public async Task Вход_DcrБезScope_ПараметрScopeВAuthorizeОтсутствует()
    {
        var (service, registry, _, _, _) = NewService();

        var record = NewRecord(registry);
        var start = await service.StartAsync(Owner, record, Redirect, input: null);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["scope"].Should().BeNull(
            "без ответа DCR слать scope в authorize нельзя: дефолт клиента заведомо разрешён");
        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.Scopes.Should().BeNull(
            "угаданного набора в записи быть не должно — иначе повторный вход упирается в то же");
    }

    [Fact]
    public async Task Вход_ScopesSupportedВМетаданных_НеПопадаетВAuthorize()
    {
        // Имитируем Clerk (Higgsfield): сервер отдаёт богатый набор возможностей —
        // именно этот случай ломал вход в Higgsfield.
        var (service, registry, _, _, handler) = NewService();
        handler.AuthorizationServerScopesSupported =
        [
            "openid", "profile", "email", "public_metadata", "private_metadata",
            "offline_access", "user:org:read",
        ];

        var record = NewRecord(registry);
        var start = await service.StartAsync(Owner, record, Redirect, input: null);

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["scope"].Should().BeNull(
            "scopes_supported описывает возможности СЕРВЕРА, а не права КЛИЕНТА — провайдер отбил бы запрос");
        query.AllKeys.Should().NotContain("scope");
        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.Scopes.Should().BeNull();
    }

    [Fact]
    public async Task Вход_РучнойScopeВInputs_ИдётВAuthorize()
    {
        var (service, registry, _, _, _) = NewService();
        var record = NewRecord(registry);

        var start = await service.StartAsync(Owner, record, Redirect,
            new McpOAuthClientInput(null, null, ["custom:a", "custom:b"]));

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["scope"].Should().Be("custom:a custom:b");
        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.Scopes.Should().Equal("custom:a", "custom:b");
    }

    [Fact]
    public async Task Вход_РучнойClientIdИЧеловекЗадалScope_УходятВAuthorize()
    {
        // Ручной client_id — DCR не было; за scope отвечает человек.
        var (service, registry, _, _, http) = NewService();

        var start = await service.StartAsync(Owner, NewRecord(registry), Redirect,
            new McpOAuthClientInput("client-руками", null, ["x", "y"]));

        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        query["scope"].Should().Be("x y");
        http.BodyOf("https://auth.example.com/register").Should().BeNull();
    }

    [Fact]
    public async Task Вход_Неудачный_DcrБезScope_ЗаписьОстаётсяБезУгаданныхScope()
    {
        // Защита от отравления: scopes_supported в метаданных есть, DCR-ответа без scope —
        // иначе одна неудачная попытка навсегда закрепит scopes_supported в записи,
        // и повторный «Войти» упирается в то же самое. После фикса в записи null,
        // повторный вход пройдёт с чистого DCR.
        var (service, registry, _, _, handler) = NewService();
        handler.AuthorizationServerScopesSupported = ["a", "b", "c"]; // провайдер расщедрился

        var record = NewRecord(registry);
        await service.StartAsync(Owner, record, Redirect, input: null);
        // Без CompleteAsync — это и есть «неудачный вход» (человек закрыл окно).

        var saved = registry.Get(Owner, record.Id)!;
        saved.Auth.OAuth!.Scopes.Should().BeNull(
            "scopes_supported в запись копировать нельзя — иначе вход отравится навсегда");
    }

    // ── таймаут discovery ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Вход_НедоступныйСерверАвторизации_УкладываетсяВПотолокИДаётПонятнуюОшибку()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ccs-mcp-oauth-hang-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(dir, "projects.json"),
                ["Mcp:OAuthDiscoveryTimeoutSeconds"] = "1",
                ["Mcp:OAuthDiscoveryOverallTimeoutSeconds"] = "5",
            }).Build();
            var registry = new McpRegistry(config, new McpSecretStore(config));
            var service = new McpOAuthService(registry, new McpSecretStore(config), new McpStatusStore(config),
                new StubHttpClientFactory(new HangingHandler()), config, NullLogger<McpOAuthService>.Instance);
            var record = NewRecord(registry);

            var stopwatch = Stopwatch.StartNew();
            var act = () => service.StartAsync(Owner, record, Redirect, input: null);
            var thrown = await act.Should().ThrowAsync<McpOAuthException>();
            stopwatch.Stop();

            thrown.Which.Message.Should().Contain("не отвечает");
            // Раньше та же недоступность authorization server держала запрос 90-100с
            // (стандартные таймауты HttpClient складывались по цепочке discovery)
            stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15),
                "общий потолок discovery не должен складываться в минуты ожидания");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* уборка best-effort */ }
        }
    }

    // Сервер, который никогда не отвечает — имитация мёртвого/зависшего authorization server
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("недостижимо — Delay должен был отмениться раньше");
        }
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

    // Простой логгер-ловушка: пишет всё в список пар (уровень, сообщение), чтобы тест
    // мог проверить наличие конкретного WARN. Используется только в кейсах, где
    // поведение логирования — часть контракта (например, откат на сохранённый client_id
    // при redirectMismatch без DCR должен сопровождаться предупреждением в лог).
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        public bool HasWarningContaining(string fragment) =>
            Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(fragment));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
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

        /// <summary>Scope, который вернёт /register. Пусто/null — поле scope отсутствует.</summary>
        public string? RegistrationScope { get; set; }

        /// <summary>Scopes_supported в метаданных authorization server. null — поля нет.</summary>
        public IReadOnlyList<string>? AuthorizationServerScopesSupported { get; set; }

        /// <summary>
        /// Признак «провайдер поддерживает DCR»: отдавать ли <c>registration_endpoint</c>
        /// в метаданных authorization server. По умолчанию true — большинство тестов
        /// опирают DCR; кейс «без DCR» включается явно.
        /// </summary>
        public bool IncludeRegistrationEndpoint { get; set; } = true;

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
                    Json(BuildAuthorizationServerMetadata()),
                "https://auth.example.com/register" =>
                    Json(BuildRegistrationResponse()),
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

            // scopes_supported не обязаны быть в ответе — оставлены как опциональное поле,
            // чтобы тесты могли убедиться: даже если провайдер их отдаёт, мы их в authorize
            // не подставляем. registration_endpoint тоже опционален (RFC 8414): выключаем
            // полем IncludeRegistrationEndpoint, чтобы покрыть кейс «провайдер без DCR».
            string BuildAuthorizationServerMetadata()
            {
                var registration = IncludeRegistrationEndpoint
                    ? "\"registration_endpoint\":\"https://auth.example.com/register\","
                    : string.Empty;
                if (AuthorizationServerScopesSupported is null)
                    return "{\"issuer\":\"https://auth.example.com\"," +
                           "\"authorization_endpoint\":\"https://auth.example.com/authorize\"," +
                           "\"token_endpoint\":\"https://auth.example.com/token\"," +
                           registration +
                           "\"code_challenge_methods_supported\":[\"S256\"]}";
                var scopes = string.Join(",",
                    AuthorizationServerScopesSupported.Select(s => "\"" + s + "\""));
                return "{\"issuer\":\"https://auth.example.com\"," +
                       "\"authorization_endpoint\":\"https://auth.example.com/authorize\"," +
                       "\"token_endpoint\":\"https://auth.example.com/token\"," +
                       registration +
                       "\"code_challenge_methods_supported\":[\"S256\"]," +
                       "\"scopes_supported\":[" + scopes + "]}";
            }

            // RFC 7591 §3.2.1: scope присутствует, если отличается от запрошенного.
            string BuildRegistrationResponse()
            {
                if (RegistrationScope is null) return """{"client_id":"client-from-dcr"}""";
                var encoded = RegistrationScope.Replace("\\", "\\\\").Replace("\"", "\\\"");
                return "{\"client_id\":\"client-from-dcr\",\"scope\":\"" + encoded + "\"}";
            }
        }
    }
}
