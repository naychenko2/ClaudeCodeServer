using System.Text.Json;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Внутренний повтор CLI (system/api_retry, CLI 2.1.246+): headless-сессия сама дотягивает
// оборванный ответ и пережидает 429/529 внутри процесса. Паспорт хода обязан отражать ФАКТ
// этих повторов (сколько раз, суммарное ожидание, последний статус), а не настроенный
// потолок. Разбор и накопление — чистая функция ClaudeSession.Accumulate: здесь её ветки.
public class ClaudeSessionApiRetryTests
{
    private static JsonElement Event(string json) => JsonDocument.Parse(json).RootElement;

    // Реальная строка события из бинаря CLI: retry_delay_ms + числовой error_status.
    // Один повтор — счётчик 1, ожидание 2000 мс, статус «529» (число приводится к строке).
    [Fact]
    public void ОдинПовтор_СЗадержкойИСтатусом_КопитФакт()
    {
        var evt = Event("""
            {"type":"system","subtype":"api_retry","attempt":1,"max_retries":2,
             "retry_delay_ms":2000,"error_status":529,"error":"Overloaded"}
            """);

        var stats = ClaudeSession.Accumulate(new CliRetryStats(0, 0, null), evt);

        stats.Count.Should().Be(1);
        stats.WaitMs.Should().Be(2000);
        stats.LastStatus.Should().Be("529", "числовой error_status приводится к строке");
    }

    // no_response-ветка: retry_delay_ms нет, error_status=null — повтор всё же был
    // (счётчик растёт), но зафиксированной задержки и статуса нет.
    [Fact]
    public void ПовторБезЗадержки_СчётчикРастёт_СуммаИСтатусНеМеняются()
    {
        var evt = Event("""
            {"type":"system","subtype":"api_retry","attempt":2,"max_retries":2,
             "error_status":null,"no_response":{"waited_ms":3000}}
            """);

        var stats = ClaudeSession.Accumulate(new CliRetryStats(1, 2000, "529"), evt);

        stats.Count.Should().Be(2, "повтор засчитан, хотя задержки не было");
        stats.WaitMs.Should().Be(2000, "без retry_delay_ms сумма не растёт");
        stats.LastStatus.Should().Be("529", "null-статус не затирает последний известный");
    }

    // Накопление за ход: несколько api_retry-событий складываются — счётчик растёт,
    // задержки суммируются, статус — последний непустой.
    [Fact]
    public void НакоплениеЗаХод_СуммируетЗадержкиИДержитПоследнийСтатус()
    {
        var e1 = Event("""
            {"type":"system","subtype":"api_retry","attempt":1,"max_retries":3,
             "retry_delay_ms":2000,"error_status":429,"error":"Too many requests"}
            """);
        var e2 = Event("""
            {"type":"system","subtype":"api_retry","attempt":2,"max_retries":3,
             "retry_delay_ms":5000,"error_status":529,"error":"Overloaded"}
            """);

        var stats = ClaudeSession.Accumulate(
            ClaudeSession.Accumulate(new CliRetryStats(0, 0, null), e1), e2);

        stats.Count.Should().Be(2);
        stats.WaitMs.Should().Be(7000, "сумма retry_delay_ms всех повторов хода");
        stats.LastStatus.Should().Be("529", "последний непустой статус, а не первый");
    }
}
