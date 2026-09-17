using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Регрессия входа Higgsfield (2026-09-17): собственный callback-путь
/// <c>/api/higgsfield/callback</c> разъехался с Clerk DCR-клиентом и вход падал с
/// «redirect_uri does not match any pre-registered url». Лечение — Connect берёт
/// общий путь через <see cref="McpOAuthService.ResolveRedirectUri"/>, и эти
/// тесты фиксируют контракт.
/// </summary>
public class HiggsfieldConnectCallbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ccs-hf-cb-" + Guid.NewGuid().ToString("N")[..8]);

    public HiggsfieldConnectCallbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task ConnectAsync_ВозвращаетОбщийCallbackПутьИзResolveRedirectUri()
    {
        var (service, oauth, registry) = NewService();
        SeedServiceRecordWithOAuth(registry,
            redirectUri: "http://test:5000" + McpOAuthService.CallbackPath);

        var origin = "http://test:5000";
        var redirectUri = oauth.ResolveRedirectUri(origin);
        redirectUri.Should().Be(origin + McpOAuthService.CallbackPath,
            "ResolveRedirectUri обязан давать общий путь — это источник правды для redirect_uri");

        var (authorizeUrl, state, returnedRedirect) =
            await service.ConnectAsync("admin-1", redirectUri);

        returnedRedirect.Should().Be(redirectUri,
            "Connect обязан вернуть тот же redirect_uri, что передали — никакой подмены на свой путь");
        // redirect_uri в authorize-URL закодирован (Uri.EscapeDataString), поэтому
        // проверяем через декодирование, а не вхождение подстроки (слэши превращаются в %2F).
        Uri.UnescapeDataString(authorizeUrl).Should().Contain(redirectUri,
            "authorize-URL должен включать тот же redirect_uri, что провайдер ожидает на return");
        state.Should().NotBeNullOrEmpty("state — основа авторизации callback");

        // Тест на pending: сохранённый в Higgsfield-сервисе redirect_uri должен
        // совпадать с общим путём, иначе legacy CompleteAsync (если провайдер придёт
        // по старому адресу) обменяет код на чужом redirect_uri.
        var pendingField = typeof(HiggsfieldOAuthService).GetField("_pending",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var pending = (System.Collections.IDictionary)pendingField.GetValue(service)!;
        pending.Contains(state).Should().BeTrue(
            "Connect обязан сохранить pending-запись для callback");
        var entry = pending[state]!;
        var redirectField = entry.GetType().GetProperty("RedirectUri")!;
        ((string)redirectField.GetValue(entry)!).Should().Be(redirectUri);
    }

    [Fact]
    public async Task ConnectAsync_СPublicBaseUrl_ПодставляетЕгоИОбщийПуть()
    {
        var (service, oauth, registry) = NewService(publicBaseUrl: "https://naychenko.me");
        SeedServiceRecordWithOAuth(registry,
            redirectUri: "https://naychenko.me" + McpOAuthService.CallbackPath);

        // origin другой — но Mcp:PublicBaseUrl обязан победить (он настроен сильнее).
        var redirectUri = oauth.ResolveRedirectUri("http://localhost:5000");
        redirectUri.Should().Be("https://naychenko.me" + McpOAuthService.CallbackPath);

        var (_, _, returnedRedirect) =
            await service.ConnectAsync("admin-1", redirectUri);

        returnedRedirect.Should().Be("https://naychenko.me" + McpOAuthService.CallbackPath,
            "с PublicBaseUrl Connect идёт под настроенным именем — иначе туннель/прокси зарегистрирует чужой redirect_uri");
    }

    [Fact]
    public void NotifyCompletedAsync_StateНеИзPending_NoOp()
    {
        var (service, _, _) = NewService();

        var result = service.NotifyCompletedAsync("unknown-state");

        result.Should().BeFalse(
            "чужой или повторный callback не должен ронять состояние или бросать");
    }

    [Fact]
    public async Task NotifyCompletedAsync_StateИзPending_СтавитConnectedИЧиститPending()
    {
        var (service, oauth, registry) = NewService();
        SeedServiceRecordWithOAuth(registry,
            redirectUri: "http://test:5000" + McpOAuthService.CallbackPath);

        // Делаем Connect, чтобы в _pending появилась запись
        var (_, state, _) = await service.ConnectAsync(
            "admin-2", oauth.ResolveRedirectUri("http://test:5000"));

        // Уведомляем о завершении через общий callback
        var ok = service.NotifyCompletedAsync(state);

        ok.Should().BeTrue("pending-запись есть — значит, это наш вход");
        var st = service.LoadState();
        st.Connected.Should().BeTrue("NotifyCompletedAsync ставит Connected");
        st.AdminOwnerId.Should().Be("admin-2",
            "adminOwnerId берётся из pending, не из claim");

        // Повторный вызов с тем же state — no-op, не бросает
        var again = service.NotifyCompletedAsync(state);
        again.Should().BeFalse("повторный callback того же входа — pending уже снят");
    }

    // ── Вспомогательные ─────────────────────────────────────────────────────────────────

    private (HiggsfieldOAuthService Service, McpOAuthService Oauth, McpRegistry Registry) NewService(
        string? publicBaseUrl = null)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
        };
        if (publicBaseUrl is not null) dict["Mcp:PublicBaseUrl"] = publicBaseUrl;

        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var secrets = new McpSecretStore(config);
        var registry = new McpRegistry(config, secrets);
        var statuses = new McpStatusStore(config);
        var oauth = new McpOAuthService(registry, secrets, statuses,
            new NotFoundHandlerFactory(), config, NullLogger<McpOAuthService>.Instance);
        var service = new HiggsfieldOAuthService(registry, secrets, statuses, oauth,
            config, NullLogger<HiggsfieldOAuthService>.Instance);
        return (service, oauth, registry);
    }

    // Запись с уже заполненным OAuth, чтобы StartAsync не ходил в DCR и не делал
    // discovery в сеть. AuthorizationServer/TokenEndpoint заданы — FindIssuerAsync
    // сразу вернёт известный issuer (строка McpOAuthService.cs:397). ClientId и
    // RedirectUri совпадают с тем, что мы передадим в Connect — redirectMismatch=false.
    private static void SeedServiceRecordWithOAuth(McpRegistry registry, string redirectUri)
    {
        registry.CreateBuiltIn(HiggsfieldOAuthService.ServiceOwnerId,
            new McpServerRecord
            {
                Key = HiggsfieldOAuthService.Key,
                Label = HiggsfieldOAuthService.Label,
                Url = "https://test.local/mcp",
                Transport = McpTransport.Http,
                Auth = new McpAuthConfig
                {
                    Kind = McpAuthKind.OAuth2,
                    OAuth = new McpOAuthConfig
                    {
                        AuthorizationServer = "https://test.local",
                        TokenEndpoint = "https://test.local/token",
                        ClientId = "client-test",
                        RedirectUri = redirectUri,
                    },
                },
                Enabled = true,
            });
    }

    private sealed class NotFoundHandlerFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}