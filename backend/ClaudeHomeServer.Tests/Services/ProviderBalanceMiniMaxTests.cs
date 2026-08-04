using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Разбор ответа GET https://www.minimax.io/v1/token_plan/remains подписки MiniMax Token Plan.
// Эндпоинт недокументированный, контракт фиксируем тестами по реальному ответу — снимок
// живого запроса 04.08.2026 с настоящим ключом (сам ключ в фикстуре не участвует).
public class ProviderBalanceMiniMaxTests
{
    // Живой payload (снимок 04.08.2026): строка "general" — квота CLI, "video" — видеогенерация,
    // к нашей квоте отношения не имеет и должна игнорироваться
    private const string RealPayload = """
        {"model_remains":[{"start_time":1785819600000,"end_time":1785837600000,"remains_time":7763572,
        "current_interval_total_count":0,"current_interval_usage_count":0,"model_name":"general",
        "current_weekly_total_count":0,"current_weekly_usage_count":0,"weekly_start_time":1785715200000,
        "weekly_end_time":1786320000000,"weekly_remains_time":490163572,"current_interval_status":1,
        "current_interval_remaining_percent":95,"current_weekly_status":1,"current_weekly_remaining_percent":90},
        {"start_time":1785801600000,"end_time":1785888000000,"remains_time":58163572,
        "current_interval_total_count":3,"current_interval_usage_count":0,"model_name":"video",
        "current_weekly_total_count":21,"current_weekly_usage_count":0,"weekly_start_time":1785715200000,
        "weekly_end_time":1786320000000,"weekly_remains_time":490163572,"current_interval_status":1,
        "current_interval_remaining_percent":100,"current_weekly_status":1,"current_weekly_remaining_percent":100}],
        "base_resp":{"status_code":0,"status_msg":"success"}}
        """;

    private static ProviderBalance? Parse(string json) =>
        ProviderBalanceService.ParseMiniMaxRemains(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ЖивойОтвет_ОсновноеОкно_Пятичасовое()
    {
        var b = Parse(RealPayload);

        b.Should().NotBeNull();
        b!.Available.Should().BeTrue();
        b.Currency.Should().Be("%");
        b.TotalBalance.Should().Be("95");
        b.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1785837600000).UtcDateTime);
    }

    [Fact]
    public void ЖивойОтвет_НедельноеОкно_ВторымВWindows()
    {
        var b = Parse(RealPayload);

        b!.Windows.Should().HaveCount(2);
        var weekly = b.Windows![1];
        weekly.Label.Should().Be("Неделя");
        weekly.Value.Should().Be("90");
        weekly.Unit.Should().Be("percent");
        weekly.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1786320000000).UtcDateTime);
    }

    [Fact]
    public void ЖивойОтвет_СтрокаVideo_Игнорируется()
    {
        // Учитывается только строка "general": её 95/90, а не 100/100 строки "video"
        var b = Parse(RealPayload);

        b!.TotalBalance.Should().Be("95");
        b.Windows![0].Value.Should().Be("95");
        b.Windows[1].Value.Should().Be("90");
    }

    [Fact]
    public void ЖивойОтвет_ПодписиИзФактическойДлительностиОкон()
    {
        // start_time..end_time = 5 часов, weekly_* = 7 суток — подписи выводятся из них,
        // а не из позиции окна в списке
        var b = Parse(RealPayload);

        b!.Windows![0].Label.Should().Be("5 часов");
        b.Windows[1].Label.Should().Be("Неделя");
        b.TrackHistory.Should().BeTrue();
    }

    [Fact]
    public void ТолькоНедельноеОкно_ПодписьНеВрётПроПятьЧасов()
    {
        // Провайдер перестал отдавать интервальный процент: единственное окно — недельное,
        // и подписано оно неделей. Историю по нему не ведём — ряд графика другой
        const string json = """
            {"model_remains":[{"model_name":"general","weekly_start_time":1785715200000,
            "weekly_end_time":1786320000000,"current_weekly_remaining_percent":90}],
            "base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("90");
        b.Windows.Should().HaveCount(1);
        b.Windows![0].Label.Should().Be("Неделя");
        b.TrackHistory.Should().BeFalse();
    }

    [Fact]
    public void ГраницИнтервалаНет_ПодписьБезПериода()
    {
        // Длительность окна неизвестна (нет start_time) — период в подписи не выдумываем
        const string json = """
            {"model_remains":[{"model_name":"general","end_time":1785837600000,
            "current_interval_remaining_percent":94}],"base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b!.Windows![0].Label.Should().Be("Окно квоты");
        b.Windows[0].ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1785837600000).UtcDateTime);
    }

    [Fact]
    public void БитыйEndTime_ГаситТолькоСброс_НеВесьБаланс()
    {
        const string json = """
            {"model_remains":[{"model_name":"general","start_time":1785819600000,"end_time":1e30,
            "current_interval_remaining_percent":94}],"base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b.Should().NotBeNull();
        b!.TotalBalance.Should().Be("94");
        b.ResetsAt.Should().BeNull();
        b.Windows![0].Label.Should().Be("Окно квоты"); // длительность тоже не посчитать
    }

    [Fact]
    public void EndTimeВСекундах_РазбираетсяКакСекунды()
    {
        // Обратный случай битого времени: секунды вместо миллисекунд дали бы «сброс 1970»
        const string json = """
            {"model_remains":[{"model_name":"general","start_time":1785819600,"end_time":1785837600,
            "current_interval_remaining_percent":94}],"base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b!.ResetsAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1785837600).UtcDateTime);
        b.Windows![0].Label.Should().Be("5 часов");
    }

    [Fact]
    public void EndTimeНет_ResetsAtNull()
    {
        const string json = """
            {"model_remains":[{"model_name":"general","current_interval_remaining_percent":94}],
            "base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b!.TotalBalance.Should().Be("94");
        b.ResetsAt.Should().BeNull();
    }

    [Fact]
    public void ПроцентСтрокой_Разбирается()
    {
        const string json = """
            {"model_remains":[{"model_name":"general","current_interval_remaining_percent":"42.5"}],
            "base_resp":{"status_code":0}}
            """;
        Parse(json)!.TotalBalance.Should().Be("42.5");
    }

    [Fact]
    public void ПроцентВнеДиапазона_Обрезается()
    {
        const string json = """
            {"model_remains":[{"model_name":"general","current_interval_remaining_percent":150,
            "current_weekly_remaining_percent":-5}],"base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b!.Windows![0].Value.Should().Be("100");
        b.Windows[1].Value.Should().Be("0");
    }

    [Fact]
    public void ПроцентНеЧислоИНеСтрока_ОкноПропускается()
    {
        // ReadNumber не должен падать на bool/object — разборщик обязан пережить мусор
        const string json = """
            {"model_remains":[{"model_name":"general","current_interval_remaining_percent":true,
            "current_weekly_remaining_percent":90}],"base_resp":{"status_code":0}}
            """;
        var b = Parse(json);

        b!.Windows.Should().HaveCount(1);
        b.TotalBalance.Should().Be("90");
    }

    [Fact]
    public void СтрокиGeneralНет_Null()
    {
        const string json = """
            {"model_remains":[{"model_name":"video","current_interval_remaining_percent":100}],
            "base_resp":{"status_code":0,"status_msg":"success"}}
            """;
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void СтатусНеНоль_Null()
    {
        const string json = """{"base_resp":{"status_code":1004,"status_msg":"login fail"}}""";
        Parse(json).Should().BeNull();
    }

    [Fact]
    public void ПустойОтвет_Null()
    {
        Parse("""{}""").Should().BeNull();
    }
}
