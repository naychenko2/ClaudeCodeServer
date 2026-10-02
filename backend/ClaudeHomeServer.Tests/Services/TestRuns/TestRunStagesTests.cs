using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Хронология этапов прогона (строка этапов карточки run_tests) и итоговые счётчики для
/// закрытой карточки: «готово · 2:15 · 174 из 177 · упало 3», «прервано · сборка 1:10».
/// </summary>
public class TestRunStagesTests
{
    private long _now;
    private TestRunStages NewStages() => new(() => _now);

    [Fact]
    public void Этапы_СменаЗакрываетПрежний_ШагСчётчикаЭтапНеПлодит()
    {
        var stages = NewStages();
        _now = 1_000;
        stages.Advance("queued").Should().BeTrue();
        _now = 13_000;
        stages.Advance("build").Should().BeTrue();
        _now = 115_000;
        stages.Advance("list").Should().BeTrue();
        _now = 118_000;
        stages.Advance("running").Should().BeTrue();
        _now = 120_000;
        stages.Advance("running").Should().BeFalse("шаг счётчика «412 из 7951» — тот же этап");

        stages.Snapshot().Should().Equal(
            new ToolStage("queued", "очередь", 1_000, 13_000),
            new ToolStage("build", "сборка", 13_000, 115_000),
            new ToolStage("list", "подсчёт", 115_000, 118_000),
            new ToolStage("running", "тесты", 118_000));

        _now = 250_000;
        stages.Finish(failed: false)[^1].Should().Be(new ToolStage("running", "тесты", 118_000, 250_000));
    }

    [Fact]
    public void Обрыв_ПоследнийЭтапНеудачей_ПрошедшиеОстаютсяГалочками()
    {
        var stages = NewStages();
        _now = 1_000;
        stages.Advance("build");
        _now = 71_000;

        stages.Finish(failed: true).Should().Equal(new ToolStage("build", "сборка", 1_000, 71_000, Failed: true));
        stages.Finish(failed: false).Should().ContainSingle()
            .Which.Failed.Should().BeTrue("повторный конец уже закрытый этап не трогает");
    }

    [Theory]
    [InlineData(true, false, TestRunPhase.Test, 0, true)]   // «Стоп»
    [InlineData(false, true, TestRunPhase.Test, 0, true)]   // потолок
    [InlineData(false, false, TestRunPhase.Build, 1, true)] // сборка упала
    [InlineData(false, false, TestRunPhase.Test, 1, false)] // упавшие тесты — не неудача этапа
    public void НеудачаЭтапа(bool cancelled, bool timedOut, TestRunPhase phase, int exit, bool expected) =>
        TestRunStages.EndedBadly(Result() with { Cancelled = cancelled, TimedOut = timedOut, Phase = phase, ExitCodeOverride = exit })
            .Should().Be(expected);

    [Fact]
    public void Итог_ИзОтчётов_СуммаПоСборкам()
    {
        var r = Result() with
        {
            Reports =
            [
                new TestReport("A", 100, 99, 1, 0, null, []),
                new TestReport("B", 77, 75, 2, 0, null, []),
            ],
        };

        TestRunStages.Totals(r).Should().Be(new ToolRunTotals(174, 3, 177));
    }

    [Fact]
    public void Итог_БезОтчёта_ПоКонсоли_ОбщееЧислоИзПодсчёта()
    {
        var r = Result() with { Cancelled = true, Total = 7951, Counts = new TestCounts(410, 2, 0) };

        TestRunStages.Totals(r).Should().Be(new ToolRunTotals(410, 2, 7951));
    }

    [Fact]
    public void Итог_Vitest_БезОтчёта_ПодсчётВФайлах_ОбщееЧислоПоПройденным()
    {
        var r = Result() with { Kind = TestRunKind.Vitest, Total = 102, Counts = new TestCounts(500, 1, 3) };

        TestRunStages.Totals(r).Should().Be(new ToolRunTotals(500, 1, 504));
    }

    [Fact]
    public void Итог_ТестовНеБыло_Null()
    {
        TestRunStages.Totals(Result() with { Phase = TestRunPhase.Build }).Should().BeNull();
        TestRunStages.Totals(TestRunResult.Refused("занято")).Should().BeNull();
        TestRunStages.Totals(Result() with { NeverStarted = true }).Should().BeNull();
        TestRunStages.Totals(Result()).Should().BeNull("ни одного теста не прошло — считать нечего");
    }

    private static TestRunResultBuilder Result() => new();

    // Короткая запись исхода для тестов: код выхода задаётся свойством, остальное — по умолчанию
    private sealed record TestRunResultBuilder
    {
        public bool Cancelled { get; init; }
        public bool TimedOut { get; init; }
        public bool NeverStarted { get; init; }
        public TestRunPhase Phase { get; init; } = TestRunPhase.Test;
        public int ExitCodeOverride { get; init; }
        public TestRunKind Kind { get; init; } = TestRunKind.Dotnet;
        public int? Total { get; init; }
        public TestCounts Counts { get; init; }
        public IReadOnlyList<TestReport> Reports { get; init; } = [];

        public static implicit operator TestRunResult(TestRunResultBuilder b) =>
            new(null, b.ExitCodeOverride, b.Cancelled, b.TimedOut, TimeSpan.Zero, [])
            {
                NeverStarted = b.NeverStarted,
                Phase = b.Phase,
                Kind = b.Kind,
                Total = b.Total,
                Counts = b.Counts,
                Reports = b.Reports,
            };
    }
}
