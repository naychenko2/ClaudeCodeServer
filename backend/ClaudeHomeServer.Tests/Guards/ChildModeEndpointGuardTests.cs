using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Guards;

/// <summary>
/// Сторож гейта И1 детского режима (KM-B2, ADR-041): ребёнок может только то, что помечено
/// [ChildAllowed]. Перебирает ВСЕ эндпоинты EndpointDataSource хоста: детский токен на любом
/// эндпоинте без метки → 403 child_mode; список размеченных заморожен здесь.
/// Слепая зона та же, что у AnonymousEndpointsGuardTests: ветки конвейера без эндпоинта
/// (WebDAV, /preview) и YARP-модули — для них отдельные тесты ниже и проверка в самих ветках.
/// </summary>
[Trait("Category", "Host")]
public class ChildModeEndpointGuardTests(TestWebApplicationFactory factory) : IClassFixture<TestWebApplicationFactory>
{
    // Заморожено: «МЕТОДЫ шаблон» эндпоинтов с [ChildAllowed]. Новая метка — правка этого списка
    // осознанным решением (расширение детской поверхности).
    private static readonly string[] FrozenAllowed =
    [
        "GET api/auth/me",
        "POST api/auth/login",
        "POST api/tts",
        "ANY /hubs/session",
        "ANY /hubs/session/negotiate",
        // SPA-фолбэк — интерфейс, а не управляющая поверхность
        "GET,HEAD {*path:nonfile}",
    ];

    private void SetChild(bool enabled = true, bool valid = true)
    {
        var user = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.SecondUsername)!;
        user.ChildMode = new ChildProfile
        {
            Enabled = enabled,
            TutorPersonaId = valid ? "tutor" : "",
            ProviderKey = "key",
            Model = "sonnet",
        };
    }

    private HttpClient ChildClient() =>
        factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    // Управляющая поверхность: REST и хабы. Остальное (статика, .well-known) — не гейт.
    private static bool IsManagedSurface(string raw) =>
        raw.StartsWith("api", StringComparison.OrdinalIgnoreCase)
        || raw.StartsWith("hubs/", StringComparison.OrdinalIgnoreCase);

    private static string Describe(RouteEndpoint e)
    {
        var methods = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } m
            ? string.Join(",", m.Order(StringComparer.Ordinal)) : "ANY";
        return $"{methods} {e.RoutePattern.RawText}";
    }

    private IEnumerable<RouteEndpoint> Endpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    [Fact]
    public void ChildAllowedList_IsFrozen()
    {
        var actual = Endpoints()
            .Where(e => e.Metadata.GetMetadata<ChildAllowedAttribute>() is not null)
            .Select(Describe).Distinct().Order(StringComparer.Ordinal).ToArray();
        actual.Should().Equal(FrozenAllowed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ChildToken_OnEveryEndpointWithoutChildAllowed_IsRefused()
    {
        SetChild();
        using var client = ChildClient();
        // Проверяем только API и хабы: статика фронта (SPA-фолбэк) ребёнку отдавать можно —
        // это не управляющая поверхность, а файлы приложения.
        var denied = Endpoints().Where(e =>
                e.Metadata.GetMetadata<ChildAllowedAttribute>() is null
                && IsManagedSurface(e.RoutePattern.RawText ?? ""))
            .ToList();
        denied.Should().NotBeEmpty();

        var leaks = new List<string>();
        foreach (var e in denied)
        {
            var method = e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.FirstOrDefault() ?? "GET";
            var path = System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText ?? "/", @"\{[^}]*\}", "x");
            using var req = new HttpRequestMessage(new HttpMethod(method), path);
            var resp = await client.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            // Утечка — только успешный ответ. Отказ (4xx) может прийти из гейта
            // (403 child_mode), из своей схемы авторизации (401 — токен устройства)
            // или из правила роли (403 без тела) — всё это «ребёнку закрыто».
            var ok = (int)resp.StatusCode is >= 200 and < 400;
            if (ok) leaks.Add($"{Describe(e)} → {(int)resp.StatusCode} {body}");
        }
        leaks.Should().BeEmpty("ребёнку закрыто всё, что не помечено [ChildAllowed]");
    }

    [Fact]
    public async Task ChildToken_OnAllowedEndpoint_Passes()
    {
        SetChild();
        using var client = ChildClient();
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        var tts = await client.PostAsync("/api/tts", new StringContent("{}", Encoding.UTF8, "application/json"));
        (await tts.Content.ReadAsStringAsync()).Should().NotContain("child_mode");
    }

    [Fact]
    public async Task ChildToken_OnPlainEndpoint_Gets403ChildMode()
    {
        SetChild();
        using var client = ChildClient();
        var resp = await client.GetAsync("/api/projects");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    [Fact]
    public async Task DenyAllProfile_Gets403_EvenOnAllowedEndpoint()
    {
        SetChild(valid: false);
        using var client = ChildClient();
        var me = await client.GetAsync("/api/auth/me");
        me.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DisabledProfile_IsOrdinaryUser()
    {
        SetChild(enabled: false);
        using var client = ChildClient();
        (await client.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdultToken_IsNotAffected()
    {
        SetChild();
        using var admin = factory.CreateAuthenticatedClient();
        (await admin.GetAsync("/api/projects")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WebDav_ChildBasic_Gets403_AdultPasses()
    {
        SetChild();
        using var client = factory.CreateClient();
        HttpRequestMessage Propfind(string user, string pass)
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), "/projects/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
            return req;
        }

        var child = await client.SendAsync(Propfind(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword));
        child.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var adult = await client.SendAsync(Propfind(TestWebApplicationFactory.TestUsername, TestWebApplicationFactory.TestPassword));
        adult.StatusCode.Should().NotBe(HttpStatusCode.Forbidden).And.NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Preview_ChildCookieToken_Gets403()
    {
        SetChild();
        var token = factory.GetToken(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/preview/some-project/");
        req.Headers.Add("Cookie", $"cc_preview={token}");
        req.Headers.Add("Sec-Fetch-Site", "same-origin");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }
}

/// <summary>Пустой владелец в гейте — fail-closed (замечание ревью R-KM-B1b, минор 1).</summary>
public class ChildModeGateEmptyOwnerTests
{
    private sealed class NullPolicy : IChildModePolicy
    {
        public ChildPolicySnapshot? For(string ownerId) => null;
    }

    private static async Task<(int Status, bool NextCalled)> Run(System.Security.Claims.ClaimsPrincipal user)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user };
        ctx.Response.Body = new MemoryStream();
        var called = false;
        var gate = new ChildModeGateMiddleware(_ => { called = true; return Task.CompletedTask; });
        await gate.InvokeAsync(ctx, new NullPolicy());
        return (ctx.Response.StatusCode, called);
    }

    [Fact]
    public async Task AuthenticatedWithoutSub_Gets403()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("name", "x")], "test"));
        (await Run(user)).Should().Be((403, false));
    }

    [Fact]
    public async Task Anonymous_Passes()
    {
        (await Run(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity()))).Should().Be((200, true));
    }
}
