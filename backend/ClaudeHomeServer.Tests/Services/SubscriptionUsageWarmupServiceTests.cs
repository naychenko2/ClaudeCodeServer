using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// Идл-пинг подписок: пингуются только простаивающие аккаунты (SubscriptionActivityTracker),
// OAuth-снимки таймер простоя не сбрасывают, пинг идёт --model haiku, IdlePingMinutes=0
// выключает механизм. Реальный запуск claude.exe тестами не проверяется (нечем подменить
// процесс) — тестируем вынесенные для этого чистые методы, как OneShotClaudeRunner.BuildArgs.
public class SubscriptionUsageWarmupServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SubscriptionUsageWarmupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sub_warmup_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (SubscriptionUsageWarmupService Service, ClaudeSubscriptionPool Pool, UsageService Usage,
        SubscriptionActivityTracker Activity) MkService(int? idlePingMinutes = null, params string[] subKeys)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
        };
        foreach (var key in subKeys)
            dict[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = "token-" + key;
        if (idlePingMinutes is not null)
            dict[$"{ClaudeSubscriptionPool.Section}:IdlePingMinutes"] = idlePingMinutes.Value.ToString();
        var config = TestConfig.Build(dict);

        var usage = new UsageService(config);
        var pool = new ClaudeSubscriptionPool(config, usage);
        var providers = new LlmProviderRegistry(config);
        var activity = new SubscriptionActivityTracker();
        var svc = new SubscriptionUsageWarmupService(pool, usage, providers, activity, config);
        return (svc, pool, usage, activity);
    }

    // --- Идл-критерий: кто попадает в KeysDueForPing ---

    [Fact]
    public void БезАктивности_АккаунтПингуется()
    {
        var (svc, _, _, _) = MkService(subKeys: ["second"]);

        svc.KeysDueForPing(TimeSpan.FromMinutes(5)).Should().Contain("second");
    }

    [Fact]
    public void СвежаяАктивность_АккаунтНеПингуется()
    {
        // Живой ход (или недавний пинг) тронул трекер только что — 5-минутный порог не вышел.
        var (svc, _, _, activity) = MkService(subKeys: ["second"]);
        activity.Touch("second");

        svc.KeysDueForPing(TimeSpan.FromMinutes(5)).Should().NotContain("second");
    }

    [Fact]
    public void OAuthСнимок_НеСбрасываетТаймер_АккаунтВсёРавноПростаивает()
    {
        // SubscriptionOAuthUsageService пишет usage.Record(..., source: "oauth"), но не трогает
        // SubscriptionActivityTracker — решение Андрея: идл-критерий смотрит только на
        // настоящие ходы/пинги, иначе живой OAuth-токен маскировал бы простаивающий аккаунт.
        var (svc, _, usage, _) = MkService(subKeys: ["second"]);
        usage.Record("five_hour", 0.5, "allowed", isUsingOverage: false,
            resetsAt: DateTime.UtcNow.AddHours(2).ToString("o"), subscriptionKey: "second", source: "oauth");

        svc.KeysDueForPing(TimeSpan.FromMinutes(5)).Should().Contain("second");
    }

    [Fact]
    public void RecordAndGuard_СамПинг_ТрогатьТрекерНеОбязан_НоЗаписьИдётСSourceProbe()
    {
        var (svc, _, usage, _) = MkService(subKeys: ["second"]);
        var msg = new RateLimitMessage("five_hour", DateTime.UtcNow.AddHours(2).ToString("o"),
            "allowed", 0.3, false);

        svc.RecordAndGuard("second", msg);

        var snap = usage.GetAll().Should().ContainSingle().Subject;
        snap.Source.Should().Be("probe");
        snap.SubscriptionKey.Should().Be("second");
    }

    [Fact]
    public void RecordAndGuard_Rejected_ВыводитИзРотации()
    {
        var (svc, pool, _, _) = MkService(subKeys: ["second"]);
        var msg = new RateLimitMessage("five_hour", DateTime.UtcNow.AddHours(2).ToString("o"),
            "rejected", null, false);

        svc.RecordAndGuard("second", msg);

        pool.IsExhausted("second").Should().BeTrue();
    }

    [Fact]
    public void RecordAndGuard_ЗдоровыйОтветНаИсчерпанном_ВозвращаетВРотацию()
    {
        var (svc, pool, _, _) = MkService(subKeys: ["second"]);
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        svc.RecordAndGuard("second", new RateLimitMessage("five_hour",
            DateTime.UtcNow.AddHours(2).ToString("o"), "allowed", 0.1, false));

        pool.IsExhausted("second").Should().BeFalse();
    }

    [Fact]
    public void RecordAndGuard_RejectedНеизвестногоОкна_НеВыводитИзРотации()
    {
        // Инцидент 2026-08-02: seven_day_overage_included с rejected — транзитная телеметрия
        // CLI, ходы на аккаунте продолжали проходить. Пишем снимок, но пул не трогаем.
        var (svc, pool, usage, _) = MkService(subKeys: ["second"]);
        var msg = new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "rejected", null, false);

        svc.RecordAndGuard("second", msg);

        pool.IsExhausted("second").Should().BeFalse();
        usage.GetAll().Should().ContainSingle(s => s.LimitType == "seven_day_overage_included");
    }

    [Fact]
    public void RecordAndGuard_НеизвестноеОкно_НеСнимаетПометкуИсчерпания()
    {
        // Симметрия: неизвестное окно не банит и не разбанивает.
        var (svc, pool, _, _) = MkService(subKeys: ["second"]);
        pool.MarkExhausted("second", DateTime.UtcNow.AddHours(2));

        svc.RecordAndGuard("second", new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "allowed_warning", 0.3, false));

        pool.IsExhausted("second").Should().BeTrue();
    }

    // P31: rate_limit_event — доказательство аутентификации (до лимитов запрос не дошёл бы).
    // Снимаем auth-dead по ЛЮБОМУ окну (а не только exhaustion-окну) и независимо от исчерпания:
    // probe ходит на auth-dead аккаунты, и это единственный путь узнать, что протухший токен
    // починили. До фикса MarkAuthDead был необратим вне Reset, гейтящегося исчерпанием (блокер P29).
    [Fact]
    public void RecordAndGuard_ЗдоровыйОтвет_СнимаетAuthDead_ДажеПоНеизвестномуОкну()
    {
        var (svc, pool, _, _) = MkService(subKeys: ["second"]);
        pool.MarkAuthDead("second");

        // Неизвестное окно (seven_day_overage_included) — раньше оно вообще не трогало пул,
        // но auth-dead обязано сняться: запрос авторизовался и дошёл.
        svc.RecordAndGuard("second", new RateLimitMessage("seven_day_overage_included",
            DateTime.UtcNow.AddDays(5).ToString("o"), "allowed", 0.1, false));

        pool.IsAuthDead("second").Should().BeFalse("подписка ответила — auth-dead снят");
    }

    // P31, сценарий приёмки: транзитный 401 выключил подписку → перевход → probe прошёл →
    // подписка снова в ротации без рестарта процесса. auth-dead при этом НЕ исчерпана.
    [Fact]
    public void RecordAndGuard_AuthDeadНеИсчерпана_СнимаетПометкуИВозвращаетВРотацию()
    {
        var (svc, pool, _, _) = MkService(subKeys: ["acc-a", "acc-b"]);
        pool.MarkAuthDead("acc-a");
        pool.IsExhausted("acc-a").Should().BeFalse("auth-dead — это не квота");

        svc.RecordAndGuard("acc-a", new RateLimitMessage("five_hour",
            DateTime.UtcNow.AddHours(2).ToString("o"), "allowed", 0.2, false));

        pool.IsAuthDead("acc-a").Should().BeFalse();
        pool.IsInRotation("acc-a").Should().BeTrue("подписка вернулась в ротацию");
    }

    // --- IdlePingMinutes: 0 выключает механизм ---

    [Fact]
    public void IdlePingMinutes_Дефолт_ПятьМинут()
    {
        var (svc, _, _, _) = MkService(subKeys: ["second"]);
        svc.ResolveIdleThreshold().Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void IdlePingMinutes_Ноль_МеханизмВыключен()
    {
        var (svc, _, _, _) = MkService(idlePingMinutes: 0, subKeys: ["second"]);
        svc.ResolveIdleThreshold().Should().BeNull();
    }

    [Fact]
    public void IdlePingMinutes_Кастомный_Читается()
    {
        var (svc, _, _, _) = MkService(idlePingMinutes: 10, subKeys: ["second"]);
        svc.ResolveIdleThreshold().Should().Be(TimeSpan.FromMinutes(10));
    }

    // --- Состав аргументов пробного хода ---

    [Fact]
    public void BuildProbeArgs_СодержитМодельHaiku()
    {
        var args = SubscriptionUsageWarmupService.BuildProbeArgs([]);

        args.Should().ContainInOrder("--model", "haiku");
    }

    [Fact]
    public void BuildProbeArgs_ПоточныйJsonСВербозом()
    {
        var args = SubscriptionUsageWarmupService.BuildProbeArgs([]);

        args.Should().ContainInOrder("--output-format", "stream-json");
        args.Should().Contain("--verbose");
        args.Should().Contain("--print");
    }

    [Fact]
    public void BuildProbeArgs_ПробрасываетHooksOffArgs()
    {
        var args = SubscriptionUsageWarmupService.BuildProbeArgs(["--settings", "path/to/settings.json"]);

        args.Should().ContainInOrder("--settings", "path/to/settings.json");
    }
}
