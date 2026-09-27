using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Единая точка учёта лимитов пула (ADR-016, план §2 п. 2): её зовут и ход (rate_limit_event),
/// и шлюз (заголовки). Логика перенесена из SessionManager без изменений.
/// </summary>
public class SubscriptionPoolRateLimitRecorderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "limits_" + Guid.NewGuid().ToString("N"));
    private readonly UsageService _usage;
    private readonly ClaudeSubscriptionPool _pool;
    private readonly SubscriptionActivityTracker _activity = new();
    private readonly SubscriptionLimitRecorder _sut;

    public SubscriptionPoolRateLimitRecorderTests()
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            [$"{ClaudeSubscriptionPool.Section}:a:OAuthToken"] = "ta",
            [$"{ClaudeSubscriptionPool.Section}:b:OAuthToken"] = "tb",
        }).Build();
        _usage = new UsageService(config);
        _pool = new ClaudeSubscriptionPool(config, _usage);
        _sut = new SubscriptionLimitRecorder(_usage, _pool, _activity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static RateLimitMessage Msg(string status, double? util = null, string window = "five_hour") =>
        new(window, DateTime.UtcNow.AddHours(2).ToString("o"), status, util);

    [Fact]
    public void Rejected_МетитИсчерпание_ПишетСнимок_ТрогаетАктивность()
    {
        _sut.Record("a", Msg("rejected"), "gateway").Should().Be(LimitRecordOutcome.Exhausted);

        _pool.IsExhausted("a").Should().BeTrue();
        _usage.GetAllBySubscription()["a"].Should().ContainSingle(s => s.Source == "gateway");
        _activity.IsIdle("a", TimeSpan.FromMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void ВыбраноБезПерерасхода_Исчерпание_СПерерасходом_Нет()
    {
        _sut.Record("a", Msg("allowed", 1.0), "turn").Should().Be(LimitRecordOutcome.Exhausted);
        _sut.Record("b", new RateLimitMessage("five_hour", null, "allowed", 1.0, IsUsingOverage: true), "turn")
            .Should().Be(LimitRecordOutcome.Recorded);
        _pool.IsExhausted("b").Should().BeFalse();
    }

    [Fact]
    public void НедавнийОтказПоМодели_Подавляет()
    {
        _pool.MarkModelUnavailable("b", "opus", FallbackErrorClass.ModelNoAccess);

        _sut.Record("a", Msg("rejected"), "turn").Should().Be(LimitRecordOutcome.Suppressed);
        _pool.IsExhausted("a").Should().BeFalse();
    }

    [Fact]
    public void РотациейВладеетАдаптер_Подавляет()
    {
        _sut.Record("a", Msg("rejected"), "turn", rotationOwnedElsewhere: () => true)
            .Should().Be(LimitRecordOutcome.Suppressed);
        _pool.IsExhausted("a").Should().BeFalse();
    }

    [Fact]
    public void ЖивойОтвет_СнимаетAuthDeadИИсчерпание()
    {
        _pool.MarkAuthDead("a");
        _pool.MarkExhausted("a");

        _sut.Record("a", Msg("allowed", 0.1), "gateway").Should().Be(LimitRecordOutcome.Recorded);

        _pool.IsAuthDead("a").Should().BeFalse();
        _pool.IsExhausted("a").Should().BeFalse();
    }

    [Fact]
    public void НеизвестноеОкно_ПулНеТрогает()
    {
        _sut.Record("a", Msg("rejected", window: "seven_day_overage_included"), "turn")
            .Should().Be(LimitRecordOutcome.Recorded);
        _pool.IsExhausted("a").Should().BeFalse();
    }

    [Fact]
    public void БезКлюча_ТолькоСнимок()
    {
        _sut.Record(null, Msg("rejected"), "turn").Should().Be(LimitRecordOutcome.Recorded);
        _pool.IsExhausted("a").Should().BeFalse();
        _pool.IsExhausted("claude").Should().BeFalse();
    }
}
