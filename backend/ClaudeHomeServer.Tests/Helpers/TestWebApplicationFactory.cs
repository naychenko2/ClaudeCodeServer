using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Helpers;

/// <summary>
/// Заглушка для NegotiateHandler — Negotiate несовместим с TestServer (требует Kestrel).
/// Просто возвращает NoResult при аутентификации.
/// </summary>
public class NoOpNegotiateHandler : IAuthenticationHandler
{
    public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context) => Task.CompletedTask;
    public Task<AuthenticateResult> AuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(AuthenticationProperties? properties) => Task.CompletedTask;
}

public class TestWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    public const string TestUsername = "testuser";
    public const string TestPassword = "testpassword";

    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), "ccs_tests_" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Создаём users.json до старта хоста — UserStore прочитает его при инициализации
        Directory.CreateDirectory(TempDir);
        CreateUsersFile(TempDir);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(TempDir, "projects.json"),
                // высокий лимит, чтобы rate-limit не флакал обычные тесты login
                ["Auth:LoginRateLimit"] = "1000"
            });
        });

        // Negotiate требует Kestrel и несовместим с TestServer — заменяем на no-op заглушку
        builder.ConfigureServices(services =>
        {
            services.AddTransient<NoOpNegotiateHandler>();
            services.AddOptions<AuthenticationSchemeOptions>(NegotiateDefaults.AuthenticationScheme);
            services.Configure<AuthenticationOptions>(opts =>
            {
                var scheme = opts.Schemes.FirstOrDefault(s => s.Name == NegotiateDefaults.AuthenticationScheme);
                if (scheme is not null) scheme.HandlerType = typeof(NoOpNegotiateHandler);
            });
        });
    }

    private static void CreateUsersFile(string dir)
    {
        var user = new User { Username = TestUsername, Role = "admin" };
        user.PasswordHash = new PasswordHasher<User>().HashPassword(user, TestPassword);
        var usersFile = new { version = 1, users = new[] { user } };
        File.WriteAllText(
            Path.Combine(dir, "users.json"),
            JsonSerializer.Serialize(usersFile));
    }

    /// <summary>Клиент с JWT, полученным через POST /api/auth/login.</summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        var response = client.PostAsJsonAsync("/api/auth/login", new
        {
            username = TestUsername,
            password = TestPassword
        }).GetAwaiter().GetResult();

        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        var token = body.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TempDir))
            Directory.Delete(TempDir, recursive: true);
    }
}
