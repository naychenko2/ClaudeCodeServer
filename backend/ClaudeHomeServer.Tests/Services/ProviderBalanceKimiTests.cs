using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа GET {ApiBaseUrl}/usages подписки Kimi for Coding. Эндпоинт недокументированный
// (подсмотрен в opencode-quota, проверен живьём 26.07.2026), поэтому контракт фиксируем тестами:
// числа приходят строками, основное окно — самое короткое из limits[] (5-часовое, 300 мин),
// недельное — корневое "usage" и уезжает в Secondary*.
public class ProviderBalanceKimiTests
{
    // Живой payload (снимок 26.07.2026): 5-ч окно израсходовано на 31%, неделя — на 6%
    private const string RealPayload = """
        {"user":{"userId":"d6uc03bacc4bjlmefq9g","region":"REGION_OVERSEA","membership":{"level":"LEVEL_ADVANCED"}},
        "usage":{"limit":"100","used":"6","remaining":"94","resetTime":"2026-08-02T10:46:54.841042Z"},
        "limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},
          "detail":{"limit":"100","used":"31","remaining":"69","resetTime":"2026-07-26T15:46:54.841042Z"}}],
        "parallel":{"limit":"30"},"totalQuota":{},
        "authentication":{"method":"METHOD_API_KEY","scope":"FEATURE_CODING"},
        "subType":"TYPE_PURCHASE","domain":"DOMAIN_NEXUS"}
        """;

    private static ProviderBalance? Parse(string json) =>
        ProviderBalanceService.ParseKimiUsages(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ЖивойОтвет_ОсновноеОкно_Пятичасовое()
    {
        var b = Parse(RealPayload);

        b.Should().NotBeNull();
        b!.Available.Should().BeTrue();
        b.Currency.Should().Be("%");
        b.TotalBalance.Should().Be("69");
        b.ResetsAt.Should().Be(DateTime.Parse("2026-07-26T15:46:54.841042Z", null,
            System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public void ЖивойОтвет_НедельноеОкно_УезжаетВSecondary()
    {
        var b = Parse(RealPayload);

        b!.SecondaryLabel.Should().Be("остаток квоты · неделя");
        b.SecondaryValue.Should().Be("94");
        b.SecondaryResetsAt.Should().Be(DateTime.Parse("2026-08-02T10:46:54.841042Z", null,
            System.Globalization.DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public void ВыборОкна_БерётСамоеКороткое()
    {
        // Часовое окно (60 HOUR... нет — 1 HOUR) короче 300-минутного: основным станет оно
        const string json = """
            {"usage":{"limit":"100","used":"0","remaining":"100","resetTime":"2026-08-02T00:00:00Z"},
            "limits":[
              {"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},"detail":{"limit":"100","used":"50","remaining":"50"}},
              {"window":{"duration":1,"timeUnit":"TIME_UNIT_HOUR"},"detail":{"limit":"100","used":"10","remaining":"90"}}
            ]}
            """;
        Parse(json)!.TotalBalance.Should().Be("90");
    }

    [Fact]
    public void БезLimits_ЖивёмНаНедельномОкне()
    {
        const string json = """{"usage":{"limit":"100","used":"6","remaining":"94","resetTime":"2026-08-02T10:46:54Z"}}""";
        var b = Parse(json);

        b!.TotalBalance.Should().Be("94");
        b.SecondaryValue.Should().BeNull(); // неделя и есть основное — дублировать нечего
    }

    [Fact]
    public void ЛимитНе100_НормализуетВПроценты()
    {
        const string json = """{"usage":{"limit":"500","used":"100","remaining":"400"}}""";
        Parse(json)!.TotalBalance.Should().Be("80");
    }

    [Fact]
    public void ЧислаБезКавычек_ТожеПарсятся()
    {
        const string json = """{"usage":{"limit":100,"used":25,"remaining":75}}""";
        Parse(json)!.TotalBalance.Should().Be("75");
    }

    [Fact]
    public void НетRemaining_ВыводитИзLimitМинусUsed()
    {
        const string json = """{"usage":{"limit":"100","used":"30"}}""";
        Parse(json)!.TotalBalance.Should().Be("70");
    }

    [Fact]
    public void НулевойЛимит_ОкноПропускается()
    {
        const string json = """{"usage":{"limit":"0","used":"0","remaining":"0"}}""";
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void ПустойОтвет_Null()
    {
        Parse("""{"user":{"userId":"x"}}""").Should().BeNull();
    }
}
