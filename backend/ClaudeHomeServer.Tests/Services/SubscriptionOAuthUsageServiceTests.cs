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
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        }).Build();
        var usage = new UsageService(config);
        var svc = new SubscriptionOAuthUsageService(
            new ClaudeSubscriptionPool(config), usage, new LlmProviderRegistry(config),
            new StubHttpFactory(handler), config);
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
    }

    // Сервис на произвольном конфиге — для тестов EnumerateAccounts (состав пула важен)
    private SubscriptionOAuthUsageService CreateServiceWith(Dictionary<string, string?> extraConfig)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        };
        foreach (var (k, v) in extraConfig) dict[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var svc = new SubscriptionOAuthUsageService(
            new ClaudeSubscriptionPool(config), new UsageService(config), new LlmProviderRegistry(config),
            new StubHttpFactory(handler), config);
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
