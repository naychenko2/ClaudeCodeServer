using System.IdentityModel.Tokens.Jwt;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Moq;

namespace ClaudeHomeServer.Tests;

// CT-9: gateway-passthrough модульного токена chan=mcp (§5.2б) + регресс cc_token (§5.2а).
// Гоняем middleware на DefaultHttpContext с реальными сервисами во временном каталоге.
public class ModuleGatewayMiddlewareTests
{
    // scope без двоеточия: ModuleRegistry.Validate допускает только ^[a-z][a-z0-9._-]{1,63}$ (§5.1)
    private const string EchoManifest =
        """{"schemaVersion":"1.0","id":"echo","version":"1.0.0","displayName":"Echo","backend":{"baseUrl":"http://localhost:9/echo","healthPath":"/health","routePrefix":"/api/modules/echo"},"scopes":["echo.read"]}""";

    private static ServiceProvider Build(string dataDir)
    {
        var modulesRoot = Path.Combine(dataDir, "modules");
        Directory.CreateDirectory(Path.Combine(modulesRoot, "echo"));
        File.WriteAllText(Path.Combine(modulesRoot, "echo", "module.json"), EchoManifest);

        var cfg = new MemoryConfig(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dataDir, "projects.json"),
            ["Modules:Path"] = modulesRoot,
        });

        var env = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == Environments.Production);

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IHostEnvironment>(env);
        services.AddSingleton<ModuleRegistry>();
        services.AddSingleton<JwtService>();
        services.AddSingleton<UserStore>();
        services.AddSingleton<FeatureFlagService>();
        services.AddSingleton<ModuleTokenService>();
        return services.BuildServiceProvider();
    }

    private static async Task<(int status, string seenAuth, IHeaderDictionary reqHeaders)> InvokeAsync(
        ServiceProvider sp, string path, string? authorization, IEnumerable<(string Key, string Value)>? extra = null)
    {
        var app = new ApplicationBuilder(sp);
        app.UseModuleGateway();

        string seenAuth = "";
        IHeaderDictionary seenHeaders = new HeaderDictionary();
        app.Run(async ctx =>
        {
            seenAuth = ctx.Request.Headers.Authorization.ToString();
            seenHeaders = ctx.Request.Headers;
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        });
        var pipeline = app.Build();

        var ctx2 = new DefaultHttpContext { RequestServices = sp };
        ctx2.Request.Method = "GET";
        ctx2.Request.Path = path;
        ctx2.Response.Body = new MemoryStream();
        if (authorization is not null)
            ctx2.Request.Headers.Authorization = authorization;
        foreach (var (key, value) in extra ?? [])
            ctx2.Request.Headers[key] = value;

        await pipeline(ctx2);
        return (ctx2.Response.StatusCode, seenAuth, seenHeaders);
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "ct9-mw", Guid.NewGuid().ToString("N"));

    // Минимальная in-memory IConfiguration без внешних пакетов: достаточно индексатора
    // (DataPath/Modules:Path) и пустой GetSection (Modules:Manifests → null/[] в ModuleRegistry).
    private sealed class MemoryConfig : IConfiguration
    {
        private readonly Dictionary<string, string?> _values;
        public MemoryConfig(Dictionary<string, string?> values) => _values = values;
        public string? this[string key]
        {
            get => _values.TryGetValue(key, out var v) ? v : null;
            set => _values[key] = value;
        }
        public IConfigurationSection GetSection(string key) => new EmptySection(key);
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
    }

    private sealed class EmptySection : IConfigurationSection
    {
        public EmptySection(string key) { Key = key; Path = key; }
        public string Key { get; }
        public string Path { get; }
        public string? Value { get; set; }
        public string? this[string key] { get => null; set { } }
        public IConfigurationSection GetSection(string key) => new EmptySection(key);
        public IEnumerable<IConfigurationSection> GetChildren() => [];
        public IChangeToken GetReloadToken() => new CancellationChangeToken(CancellationToken.None);
    }

    [Fact]
    public async Task Модульный_mcp_токен_проходит_gateway_без_изменений()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        using var sp = Build(dir);
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var module = sp.GetRequiredService<ModuleRegistry>().Get("echo")!;
        var token = tokens.Issue(module, "user-1", "Tester", "mcp");

        var (status, seenAuth, _) = await InvokeAsync(sp, "/api/modules/echo/api/whoami", $"Bearer {token}");

        Assert.Equal(200, status);
        // Passthrough (§5.2б): модуль видит исходный chan=mcp-токен, re-mint не происходит
        Assert.Equal($"Bearer {token}", seenAuth);
    }

    [Fact]
    public async Task cc_token_перевыпускается_как_chan_gateway()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        using var sp = Build(dir);
        var users = sp.GetRequiredService<UserStore>();
        var jwt = sp.GetRequiredService<JwtService>();
        var module = sp.GetRequiredService<ModuleRegistry>().Get("echo")!;
        var user = users.Add("tester", "pw", "user");
        var ccToken = jwt.Issue(user).token;

        var (status, seenAuth, _) = await InvokeAsync(sp, "/api/modules/echo/api/data", $"Bearer {ccToken}");

        Assert.Equal(200, status);
        Assert.StartsWith("Bearer ", seenAuth);
        var reissued = seenAuth["Bearer ".Length..];
        Assert.NotEqual(ccToken, reissued);   // cc_token срезан и заменён
        // Перевыпущен именно модульный токен канала gateway с aud этого модуля (§5.2а)
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(reissued);
        Assert.Equal("gateway", parsed.Claims.First(c => c.Type == "chan").Value);
        Assert.Equal(module.Audience, parsed.Audiences.First());
    }

    [Fact]
    public async Task Невалидный_или_отсутствующий_токен_даёт_401()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        using var sp = Build(dir);

        var (status1, _, _) = await InvokeAsync(sp, "/api/modules/echo/api/data", "Bearer garbage");
        Assert.Equal(StatusCodes.Status401Unauthorized, status1);

        var (status2, _, _) = await InvokeAsync(sp, "/api/modules/echo/api/data", null);
        Assert.Equal(StatusCodes.Status401Unauthorized, status2);
    }

    [Fact]
    public async Task Входные_X_AIHome_заголовки_срезаются_на_обоих_путях()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        using var sp = Build(dir);
        var tokens = sp.GetRequiredService<ModuleTokenService>();
        var module = sp.GetRequiredService<ModuleRegistry>().Get("echo")!;
        var token = tokens.Issue(module, "user-1", "Tester", "mcp");

        var (status, _, reqHeaders) = await InvokeAsync(sp, "/api/modules/echo/api/data", $"Bearer {token}",
            extra: new[] { ("X-AIHome-User", "attacker"), ("X-AIHome-Role", "admin") });

        Assert.Equal(200, status);
        Assert.False(reqHeaders.ContainsKey("X-AIHome-User"));
        Assert.False(reqHeaders.ContainsKey("X-AIHome-Role"));
    }
}
