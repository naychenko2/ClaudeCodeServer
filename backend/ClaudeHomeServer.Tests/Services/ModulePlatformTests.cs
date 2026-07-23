using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Modules;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Юнит-тесты платформы внешних модулей (ТЗ R1–R3):
/// реестр манифестов, модульные RS256-токены + JWKS, YARP-конфиг из реестра.
/// </summary>
public class ModulePlatformTests : IDisposable
{
    private readonly string _tempDir;

    public ModulePlatformTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "modules_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static string ManifestJson(string id, string schemaVersion = "1.0",
        string? routePrefix = null, string mcp = "") => $$"""
        {
          "schemaVersion": "{{schemaVersion}}",
          "id": "{{id}}",
          "version": "0.1.0",
          "displayName": "Тестовый модуль",
          "backend": {
            "baseUrl": "http://127.0.0.1:5100",
            "healthPath": "/health",
            "routePrefix": "{{routePrefix ?? $"/api/modules/{id}"}}"
          }{{mcp}}
        }
        """;

    private string WriteModule(string dirName, string json)
    {
        var dir = Path.Combine(_tempDir, "modules", dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "module.json"), json);
        return dir;
    }

    private ModuleRegistry CreateRegistry(params (string key, string? value)[] extraConfig)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Modules:Path"] = Path.Combine(_tempDir, "modules"),
        };
        foreach (var (k, v) in extraConfig) settings[k] = v;
        return new ModuleRegistry(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<ModuleRegistry>.Instance);
    }

    // --- R1: ModuleRegistry ---

    [Fact]
    public void Registry_LoadsValidManifest()
    {
        WriteModule("echo", ManifestJson("module-echo"));

        var registry = CreateRegistry();

        registry.All.Should().HaveCount(1);
        var m = registry.Get("module-echo")!;
        m.Manifest.DisplayName.Should().Be("Тестовый модуль");
        m.Audience.Should().Be("aihome-module:module-echo");
        m.FeatureFlagKey.Should().Be("module-module-echo");
    }

    [Fact]
    public void Registry_BrokenJson_IsSkipped_CoreStarts()
    {
        WriteModule("broken", "{ это не JSON ]");
        WriteModule("ok", ManifestJson("ok-module"));

        var registry = CreateRegistry();

        registry.All.Should().HaveCount(1);
        registry.Get("ok-module").Should().NotBeNull();
    }

    [Fact]
    public void Registry_IncompatibleMajor_IsSkipped()
    {
        WriteModule("v2", ManifestJson("future-module", schemaVersion: "2.0"));

        CreateRegistry().All.Should().BeEmpty();
    }

    [Fact]
    public void Registry_NewerMinor_IsLoaded()
    {
        WriteModule("minor", ManifestJson("minor-module", schemaVersion: "1.7"));

        CreateRegistry().Get("minor-module").Should().NotBeNull(
            "минор выше поддерживаемого — работа на общих полях (§8)");
    }

    [Fact]
    public void Registry_ForeignRoutePrefix_IsSkipped()
    {
        // Чужой префикс дал бы модулю перехват путей ядра — жёсткий якорь мажора 1
        WriteModule("evil", ManifestJson("evil-module", routePrefix: "/api/auth"));

        CreateRegistry().All.Should().BeEmpty();
    }

    [Fact]
    public void Registry_DuplicateId_SecondIsSkipped()
    {
        WriteModule("a", ManifestJson("dup-module"));
        WriteModule("b", ManifestJson("dup-module"));

        CreateRegistry().All.Should().HaveCount(1);
    }

    [Fact]
    public void Registry_ExplicitManifestPathFromConfig_IsLoaded()
    {
        var dir = Path.Combine(_tempDir, "elsewhere");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "module.json"), ManifestJson("outside-module"));

        var registry = CreateRegistry(("Modules:Manifests:0", Path.Combine(dir, "module.json")));

        registry.Get("outside-module").Should().NotBeNull();
    }

    // --- R3: ModuleTokenService ---

    private ModuleTokenService CreateTokenService() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build(),
        NullLogger<ModuleTokenService>.Instance);

    private LoadedModule EchoModule()
    {
        WriteModule("echo", ManifestJson("module-echo"));
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(ManifestJson("module-echo"))!;
        manifest.Scopes = ["echo.read", "echo.write"];
        return new LoadedModule(manifest, _tempDir);
    }

    [Fact]
    public void Token_GatewayChannel_HasFrozenClaimSchema()
    {
        var svc = CreateTokenService();
        var before = DateTime.UtcNow;

        var token = svc.Issue(EchoModule(), "user-1", "Андрей", "gateway");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Header.Alg.Should().Be("RS256");
        jwt.Header.Kid.Should().MatchRegex("^[a-z0-9-]{8,64}$");
        jwt.Issuer.Should().Be("ClaudeHomeServer");
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("aihome-module:module-echo");
        jwt.Subject.Should().Be("user-1");
        jwt.Claims.First(c => c.Type == "name").Value.Should().Be("Андрей");
        jwt.Claims.First(c => c.Type == "scope").Value.Should().Be("echo.read echo.write");
        jwt.Claims.First(c => c.Type == "chan").Value.Should().Be("gateway");
        jwt.Claims.Should().Contain(c => c.Type == "jti");
        // §5.1: nbf = iat, TTL по каналу gateway — 5 мин
        jwt.ValidFrom.Should().Be(jwt.IssuedAt);
        jwt.ValidTo.Should().BeCloseTo(before.AddMinutes(5), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Token_McpChannel_Has60MinTtl()
    {
        var token = CreateTokenService().Issue(EchoModule(), "user-1", "u", "mcp");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        jwt.Claims.First(c => c.Type == "chan").Value.Should().Be("mcp");
        jwt.ValidTo.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(60), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Token_UnknownChannel_Throws()
    {
        var act = () => CreateTokenService().Issue(EchoModule(), "u", "u", "browser");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Token_ValidatesAgainstJwks()
    {
        var svc = CreateTokenService();
        var token = svc.Issue(EchoModule(), "user-1", "u", "gateway");

        // Внешний скрипт-валидатор: строим ключ из публичных n/e JWKS
        var jwks = JsonSerializer.SerializeToElement(svc.BuildJwks());
        var key = jwks.GetProperty("keys").EnumerateArray().First();
        var rsa = System.Security.Cryptography.RSA.Create();
        rsa.ImportParameters(new System.Security.Cryptography.RSAParameters
        {
            Modulus = Base64UrlEncoder.DecodeBytes(key.GetProperty("n").GetString()!),
            Exponent = Base64UrlEncoder.DecodeBytes(key.GetProperty("e").GetString()!),
        });

        var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token,
            new TokenValidationParameters
            {
                ValidIssuer = "ClaudeHomeServer",
                ValidAudience = "aihome-module:module-echo",
                IssuerSigningKey = new RsaSecurityKey(rsa) { KeyId = key.GetProperty("kid").GetString() },
                ClockSkew = TimeSpan.FromSeconds(60),
            }, out _);

        principal.FindFirst("sub")!.Value.Should().Be("user-1");
    }

    [Fact]
    public void Jwks_Rotation_OldKeyStaysNewKeySigns()
    {
        var svc = CreateTokenService();
        var module = EchoModule();
        var kidBefore = new JwtSecurityTokenHandler()
            .ReadJwtToken(svc.Issue(module, "u", "u", "gateway")).Header.Kid;

        svc.Rotate();
        var kidAfter = new JwtSecurityTokenHandler()
            .ReadJwtToken(svc.Issue(module, "u", "u", "gateway")).Header.Kid;

        kidAfter.Should().NotBe(kidBefore, "после ротации подписывает новый ключ");
        var jwks = JsonSerializer.SerializeToElement(svc.BuildJwks());
        var kids = jwks.GetProperty("keys").EnumerateArray()
            .Select(k => k.GetProperty("kid").GetString()).ToList();
        kids.Should().Contain(kidBefore, "отставленный ключ остаётся в JWKS ≥24 ч (§5.3)");
        kids.Should().Contain(kidAfter);
    }

    [Fact]
    public void Keys_PersistAcrossRestart()
    {
        var svc = CreateTokenService();
        var kid = new JwtSecurityTokenHandler()
            .ReadJwtToken(svc.Issue(EchoModule(), "u", "u", "gateway")).Header.Kid;

        var restarted = CreateTokenService();
        var kidAfter = new JwtSecurityTokenHandler()
            .ReadJwtToken(restarted.Issue(EchoModule(), "u", "u", "gateway")).Header.Kid;

        kidAfter.Should().Be(kid, "ключ читается из data/module-keys.json");
    }

    // --- R2: ModuleProxyConfigProvider ---

    [Fact]
    public void ProxyConfig_BuildsRouteAndClusterPerModule()
    {
        WriteModule("echo", ManifestJson("module-echo"));
        var provider = new ModuleProxyConfigProvider(CreateRegistry());

        var config = provider.GetConfig();

        var route = config.Routes.Should().ContainSingle().Subject;
        route.RouteId.Should().Be("module-module-echo");
        route.Match.Path.Should().Be("/api/modules/module-echo/{**catch-all}");
        route.Transforms.Should().Contain(t => t.ContainsKey("PathRemovePrefix"));

        var cluster = config.Clusters.Should().ContainSingle().Subject;
        cluster.ClusterId.Should().Be("module-module-echo");
        cluster.Destinations!.Values.Single().Address.Should().Be("http://127.0.0.1:5100");
        // §3.1: activity timeout 300 с
        cluster.HttpRequest!.ActivityTimeout.Should().Be(TimeSpan.FromSeconds(300));
        // §3: active health-check на healthPath
        cluster.HealthCheck!.Active!.Enabled.Should().BeTrue();
        cluster.HealthCheck.Active.Path.Should().Be("/health");
    }

    [Fact]
    public void ProxyConfig_EmptyRegistry_NoRoutes()
    {
        var provider = new ModuleProxyConfigProvider(CreateRegistry());
        var config = provider.GetConfig();
        config.Routes.Should().BeEmpty();
        config.Clusters.Should().BeEmpty();
    }

    // --- R8: динамические фич-флаги модулей ---

    [Fact]
    public void FeatureFlags_IncludeModuleFlag_DefaultOn()
    {
        WriteModule("echo", ManifestJson("module-echo"));
        var users = new ClaudeHomeServer.Services.UserStore(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            }).Build(),
            new Helpers.FakeHostEnvironment(),
            NullLogger<ClaudeHomeServer.Services.UserStore>.Instance);
        var flags = new ClaudeHomeServer.Services.FeatureFlagService(users, CreateRegistry());
        var userId = users.GetFirst()!.Id;

        flags.GetDefinitions().Should().Contain(d => d.Key == "module-module-echo");
        flags.Exists("module-module-echo").Should().BeTrue();
        flags.IsEnabled(userId, "module-module-echo").Should().BeTrue("модуль в реестре включён по умолчанию");

        users.SetFeatureFlag(userId, "module-module-echo", false);
        flags.IsEnabled(userId, "module-module-echo").Should().BeFalse("per-user override выключает модуль");
    }
}
