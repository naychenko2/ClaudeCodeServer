using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа GET {BalanceUrl}/monitor/usage/quota/limit подписки GLM (z.ai) Coding Plan.
// Эндпоинт недокументированный, контракт фиксируем тестами по реальному ответу — снимок
// живого запроса 07.08.2026 с настоящим ключом (сам ключ в фикстуре не участвует).
public class ProviderBalanceGlmTests
{
    // Живой payload (снимок 07.08.2026): два окна токенов (5 часов израсходовано на 60%,
    // неделя — на 28%) и месячный лимит вызовов веб-инструментов (101/4000).
    private const string RealPayload = """
        {"code":200,"msg":"Operation successful","success":true,
        "data":{"level":"max","limits":[
        {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":60,"nextResetTime":1786101061032},
        {"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":28,"nextResetTime":1786308142998},
        {"type":"TIME_LIMIT","unit":5,"number":1,"usage":4000,"currentValue":101,"remaining":3899,
        "percentage":2,"nextResetTime":1786567342991,
        "usageDetails":[{"modelCode":"search-prime","usage":93},
        {"modelCode":"web-reader","usage":8},{"modelCode":"zread","usage":0}]}]}}
        """;

    private static ProviderBalance? Parse(string json) =>
        ProviderBalanceService.ParseGlmQuota(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ЖивойОтвет_ОсновноеОкно_Пятичасовое()
    {
        var b = Parse(RealPayload);

        b.Should().NotBeNull();
        b!.Available.Should().BeTrue();
        b.Currency.Should().Be("%");
        b.TotalBalance.Should().Be("40"); // 100 − 60
        b.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786101061032).UtcDateTime);
        b.TrackHistory.Should().BeTrue();
    }

    [Fact]
    public void ЖивойОтвет_ТриОкнаВПорядкеДлительности()
    {
        var b = Parse(RealPayload);

        b!.Windows.Should().HaveCount(3);
        // Короткое (5 часов) первым, затем неделя, затем месячный лимит инструментов
        b.Windows![0].Label.Should().Be("5 часов");
        b.Windows[0].Value.Should().Be("40"); // 100 − 60
        b.Windows[0].Unit.Should().Be("percent");
        b.Windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786101061032).UtcDateTime);

        b.Windows[1].Label.Should().Be("Неделя");
        b.Windows[1].Value.Should().Be("72"); // 100 − 28
        b.Windows[1].Unit.Should().Be("percent");
        b.Windows[1].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786308142998).UtcDateTime);
    }

    [Fact]
    public void ЖивойОтвет_ВебИнструменты_ТретьимОкномCount()
    {
        var b = Parse(RealPayload);

        var tools = b!.Windows![2];
        tools.Label.Should().Be("Веб-инструменты"); // НЕ «Месяц» — иначе читалось бы как квота токенов
        tools.Value.Should().Be("101/4000");
        tools.Unit.Should().Be("count");
        tools.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786567342991).UtcDateTime);
    }

    [Fact]
    public void БезЧасовогоОкна_ОсновноеНедельноеИсторияНеВедётся()
    {
        // Провайдер перестал отдавать 5-часовое окно: единственное токен-окно — недельное,
        // оно же основное. Историю по нему не ведём — ряд графика другой
        const string json = """
            {"data":{"limits":[
            {"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":28,"nextResetTime":1786308142998}]}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("72");
        b.Windows.Should().HaveCount(1);
        b.Windows![0].Label.Should().Be("Неделя");
        b.TrackHistory.Should().BeFalse();
    }

    [Fact]
    public void НеизвестныйUnit_ПодписьБезПериода()
    {
        // Код unit недокументентирован: незнакомый → длительность неизвестна, период в подписи
        // не выдумываем. Часовым такое окно не считаем — история не ведётся
        const string json = """
            {"data":{"limits":[
            {"type":"TOKENS_LIMIT","unit":99,"number":2,"percentage":50,"nextResetTime":1786101061032}]}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("50");
        b.Windows![0].Label.Should().Be("Окно квоты");
        b.TrackHistory.Should().BeFalse();
    }

    [Fact]
    public void ЧислаСтрокой_Разбираются()
    {
        // percentage / currentValue / usage могут прийти строками
        const string json = """
            {"data":{"limits":[
            {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":"60","nextResetTime":1786101061032},
            {"type":"TIME_LIMIT","unit":5,"number":1,"usage":"4000","currentValue":"101",
            "nextResetTime":1786567342991}]}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("40");
        b.Windows![0].Value.Should().Be("40");
        b.Windows[1].Value.Should().Be("101/4000");
    }

    [Fact]
    public void ПроцентВнеДиапазона_Обрезается()
    {
        const string json = """
            {"data":{"limits":[
            {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":150,"nextResetTime":1786101061032}]}}
            """;
        Parse(json)!.Windows![0].Value.Should().Be("0");
    }

    [Fact]
    public void НеразборчивыйПроцент_ОкноПропускается()
    {
        // ReadNumber не должен падать на bool/object — разборщик обязан пережить мусор;
        // битое токен-окно не должно ронять валидный TIME_LIMIT и наоборот
        const string json = """
            {"data":{"limits":[
            {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":true},
            {"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":28,"nextResetTime":1786308142998}]}}
            """;
        var b = Parse(json);

        b!.Windows.Should().HaveCount(1);
        b.TotalBalance.Should().Be("72");
    }

    [Fact]
    public void НеразборчивыйCount_ОкноИнструментовПропускается()
    {
        // currentValue не разобрать — count-окно не добавляем, токен-окна живут
        const string json = """
            {"data":{"limits":[
            {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":60,"nextResetTime":1786101061032},
            {"type":"TIME_LIMIT","unit":5,"number":1,"usage":4000,"currentValue":false}]}}
            """;
        var b = Parse(json);

        b!.Windows.Should().HaveCount(1);
        b.Windows![0].Unit.Should().Be("percent");
    }

    [Fact]
    public void ТолькоЛимитИнструментов_БезТокенов_Null()
    {
        // Общей безоконной квоты токенов у GLM нет — без percent-окон баланс показать нечем
        const string json = """
            {"data":{"limits":[
            {"type":"TIME_LIMIT","unit":5,"number":1,"usage":4000,"currentValue":101,
            "nextResetTime":1786567342991}]}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void НетМассиваLimits_Null()
    {
        Parse("""{}""").Should().BeNull();
        Parse("""{"data":{"level":"max"}}""").Should().BeNull();
    }
}
