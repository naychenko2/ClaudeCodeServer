using ClaudeHomeServer.Tests.Helpers;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Поллер api/oauth/usage: статусы неуспеха per-аккаунт (setup-токен → 403 → «токен
// не подходит», лог один раз) и динамический разбор окон ответа (включая незнакомые
// per-model окна вроде seven_day_fable).
// Коллекция SystemEnv — общая с Local/DockerProcessRunnerEnvTests: EnvVarScope ниже
// мутирует process-global CLAUDE_CODE_OAUTH_TOKEN, xunit не должен гонять эти классы
// параллельно (гонка с любым спавном процесса, читающим env).
[Collection("SystemEnv")]
public class SubscriptionOAuthUsageServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SubscriptionOAuthUsageServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "oauth_usage_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private (SubscriptionOAuthUsageService Service, UsageService Usage) CreateService(StubHandler handler)
    {
        var config = TestConfig.Build(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        });
        var usage = new UsageService(config);
        var pool = new ClaudeSubscriptionPool(config);
        var guard = new SubscriptionWindowMismatchGuard(usage, pool, new SubscriptionWindowMismatchGuardTests.SilentNotifier());
        var svc = new SubscriptionOAuthUsageService(
            pool, usage, new LlmProviderRegistry(config),
            new StubHttpFactory(handler), guard, config);
        svc.OverrideUserAgent("claude-code/test"); // не дёргать `claude --version` в тесте
        return (svc, usage);
    }

    // Пары вызовов с перехватом stderr — проверка «лог не чаще раза при смене статуса»
    private static async Task<string> CaptureErrAsync(Func<Task> action)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { await action(); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }

    [Fact]
    public async Task SetupТокен403_СтатусUnauthorized_ЛогОдинРаз_СнимковНет()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var (svc, usage) = CreateService(handler);

        var log = await CaptureErrAsync(async () =>
        {
            await svc.PollAsync("claude-2", "sk-ant-oat01-secret", CancellationToken.None);
            await svc.PollAsync("claude-2", "sk-ant-oat01-secret", CancellationToken.None);
        });

        svc.StatusOf("claude-2").Should().Be(SubscriptionOAuthUsageService.StatusUnauthorized);
        usage.GetAll().Should().BeEmpty();
        handler.Calls.Should().Be(2); // 403 не уводит в backoff — каждый тик пробуем снова
        Regex.Matches(log, Regex.Escape("[OAuthUsage]")).Count.Should().Be(1, "лог — только при смене статуса");
        log.Should().NotContain("sk-ant-oat01", "токен не должен утекать в журнал");
    }

    [Fact]
    public async Task Ответ401_ТожеUnauthorized()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var (svc, _) = CreateService(handler);

        await CaptureErrAsync(() => svc.PollAsync("claude", "token", CancellationToken.None));

        svc.StatusOf("claude").Should().Be(SubscriptionOAuthUsageService.StatusUnauthorized);
    }

    [Fact]
    public async Task Ответ500_СтатусError()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var (svc, _) = CreateService(handler);

        await CaptureErrAsync(() => svc.PollAsync("claude", "token", CancellationToken.None));

        svc.StatusOf("claude").Should().Be(SubscriptionOAuthUsageService.StatusError);
    }

    [Fact]
    public async Task УспешныйОтвет_ОкнаРазобраныДинамически_ВключаяНезнакомыйКлюч()
    {
        const string json = """
        {
            "five_hour": { "utilization": 53.0, "resets_at": "2026-07-25T18:00:00Z" },
            "seven_day": { "utilization": 20.0, "resets_at": "2026-07-30T00:00:00Z" },
            "seven_day_fable": { "utilization": 23.0, "resets_at": "2026-07-30T00:00:00Z" },
            "extra_usage": { "is_enabled": true, "monthly_limit": 100, "used_credits": 5, "utilization": 5.0 },
            "account_kind": "max",
            "meta": { "irrelevant": true }
        }
        """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var (svc, usage) = CreateService(handler);

        await CaptureErrAsync(() => svc.PollAsync("claude-2", "token", CancellationToken.None));

        svc.StatusOf("claude-2").Should().Be(SubscriptionOAuthUsageService.StatusOk);
        var byType = usage.GetAll().ToDictionary(s => s.LimitType);
        byType.Keys.Should().BeEquivalentTo("five_hour", "seven_day", "seven_day_fable", "extra_usage");
        byType["five_hour"].Utilization.Should().BeApproximately(0.53, 0.001);
        // Незнакомое per-model окно (Fable) подхватывается без правок кода
        byType["seven_day_fable"].Utilization.Should().BeApproximately(0.23, 0.001);
        byType["seven_day_fable"].ResetsAt.Should().Be("2026-07-30T00:00:00Z");
        byType["seven_day_fable"].SubscriptionKey.Should().Be("claude-2");
        byType["extra_usage"].Utilization.Should().BeApproximately(0.05, 0.001);
        byType.Values.Select(s => s.Source).Should().AllBeEquivalentTo("oauth");
    }

    // Сервис на произвольном конфиге — для тестов EnumerateAccounts (состав пула важен)
    private SubscriptionOAuthUsageService CreateServiceWith(Dictionary<string, string?> extraConfig)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        };
        foreach (var (k, v) in extraConfig) dict[k] = v;
        var config = TestConfig.Build(dict);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var usage = new UsageService(config);
        var pool = new ClaudeSubscriptionPool(config);
        var guard = new SubscriptionWindowMismatchGuard(usage, pool, new SubscriptionWindowMismatchGuardTests.SilentNotifier());
        var svc = new SubscriptionOAuthUsageService(
            pool, usage, new LlmProviderRegistry(config),
            new StubHttpFactory(handler), guard, config);
        svc.OverrideUserAgent("claude-code/test");
        return svc;
    }

    [Fact]
    public void EnumerateAccounts_ПулСодержитClaude_PrimaryВеткаНеОпрашивается()
    {
        // Прод-баг 2026-07-25: primary-ветка (env/конфиг/~/.claude) и запись пула "claude"
        // (профиль sub-claude) опрашивались ОБЕ под ключом PrimaryKey — два разных токена,
        // два аккаунта, противоречивые снимки в одну секунду. Одно окно — один источник.
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            ["Claude:OAuthToken"] = "primary-token",
            [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-claude",
            [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
        });

        var accounts = svc.EnumerateAccounts().ToList();

        accounts.Select(a => a.Key).Should().BeEquivalentTo("claude", "second");
        accounts.Single(a => a.Key == "claude").Token.Should().Be("token-claude",
            "ключ claude должен опрашиваться токеном пула, а не primary-веткой");
    }

    [Fact]
    public void EnumerateAccounts_ПулБезClaude_PrimaryОпрашиваетсяКакРаньше()
    {
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            ["Claude:OAuthToken"] = "primary-token",
            [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
        });

        var accounts = svc.EnumerateAccounts().ToList();

        accounts.Select(a => a.Key).Should().BeEquivalentTo("claude", "second");
        accounts.Single(a => a.Key == "second").Token.Should().Be("token-second");
    }

    // --- LoginCommandFor: готовая PowerShell-команда входа для плашки «нужен claude login» ---

    private static IDisposable SystemEnv(string key, string? value)
    {
        var prev = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
        return new Restore(key, prev);
    }

    private sealed class Restore(string key, string? prevValue) : IDisposable
    {
        public void Dispose() => Environment.SetEnvironmentVariable(key, prevValue);
    }

    [Fact]
    public void LoginCommandFor_АккаунтПула_СодержитПутьSubКлючИClaudeLogin()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", null);
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:second:OAuthToken"] = "token-second",
        });

        var expectedDir = Path.Combine(_tempDir, "claude-profiles", "sub-second");
        svc.LoginCommandFor("second").Should().Be($"$env:CLAUDE_CONFIG_DIR = \"{expectedDir}\"; claude login");
    }

    [Fact]
    public void LoginCommandFor_PrimaryВПуле_ИдётПоТойЖеСхемеЧтоПодписка()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", null);
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            [$"{ClaudeSubscriptionPool.Section}:claude:OAuthToken"] = "token-claude",
        });

        var expectedDir = Path.Combine(_tempDir, "claude-profiles", "sub-claude");
        svc.LoginCommandFor("claude").Should().Be($"$env:CLAUDE_CONFIG_DIR = \"{expectedDir}\"; claude login");
    }

    [Fact]
    public void LoginCommandFor_PrimaryНеВПуле_ТокенИзEnv_Null()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", "env-token");
        var svc = CreateServiceWith(new Dictionary<string, string?>());

        svc.LoginCommandFor("claude").Should().BeNull("env-токен перекроет вход в файл профиля");
    }

    [Fact]
    public void LoginCommandFor_PrimaryНеВПуле_ТокенИзКонфига_Null()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", null);
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            ["Claude:OAuthToken"] = "cfg-token",
        });

        svc.LoginCommandFor("claude").Should().BeNull("конфиг-токен перекроет вход в файл профиля");
    }

    [Fact]
    public void LoginCommandFor_PrimaryНеВПуле_ФайловыеКреды_КомандаСПутёмПрофиля()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", null);
        var profileDir = Path.Combine(_tempDir, "user-home", ".claude");
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            ["ClaudeUserProfileDir"] = profileDir,
        });

        svc.LoginCommandFor("claude").Should().Be($"$env:CLAUDE_CONFIG_DIR = \"{profileDir}\"; claude login");
    }

    [Fact]
    public void LoginCommandFor_ПутьСПробелами_ВесьПутьВОднойПареКавычек()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", null);
        var dirWithSpaces = Path.Combine(_tempDir, "user profile dir", ".claude");
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            ["ClaudeUserProfileDir"] = dirWithSpaces,
        });

        var cmd = svc.LoginCommandFor("claude");

        cmd.Should().Be($"$env:CLAUDE_CONFIG_DIR = \"{dirWithSpaces}\"; claude login");
    }

    [Fact]
    public void LoginCommandFor_ПутьСКавычкой_ЭкранируетсяБэктиком()
    {
        using var _ = SystemEnv("CLAUDE_CODE_OAUTH_TOKEN", null);
        var dirWithQuote = Path.Combine(_tempDir, "weird\"dir", ".claude");
        var svc = CreateServiceWith(new Dictionary<string, string?>
        {
            ["ClaudeUserProfileDir"] = dirWithQuote,
        });

        var cmd = svc.LoginCommandFor("claude");

        cmd.Should().Contain("weird`\"dir", "двойная кавычка внутри пути экранируется бэктиком — иначе PS-строка оборвётся раньше времени");
        cmd.Should().StartWith("$env:CLAUDE_CONFIG_DIR = \"").And.EndWith("; claude login");
    }

    // --- Рефреш протухшего access-токена профиля (sub-профили CLI не обновляет) ---

    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";

    private string WriteProfileCreds(string name, string access, string refresh, long expiresAtMs)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".credentials.json"), $$"""
        {
          "claudeAiOauth": {
            "accessToken": "{{access}}",
            "refreshToken": "{{refresh}}",
            "expiresAt": {{expiresAtMs}},
            "subscriptionType": "max"
          },
          "mcpOAuth": { "keep": "me" }
        }
        """);
        return dir;
    }

    // Хендлер обоих эндпоинтов: usage пускает только свежий токен, token-эндпоинт
    // выдаёт новую пару (или отказывает, если refreshOk=false)
    private static StubHandler RefreshAwareHandler(string freshToken, bool refreshOk, Action? onRefresh = null)
        => new(req =>
        {
            if (req.RequestUri!.ToString().StartsWith(TokenEndpoint))
            {
                onRefresh?.Invoke();
                if (!refreshOk) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{ "access_token": "{{freshToken}}", "refresh_token": "refresh-2", "expires_in": 28800 }""",
                        Encoding.UTF8, "application/json"),
                };
            }
            var auth = req.Headers.Authorization?.Parameter;
            return auth == freshToken
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "five_hour": { "utilization": 42.0 } }""", Encoding.UTF8, "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

    [Fact]
    public async Task ПротухшиеКреды_ПродленыДоЗапроса_ФайлПереписан_ОпросOk()
    {
        var dir = WriteProfileCreds("sub-exp", "stale-token", "refresh-1",
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());
        var handler = RefreshAwareHandler("fresh-token", refreshOk: true);
        var (svc, usage) = CreateService(handler);

        await CaptureErrAsync(() => svc.PollAsync("claude-2", "stale-token", dir, CancellationToken.None));

        svc.StatusOf("claude-2").Should().Be(SubscriptionOAuthUsageService.StatusOk);
        usage.GetAll().Should().ContainSingle(s => s.LimitType == "five_hour");
        var creds = File.ReadAllText(Path.Combine(dir, ".credentials.json"));
        creds.Should().Contain("fresh-token").And.Contain("refresh-2", "пара должна ротироваться");
        creds.Should().Contain("keep", "прочие поля файла (mcpOAuth) обязаны сохраниться");
        creds.Should().NotContain("stale-token");
    }

    [Fact]
    public async Task Отказ401ПриЖивомExpiresAt_ОднаПопыткаРефреша_ПовторУспешен()
    {
        // expiresAt в будущем (отзыв токена, рассинхрон часов) — рефреш по факту 401
        var dir = WriteProfileCreds("sub-revoked", "revoked-token", "refresh-1",
            DateTimeOffset.UtcNow.AddHours(4).ToUnixTimeMilliseconds());
        var refreshCalls = 0;
        var handler = RefreshAwareHandler("fresh-token", refreshOk: true, () => refreshCalls++);
        var (svc, _) = CreateService(handler);

        await CaptureErrAsync(() => svc.PollAsync("claude-2", "revoked-token", dir, CancellationToken.None));

        svc.StatusOf("claude-2").Should().Be(SubscriptionOAuthUsageService.StatusOk);
        refreshCalls.Should().Be(1);
    }

    [Fact]
    public async Task Рефреш429_ОднаПопытка_БезЗапросаUsage_АккаунтВBackoff()
    {
        // Прод 25.07: token-эндпоинт живёт в том же скользящем 429-бакете UA claude-code,
        // что и usage — при 429 нельзя ни повторять рефреш, ни жечь usage-запрос протухшим
        // токеном (гарантированный 401 + трафик в тот же бакет)
        var dir = WriteProfileCreds("sub-limited", "stale-token", "refresh-1",
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());
        var handler = new StubHandler(req => req.RequestUri!.ToString().StartsWith(TokenEndpoint)
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : throw new InvalidOperationException("usage-эндпоинт не должен дёргаться при 429 рефреша"));
        var (svc, _) = CreateService(handler);

        var log = await CaptureErrAsync(async () =>
        {
            await svc.PollAsync("claude-2", "stale-token", dir, CancellationToken.None);
            // Второй тик сразу же: аккаунт в backoff — ни одного нового запроса
            await svc.PollAsync("claude-2", "stale-token", dir, CancellationToken.None);
        });

        handler.Calls.Should().Be(1, "одна попытка рефреша, без повтора и без usage-запроса");
        svc.StatusOf("claude-2").Should().BeNull("429 бакета — не приговор токену, статус не трогаем");
        log.Should().NotContain("рефреш токена отвергнут", "429 — молча ждать, это не отказ токена");
    }

    [Fact]
    public async Task РефрешОтвергнут_СтатусUnauthorized_ФайлНеТронут()
    {
        var dir = WriteProfileCreds("sub-dead", "stale-token", "refresh-dead",
            DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeMilliseconds());
        var handler = RefreshAwareHandler("fresh-token", refreshOk: false);
        var (svc, _) = CreateService(handler);

        var log = await CaptureErrAsync(() => svc.PollAsync("claude-2", "stale-token", dir, CancellationToken.None));

        svc.StatusOf("claude-2").Should().Be(SubscriptionOAuthUsageService.StatusUnauthorized);
        File.ReadAllText(Path.Combine(dir, ".credentials.json")).Should().Contain("stale-token");
        log.Should().NotContain("refresh-dead", "refresh-токен не должен утекать в журнал");
    }

    [Fact]
    public async Task ВосстановлениеПосле403_СтатусСноваOk()
    {
        var fail = true;
        var handler = new StubHandler(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "five_hour": { "utilization": 1.0 } }""", Encoding.UTF8, "application/json"),
            });
        var (svc, _) = CreateService(handler);

        await CaptureErrAsync(() => svc.PollAsync("claude", "token", CancellationToken.None));
        svc.StatusOf("claude").Should().Be(SubscriptionOAuthUsageService.StatusUnauthorized);

        fail = false;
        await CaptureErrAsync(() => svc.PollAsync("claude", "token", CancellationToken.None));
        svc.StatusOf("claude").Should().Be(SubscriptionOAuthUsageService.StatusOk);
    }
}
