using ClaudeHomeServer.Telemetry;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Telemetry;

/// <summary>
/// Инварианты шкалы бакетов для <c>ccs.llm.duration</c>.
///
/// Контекст: с дефолтными границами OTel (потолок 10 000 мс) практически все ходы LLM
/// попадали в последний бакет (10000, +Inf]. Квантили считаются интерполяцией по бакетам,
/// поэтому p95/p99 упирались в потолок и не различали ход на 30 секунд и на 10 минут —
/// на живых данных p99 показывал 9975 мс независимо от реальной длительности.
///
/// Тесты стерегут шкалу от возврата к такому состоянию.
/// </summary>
public class HistogramBoundariesTests
{
    [Fact]
    public void Boundaries_AreStrictlyIncreasing()
    {
        // OTel требует строго возрастающие границы; нарушение = молча битая гистограмма
        var b = ServerMetrics.LlmDurationBoundaries;

        b.Should().NotBeEmpty();
        b.Should().BeInAscendingOrder();
        b.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Boundaries_CoverLongTurns()
    {
        // Ход через claude CLI легко идёт минутами, а с циклом «до готово» — и дольше.
        // Верхняя граница должна быть на порядок выше дефолтных 10 секунд, иначе
        // «самые долгие ходы» неотличимы друг от друга.
        ServerMetrics.LlmDurationBoundaries.Max()
            .Should().BeGreaterThanOrEqualTo(600_000,
                "иначе p99 упрётся в потолок, как было с дефолтными границами");
    }

    [Fact]
    public void Boundaries_HaveResolutionInTypicalRange()
    {
        // Основная масса ходов — единицы и десятки секунд. Там нужна детализация,
        // иначе медиана скачет между соседними бакетами.
        ServerMetrics.LlmDurationBoundaries
            .Count(x => x is >= 5_000 and <= 60_000)
            .Should().BeGreaterThanOrEqualTo(3, "в диапазоне 5–60 с нужно несколько бакетов");
    }

    [Fact]
    public void Counters_HaveUnitAnnotation()
    {
        // Конвенция OTel: у счётчиков событий unit вида {error}/{call} — помечает,
        // ЧТО считаем. Без него в UI не отличить счётчик от измеряемой величины.
        ServerMetrics.LlmErrors.Unit.Should().Be("{error}");
        ServerMetrics.McpCalls.Unit.Should().Be("{call}");
        ServerMetrics.McpErrors.Unit.Should().Be("{error}");
        ServerMetrics.DifySyncErrors.Unit.Should().Be("{error}");
        ServerMetrics.TelemetryHeartbeat.Unit.Should().Be("{tick}");
        ServerMetrics.LlmRateLimitHits.Unit.Should().Be("{hit}");
    }

    [Fact]
    public void LlmDuration_KeepsMillisecondUnit()
    {
        // Дашборд рисует ось в ms и границы заданы в ms — смена единицы без правки
        // обоих мест сделает график врущим в 1000 раз.
        ServerMetrics.LlmDuration.Unit.Should().Be("ms");
    }
}
