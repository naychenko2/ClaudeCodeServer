using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Интеграционные тесты gateway внешних модулей (ТЗ R2/R4/R9, контракт §3/§5.2):
/// реальный хост + фейковый echo-модуль на HttpListener, отражающий полученные заголовки.
/// Критичный по безопасности инвариант — срезка клиентских Authorization/X-AIHome-*
/// на входе и инъекция модульного RS256-токена (CT-2, CT-10).
/// </summary>
public class ModuleGatewayTests : IDisposable
{
    private sealed class ModuleFactory : TestWebApplicationFactory
    {
        public int ModulePort { get; } = FreePort();

        private static int FreePort()
        {
            var listener = new TcpListenerWrapper();
            return listener.Port;
        }

        private sealed class TcpListenerWrapper
        {
            public int Port { get; }
            public TcpListenerWrapper()
            {
                var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                l.Start();
                Port = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(TempDir);
            var moduleDir = Path.Combine(TempDir, "modules", "echo");
            Directory.CreateDirectory(moduleDir);
            File.WriteAllText(Path.Combine(moduleDir, "module.json"), $$"""
                {
                  "schemaVersion": "1.0",
                  "id": "test-echo",
                  "version": "0.1.0",
                  "displayName": "Echo",
                  "backend": {
                    "baseUrl": "http://127.0.0.1:{{ModulePort}}",
                    "healthPath": "/health",
                    "routePrefix": "/api/modules/test-echo"
                  },
                  "frontend": {
                    "remoteEntry": "/api/modules/test-echo/ui/remoteEntry.js",
                    "exposedModule": "./Tab",
                    "tab": { "label": "Echo", "icon": "zap", "order": 10 }
                  },
                  "scopes": ["echo.read"]
                }
                """);
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Modules:Path"] = Path.Combine(TempDir, "modules"),
                }));
        }
    }

    private readonly ModuleFactory _factory = new();
    private HttpListener? _module;
    private volatile string _lastHeadersJson = "{}";

    // Фейковый модуль: на любой запрос отвечает 200 и запоминает полученные заголовки
    private void StartModule()
    {
        _module = new HttpListener();
        _module.Prefixes.Add($"http://127.0.0.1:{_factory.ModulePort}/");
        _module.Start();
        _ = Task.Run(async () =>
        {
            while (_module.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _module.GetContextAsync(); }
                catch { break; }
                var headers = ctx.Request.Headers.AllKeys
                    .ToDictionary(k => k!, k => ctx.Request.Headers[k]);
                var payload = JsonSerializer.Serialize(headers);
                // Активный health-check YARP штатно опрашивает /health — он не должен
                // затирать заголовки проксированного data-plane запроса (иначе тест
                // «запрос без токена не дошёл» ложно видит пробу монитора)
                if (ctx.Request.Url?.AbsolutePath != "/health")
                    _lastHeadersJson = payload;
                var body = System.Text.Encoding.UTF8.GetBytes(payload);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
    }

    public void Dispose()
    {
        try { _module?.Stop(); } catch { }
        _factory.Dispose();
    }

    // --- R4/CT-10: срезка и инъекция ---

    [Fact]
    public async Task Gateway_InjectsModuleToken_AndStripsClientHeaders()
    {
        StartModule();
        using var client = _factory.CreateAuthenticatedClient();
        // Клиент пытается подделать identity-заголовки ядра
        client.DefaultRequestHeaders.Add("X-AIHome-User", "fake-admin");
        client.DefaultRequestHeaders.Add("X-AIHome-Scope", "root");

        var resp = await client.GetAsync("/api/modules/test-echo/whoami");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var received = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await resp.Content.ReadAsStringAsync())!;
        // CT-10: клиентские identity-заголовки до модуля не дошли
        received.Keys.Should().NotContain(k => k.StartsWith("X-AIHome-"),
            "клиентские X-AIHome-* обязаны срезаться на границе (§5.2)");
        // CT-2: вместо cc_token модуль получил RS256-токен со схемой §5.1
        received.Should().ContainKey("Authorization");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(
            received["Authorization"]["Bearer ".Length..]);
        jwt.Header.Alg.Should().Be("RS256");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("aihome-module:test-echo");
        jwt.Claims.First(c => c.Type == "chan").Value.Should().Be("gateway");
        jwt.Claims.First(c => c.Type == "scope").Value.Should().Be("echo.read");
        jwt.Subject.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Gateway_NoToken_401_RequestNeverReachesModule()
    {
        StartModule();
        _lastHeadersJson = "UNTOUCHED";
        using var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/modules/test-echo/whoami");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _lastHeadersJson.Should().Be("UNTOUCHED", "запрос без cc_token не должен достигать модуля");
    }

    [Fact]
    public async Task Gateway_GarbageToken_401()
    {
        StartModule();
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-jwt");

        var resp = await client.GetAsync("/api/modules/test-echo/whoami");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Gateway_UnknownModule_404()
    {
        using var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/modules/no-such-module/x");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- §7: ui-статика — публичная, без утечки cc_token ---

    [Fact]
    public async Task Gateway_UiPath_AnonymousAndNoAuthForwarded()
    {
        StartModule();
        using var client = _factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/modules/test-echo/ui/remoteEntry.js");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var received = JsonSerializer.Deserialize<Dictionary<string, string>>(
            await resp.Content.ReadAsStringAsync())!;
        received.Keys.Should().NotContain("Authorization", "cc_token не должен утекать модулю на ui-путях");
        resp.Headers.CacheControl!.ToString().Should().Contain("immutable");
    }

    [Fact]
    public async Task Gateway_UiPath_WorksWithoutAnyToken()
    {
        StartModule();
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/modules/test-echo/ui/chunk.js");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- R9/CT-7: деградация — модуль погашен ---

    [Fact]
    public async Task Gateway_ModuleDown_503WithContractShape()
    {
        // Модуль НЕ запускаем — соединение откажет
        using var client = _factory.CreateAuthenticatedClient();

        var resp = await client.GetAsync("/api/modules/test-echo/whoami");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resp.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(15));
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("module_unavailable");
        body.GetProperty("moduleId").GetString().Should().Be("test-echo");
        body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(15);
    }

    [Fact]
    public async Task CoreApis_WorkWhileModuleDown()
    {
        // CT-7/R9: /api/* ядра живут при погашенном модуле
        using var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/projects");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- R6/R8: список модулей и гейт видимости ---

    [Fact]
    public async Task ModulesList_ReturnsManifestData()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/modules");

        var item = body.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("id").GetString().Should().Be("test-echo");
        item.GetProperty("tab").GetProperty("label").GetString().Should().Be("Echo");
        item.GetProperty("remoteEntry").GetString().Should()
            .Be("/api/modules/test-echo/ui/remoteEntry.js?v=0.1.0");
        item.GetProperty("apiBase").GetString().Should().Be("/api/modules/test-echo");
    }

    [Fact]
    public async Task ModulesList_FlagOff_HidesModule_AndGatewayCloses()
    {
        StartModule();
        using var client = _factory.CreateAuthenticatedClient();
        var put = await client.PutAsJsonAsync("/api/feature-flags/module-test-echo", new { enabled = false });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/modules");
        body.GetProperty("items").GetArrayLength().Should().Be(0, "выключенный флагом модуль скрыт (R8)");

        var proxied = await client.GetAsync("/api/modules/test-echo/whoami");
        proxied.StatusCode.Should().Be(HttpStatusCode.NotFound, "gateway тоже закрыт для выключенного модуля");
    }

    // --- R3: JWKS публичный ---

    [Fact]
    public async Task Jwks_PublicEndpoint_ReturnsRsaKeys()
    {
        using var client = _factory.CreateClient(); // без аутентификации

        var body = await client.GetFromJsonAsync<JsonElement>("/.well-known/aihome-modules/jwks.json");

        var key = body.GetProperty("keys").EnumerateArray().First();
        key.GetProperty("kty").GetString().Should().Be("RSA");
        key.GetProperty("alg").GetString().Should().Be("RS256");
        key.GetProperty("kid").GetString().Should().MatchRegex("^[a-z0-9-]{8,64}$");
        key.GetProperty("n").GetString().Should().NotBeNullOrEmpty();
    }
}
