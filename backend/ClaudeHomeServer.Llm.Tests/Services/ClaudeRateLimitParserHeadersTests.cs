using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Разбор заголовков anthropic-ratelimit-unified-* (шлюз LLM, ADR-016) в те же
/// RateLimitMessage, что дают rate_limit_event CLI.
/// </summary>
public class ClaudeRateLimitParserHeadersTests
{
    private static Func<string, string?> Headers(params (string Name, string Value)[] headers)
    {
        var map = headers.ToDictionary(h => ClaudeRateLimitParser.UnifiedHeaderPrefix + h.Name, h => h.Value,
            StringComparer.OrdinalIgnoreCase);
        return name => map.TryGetValue(name, out var v) ? v : null;
    }

    [Fact]
    public void ОбаОкна_ИменаКакУRateLimitEvent()
    {
        var list = ClaudeRateLimitParser.FromUnifiedHeaders(Headers(
            ("status", "allowed"),
            ("5h-status", "allowed"), ("5h-utilization", "0.42"), ("5h-reset", "1790000000"),
            ("7d-status", "allowed_warning"), ("7d-utilization", "0.9"), ("7d-reset", "1790500000"),
            ("representative-claim", "five_hour")));

        list.Should().HaveCount(2);
        var h5 = list.Single(m => m.LimitType == "five_hour");
        h5.Utilization.Should().Be(0.42);
        h5.Status.Should().Be("allowed");
        h5.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1790000000).ToString("o"));
        var d7 = list.Single(m => m.LimitType == "seven_day");
        d7.Status.Should().Be("allowed_warning");
        d7.Utilization.Should().Be(0.9);
    }

    [Fact]
    public void БезСтатусаОкна_ОбщийСтатусТолькоДляОкнаИзRepresentativeClaim()
    {
        var list = ClaudeRateLimitParser.FromUnifiedHeaders(Headers(
            ("status", "rejected"), ("representative-claim", "seven_day"),
            ("5h-utilization", "0.3"), ("7d-utilization", "1.0"), ("7d-reset", "1790500000")));

        list.Single(m => m.LimitType == "seven_day").Status.Should().Be("rejected");
        list.Single(m => m.LimitType == "five_hour").Status.Should().BeNull("отказ решило недельное окно");
    }

    [Fact]
    public void ОкноВыбрано_ПерерасходРазрешён_ИдётПерерасход()
    {
        var list = ClaudeRateLimitParser.FromUnifiedHeaders(Headers(
            ("5h-status", "allowed"), ("5h-utilization", "1.0"), ("overage-status", "allowed")));

        list.Single().IsUsingOverage.Should().BeTrue();
        list.Single().OverageStatus.Should().Be("allowed");
    }

    [Fact]
    public void НетЗаголовков_ПустойСписок() =>
        ClaudeRateLimitParser.FromUnifiedHeaders(_ => null).Should().BeEmpty();
}
