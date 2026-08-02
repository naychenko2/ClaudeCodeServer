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
using Microsoft.Data.Sqlite;
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

    // Второй пользователь — для тестов изоляции по владельцу
    public const string SecondUsername = "seconduser";
    public const string SecondPassword = "secondpassword";

    public string TempDir { get; } = Path.Combine(Path.GetTempPath(), "ccs_tests_" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Создаём users.json до старта хоста — UserStore прочитает его при инициализации
        Directory.CreateDirectory(TempDir);
        CreateUsersFile(TempDir);
        // Пустой «пользовательский профиль CLI»: без оверрайда LlmProviderRegistry брал бы
        // реальный ~/.claude разработчика и синкал его (13k файлов, ~95 MB) в temp-профиль
        // на каждом хосте, где ход строится с профилем подписки/провайдера
        var emptyClaudeProfile = Path.Combine(TempDir, "claude-user-profile");
        Directory.CreateDirectory(emptyClaudeProfile);

        // Среда Testing: Program.cs по ней НЕ подключает appsettings.Local.json
        // разработчика — боевые токены подписок/провайдеров не протекают в тестовый хост
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(TempDir, "projects.json"),
                // Домашняя база local-пользователей: без override берётся дефолт "/projects"
                // из appsettings.json. На Windows он молча резолвится в C:\projects\…, а на
                // Linux CI это корень ФС — Directory.CreateDirectory падает с Permission denied
                // (ResolveChatRoot при создании чата вне проекта). Уводим в temp — уберётся с TempDir.
                ["DefaultProjectsPath"] = Path.Combine(TempDir, "projects"),
                ["ClaudeUserProfileDir"] = emptyClaudeProfile,
                // страховка от прогрева подписок реальными claude.exe «ping»-ходами:
                // ключ читается в рантайме, InMemory-источник фабрики сильнее любого файла
                ["ClaudeSubscriptions:WarmupOnStartup"] = "false",
                // высокий лимит, чтобы rate-limit не флакал обычные тесты login
                ["Auth:LoginRateLimit"] = "1000",
                // не опрашивать claude CLI при прогреве каталога моделей: каждый подъём
                // приложения спавнил настоящий claude.exe с мелькающими окнами bash/cmd
                ["ModelCatalog:QueryCli"] = "false",
                // не опрашивать API провайдеров в тестах: каждый провайдер × таймаут 10 с
                // при недоступности — кратно замедляет старт каждого тестового хоста
                ["ModelCatalog:QueryProviderApis"] = "false",
                ["Ollama:Model"] = "", // отключить прогрев и локальные фоновые задачи в тестах
                // не опрашивать api/oauth/usage: тест не должен ходить в сеть с реальным
                // токеном ~/.claude машины
                ["ClaudeSubscriptions:UsagePollMinutes"] = "0",
                // FileWatcher→CodeGraph integration: polling-режим детерминированнее
                // FileSystemWatcher в TestServer/CI; короткий интервал + короткий дебаунс
                // rebuild, чтобы тесты завершались за ~1с, а не ждали 15с.
                ["FileWatcher:UsePolling"] = "true",
                ["FileWatcher:PollIntervalMs"] = "150",
                ["CodeGraph:RebuildDebounceMs"] = "50"
                // Телеметрию отсюда отключить НЕЛЬЗЯ: Program.cs подключает
                // appsettings.Local.json ПОСЛЕ источников этой фабрики, поэтому
                // Telemetry:Backends:*:Enabled из файла разработчика сильнее.
                // Экспорт глушится переменной окружения — см. TestTelemetryGuard.
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
        var hasher = new PasswordHasher<User>();
        var user = new User { Username = TestUsername, Role = "admin" };
        user.PasswordHash = hasher.HashPassword(user, TestPassword);
        var second = new User { Username = SecondUsername, Role = "user" };
        second.PasswordHash = hasher.HashPassword(second, SecondPassword);
        var usersFile = new { version = 1, users = new[] { user, second } };
        File.WriteAllText(
            Path.Combine(dir, "users.json"),
            JsonSerializer.Serialize(usersFile));
    }

    /// <summary>JWT указанного пользователя через POST /api/auth/login.</summary>
    public string GetToken(string username, string password)
    {
        using var client = CreateClient();
        var response = client.PostAsJsonAsync("/api/auth/login", new { username, password })
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var body = response.Content.ReadFromJsonAsync<JsonElement>().GetAwaiter().GetResult();
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Клиент с JWT основного тестового пользователя.</summary>
    public HttpClient CreateAuthenticatedClient() =>
        CreateAuthenticatedClient(TestUsername, TestPassword);

    /// <summary>Клиент с JWT указанного пользователя.</summary>
    public HttpClient CreateAuthenticatedClient(string username, string password)
    {
        var client = CreateClient();
        var token = GetToken(username, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || !Directory.Exists(TempDir)) return;

        // Microsoft.Data.Sqlite пулит физические соединения: после остановки хоста файл
        // project-events.db всё ещё открыт, и удаление временной папки падало с IOException
        // (мигающие «Test Class Cleanup Failure» в полном прогоне).
        SqliteConnection.ClearAllPools();
        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Delete(TempDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Уборка temp — не предмет теста: не дотёрли за 5 попыток, оставляем ОС
                if (i >= 5) return;
                Thread.Sleep(20 * i);
            }
        }
    }
}
