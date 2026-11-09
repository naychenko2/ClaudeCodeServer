using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа GET {ApiBaseUrl}/usages подписки Kimi for Coding. Эндпоинт недокументированный
// (подсмотрен в opencode-quota, проверен живьём 26.07.2026), поэтому контракт фиксируем тестами:
// числа приходят строками, основное окно — самое короткое из limits[] (5-часовое, 300 мин),
// недельное — корневое "usage" и уезжает вторым элементом Windows.
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
    public void ЖивойОтвет_НедельноеОкно_ВторымВWindows()
    {
        var b = Parse(RealPayload);

        b!.Windows.Should().HaveCount(2);
        var weekly = b.Windows![1];
        weekly.Label.Should().Be("Неделя");
        weekly.Value.Should().Be("94");
        weekly.Unit.Should().Be("percent");
        weekly.ResetsAt.Should().Be(DateTime.Parse("2026-08-02T10:46:54.841042Z", null,
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
    public void ЖивойОтвет_ПодписиИзФактическойДлительностиОкон()
    {
        var b = Parse(RealPayload);

        b!.Windows![0].Label.Should().Be("5 часов"); // window 300 минут
        b.Windows[1].Label.Should().Be("Неделя");
        b.TrackHistory.Should().BeTrue();
    }

    [Fact]
    public void БезLimits_ЖивёмНаНедельномОкне()
    {
        const string json = """{"usage":{"limit":"100","used":"6","remaining":"94","resetTime":"2026-08-02T10:46:54Z"}}""";
        var b = Parse(json);

        b!.TotalBalance.Should().Be("94");
        b.Windows.Should().HaveCount(1); // неделя и есть основное — дублировать нечего
        // Подпись идёт от окна, а не от позиции: пятичасового окна тут нет и обещать его нельзя
        b.Windows![0].Label.Should().Be("Неделя");
        b.TrackHistory.Should().BeFalse(); // ряд графика другой — точку не пишем
    }

    [Fact]
    public void ОкноБезДлительности_ПодписьБезПериода()
    {
        // limits[] без window: окно основное, но сколько оно длится — неизвестно
        const string json = """
            {"limits":[{"detail":{"limit":"100","used":"20","remaining":"80"}}]}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("80");
        b.Windows![0].Label.Should().Be("Окно квоты");
    }

    [Fact]
    public void НеделяСовпалаПоЗначениюНоНеПоСбросу_ОтдаётсяВторымОкном()
    {
        // Дедуп идёт по (остаток, сброс), а не по равенству структуры целиком
        const string json = """
            {"usage":{"limit":"100","used":"50","remaining":"50","resetTime":"2026-08-02T00:00:00Z"},
            "limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},
              "detail":{"limit":"100","used":"50","remaining":"50","resetTime":"2026-07-26T00:00:00Z"}}]}
            """;
        var b = Parse(json);

        b!.Windows.Should().HaveCount(2);
        b.Windows![1].Label.Should().Be("Неделя");
    }

    [Fact]
    public void ResetTimeБезZ_СчитаетсяUtc()
    {
        // Без AssumeUniversal время трактовалось бы как локальное: на Windows (MSK) сброс
        // уезжал на три часа относительно Linux-CI
        const string json = """{"usage":{"limit":"100","remaining":"94","resetTime":"2026-08-02T10:46:54"}}""";

        Parse(json)!.ResetsAt.Should().Be(new DateTime(2026, 8, 2, 10, 46, 54, DateTimeKind.Utc));
    }

    [Fact]
    public void ЗначениеНеЧислоИНеСтрока_ОкноПропускается()
    {
        // ReadNumber не должен падать на bool/object — разборщик обязан пережить мусор
        const string json = """{"usage":{"limit":true,"remaining":{"x":1}}}""";

        Parse(json).Should().BeNull();
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

    // ── Параллельные сессии и уровень подписки (дособрано 07.08.2026) ─────────────────

    [Fact]
    public void ЖивойОтвет_PlanLabel_ИмяПодпискиБезПриставки()
    {
        // Поле PlanLabel — primary: «Advanced» без «Подписка:», подпись рисует интерфейс
        Parse(RealPayload)!.PlanLabel.Should().Be("Advanced");
    }

    [Fact]
    public void ПараллельныеСессии_CountОкноПоследним()
    {
        // parallel.limit — строка, занятость — длина details: окно «2/30» последним, count, без сброса
        const string json = """
            {"usage":{"limit":"100","used":"6","remaining":"94","resetTime":"2026-08-02T00:00:00Z"},
            "limits":[{"window":{"duration":300,"timeUnit":"TIME_UNIT_MINUTE"},
              "detail":{"limit":"100","used":"31","remaining":"69","resetTime":"2026-07-26T00:00:00Z"}}],
            "parallel":{"limit":"30","details":["a","b"]},
            "user":{"membership":{"level":"LEVEL_STANDARD"}}}
            """;
        var b = Parse(json);

        b!.Windows.Should().HaveCount(3); // 5 часов + неделя + параллельные
        var par = b.Windows![2];
        par.Label.Should().Be("Параллельные сессии");
        par.Value.Should().Be("2/30");
        par.Unit.Should().Be("count");
        par.ResetsAt.Should().BeNull();
        b.PlanLabel.Should().Be("Standard");
        // Квотные окна и история не изменились — доп. окно лишь добавилось последним
        b.Windows[0].Unit.Should().Be("percent");
        b.TrackHistory.Should().BeTrue();
    }

    [Fact]
    public void ПараллельныеБезDetails_ОкнаНет()
    {
        // limit без details — занятость неизвестна, count-окно не добавляем (как в снимке 26.07)
        const string json = """
            {"usage":{"limit":"100","remaining":"94","resetTime":"2026-08-02T00:00:00Z"},
            "parallel":{"limit":"30"}}
            """;
        var b = Parse(json);

        b!.Windows.Should().HaveCount(1);
        b.Windows![0].Unit.Should().Be("percent");
        b.PlanLabel.Should().BeNull(); // уровня подписки в ответе нет
    }

    [Fact]
    public void УровеньПодписки_НеСLEVEL_НеПоказываем()
    {
        // Чужой формат уровня (не LEVEL_*) — не угадываем, PlanLabel не ставим
        const string json = """
            {"usage":{"limit":"100","remaining":"94","resetTime":"2026-08-02T00:00:00Z"},
            "user":{"membership":{"level":"VIP_GOLD"}}}
            """;
        Parse(json)!.PlanLabel.Should().BeNull();
    }
}
