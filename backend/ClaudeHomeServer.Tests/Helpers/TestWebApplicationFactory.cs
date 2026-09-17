using System.Net.Http.Headers;
using System.Net.Http.Json;
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

    // Стаб LLM-адаптеров: подменяет реальную LlmSessionAdapterFactory, чтобы сервер-
    // инициированные ходы (kickoff онбординга) не запускали claude.exe в интеграционных
    // тестах. Хранит созданные адаптеры по sessionId — тесты проверяют факт первого хода.
    internal FakeLlmSessionAdapterFactory LlmAdapters { get; } = new();

    // Точка подмены сервисов для конкретного теста (напр. стаб IProviderBalanceService): null —
    // хост собирается как обычно. Применяется после встроенных регистраций, поэтому может их перетереть.
    public Action<IServiceCollection>? ExtraServices { get; set; }

    // Точка подмены КОНФИГА для конкретного теста (напр. секция LlmProviders): источник
    // добавляется после встроенных ключей фабрики, поэтому перетирает их. Нужен там, где
    // сервис читает IConfiguration в конструкторе (LlmProviderRegistry) и подменять его
    // целиком через ExtraServices значило бы собирать его руками мимо боевой регистрации.
    public Dictionary<string, string?> ExtraConfig { get; } = [];

    // ─── Опт-ин «фронт раздаётся, как на проде» ──────────────────────────────
    //
    // `Program.cs` ищет distPath в порядке: `AppContext.BaseDirectory/wwwroot` →
    // `../../frontend/dist`. Ни в одном тестовом прогоне ни тот, ни другой путь не
    // существует, поэтому блок раздачи фронта встаёт в «else»-ветку и НЕ регистрирует
    // `MapFallbackToFile` — а вместе с ним и фикс `/api`-исключения (это и был
    // дефект задачи 6c7f3d34, на котором все «зелёные по неверной причине»).
    //
    // `UseFrontend` поднимает флаг: между Build первой фабрики с флагом и Build последней
    // фабрики с флагом физически создаётся `<AppContext.BaseDirectory>/wwwroot/index.html`
    // с подложенной статикой. Счётчик — на случай, когда в одной коллекции xUnit'а
    // работают несколько фабрик одновременно: флаг поднимают все, чистит — последний.
    // Параллельные классы тестов делят один и тот же каталог (он по природе глобален) —
    // это сознательно: `AppContext.BaseDirectory` единственный на процесс, и
    // `Program.cs` смотрит именно туда. Тестов, рассчитывающих на отсутствие wwwroot,
    // в наборе нет.
    public bool UseFrontend { get; init; } = true;
    private static readonly object _FrontendLock = new();
    private static int _FrontendReferences;
    private static string? _FrontendPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Создаём users.json до старта хоста — UserStore прочитает его при инициализации
        Directory.CreateDirectory(TempDir);
        CreateUsersFile(TempDir);

        // Опт-ин фронт-каталога: «фронт как на проде» — `Program.cs` находит
        // `AppContext.BaseDirectory/wwwroot`, регистрирует `MapFallbackToFile` и
        // наш fallback-эндпоинт `/api/{**rest}`. Тест, проверяющий этот фикс,
        // ОБЯЗАН пройти через эту ветку, иначе он зелёный на пустоте. Каталог
        // глобален на процесс (один и тот же путь проверяется в `Program.cs:1766`),
        // поэтому учитываем ссылки на каталог через статический счётчик, чтобы
        // параллельные хосты в xUnit-коллекциях не снесли друг другу wwwroot.
        if (UseFrontend) EnsureFrontendRoot();
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
                // Домашняя база local-пользователей: без override берётся пустая строка из
                // appsettings.Testing.json — UserHomeResolver вернёт null с явным сообщением
                // «Не задана папка проектов по умолчанию», что лучше молчаливого попадания
                // в продовую папку через контейнерный монтирование /projects. Уводим в temp
                // — уберётся с TempDir.
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
            if (ExtraConfig.Count > 0) config.AddInMemoryCollection(ExtraConfig);
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

            // LLM-адаптер → стаб: сервер-инициированные ходы (kickoff онбординга) не запускают
            // реальный claude.exe в интеграционных тестах. Регистрация перезаписывает боевой
            // синглтон LlmSessionAdapterFactory из Program.cs (последняя побеждает).
            services.AddSingleton<ClaudeHomeServer.Services.Llm.ILlmSessionAdapterFactory>(LlmAdapters);

            // Дешёвые one-shot вызовы → стаб по умолчанию: тестовый хост не должен запускать
            // настоящий claude.exe из фоновых действий (типовые умения при создании персон,
            // теги, сводки). Тестам, которым нужны управляемые ответы, — свои заглушки
            // через ExtraServices (регистрация позже — побеждает).
            services.AddSingleton<ClaudeHomeServer.Services.Llm.ICheapTextRunner>(
                new StubCheapTextRunner("[]"));
        });

        if (ExtraServices is { } extra)
            builder.ConfigureServices(extra);
    }

    /// <summary>
    /// Заглушка ICheapTextRunner по умолчанию: пустой JSON-массив — все парсеры кандидатов
    /// (привязки, черновики) читают его как «ничего не подошло», вызов мгновенный и без сети.
    /// </summary>
    private sealed class StubCheapTextRunner(string answer) : ClaudeHomeServer.Services.Llm.ICheapTextRunner
    {
        public bool UsesLocal(string actionKey) => false;
        public bool HasFreeRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";
        public Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
            => Task.FromResult(answer);
        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);
        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<ClaudeHomeServer.Services.Llm.OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Поднимает «фронт как на проде» в <c>{AppContext.BaseDirectory}/wwwroot</c>
    /// под статическую раздачу <c>Program.cs</c>. Создаёт каталог и минимальный
    /// <c>index.html</c> при первом запросе; последующие хосты с <see cref="UseFrontend"/>
    /// делят тот же путь. Снимается на последнем Dispose из цепочки ссылок
    /// (см. <see cref="_FrontendReferences"/>).
    /// </summary>
    private static void EnsureFrontendRoot()
    {
        lock (_FrontendLock)
        {
            _FrontendReferences++;
            if (_FrontendPath is not null) return;
            // AppContext.BaseDirectory — общий путь, на который смотрит Program.cs.
            // bin/Debug/net10.0/ тестовой сборки, бирюзовое соответствие проду.
            var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            Directory.CreateDirectory(root);
            // Минимальный «index.html», достаточный для `MapFallbackToFile` —
            // контент значения не имеет, важна только отдача text/html на любой SPA-маршрут.
            // С `ContentTypeProvider` `Program.cs:1802` достаточно расширения `.html`.
            File.WriteAllText(Path.Combine(root, "index.html"),
                "<!doctype html><title>test</title><div id=root>frontend test fixture</div>");
            _FrontendPath = root;
        }
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

        // На Dispose снимаем свою ссылку на wwwroot: последний Dispose в цепочке
        // сносит каталог. `false` фиксирует случай, когда фабрика выключалась без билда —
        // смой счётчик ровно на столько, на сколько подняли в ConfigureWebHost.
        ReleaseFrontendRoot();

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

    /// <summary>
    /// Зеркало к <see cref="EnsureFrontendRoot"/>: под ссылочным счётом. На последнем
    /// релизе удаляет <c>wwwroot</c> целиком (каталог общий на процесс).
    /// </summary>
    private void ReleaseFrontendRoot()
    {
        if (!UseFrontend) return;
        lock (_FrontendLock)
        {
            if (_FrontendReferences <= 0) return;
            _FrontendReferences--;
            if (_FrontendReferences > 0 || _FrontendPath is null) return;
            try
            {
                if (Directory.Exists(_FrontendPath)) Directory.Delete(_FrontendPath, recursive: true);
            }
            catch (IOException)
            {
                // Хост ещё держит handle на index.html — оставляем каталог ОС, в худшем
                // случае следующий прогон перезапишет. Это уборка теста, не предмет проверки.
            }
            _FrontendPath = null;
        }
    }
}
