using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.Services.TestRuns;

/// <summary>
/// Хронология этапов прогона для строки этапов на карточке инструмента
/// («✓ сборка 1:42 · тесты 2:13»). Этап открывается первым событием прогресса со своим Stage и
/// закрывается следующим этапом либо концом прогона; время — часы сервера (Unix-мс), чтобы
/// длительности после F5 считались так же, как вживую.
/// Колбэк прогресса зовут потоки обоих потоков вывода — всё под замком.
/// </summary>
public sealed class TestRunStages(Func<long> clock)
{
    private readonly object _gate = new();
    private readonly List<ToolStage> _stages = [];

    // Короткая подпись этапа: в строке этапов места мало, «подсчёт тестов» и «запуск стенда»
    // живут в подписи прогресса, а здесь хватает одного слова
    public static string LabelOf(string stage) => stage switch
    {
        "queued" => "очередь",
        "build" => "сборка",
        "stand" => "стенд",
        "running" => "тесты",
        // Подъём дев-стенда (start_stand): запуск процесса и ожидание порта, затем «готов»
        "start" => "запуск",
        "ready" => "готов",
        _ => stage,
    };

    // Подсчёт тестов (--list-tests, у vitest — файлов) отдельным этапом на карточке непонятен:
    // он входит в «тесты», и их время идёт с начала подсчёта. Подпись прогресса при этом своя
    // («подсчёт тестов»), а процент появляется, только когда подсчёт кончился
    private static string StageKeyOf(string stage) => stage == "list" ? "running" : stage;

    // Новый этап — закрыть прежний и открыть следующий. false — этап тот же (шаг счётчика)
    public bool Advance(string stage)
    {
        stage = StageKeyOf(stage);
        lock (_gate)
        {
            if (_stages.Count > 0 && _stages[^1].Stage == stage && _stages[^1].EndedAt is null) return false;
            var now = clock();
            CloseLast(now, failed: false);
            _stages.Add(new ToolStage(stage, LabelOf(stage), now));
            return true;
        }
    }

    public IReadOnlyList<ToolStage> Snapshot()
    {
        lock (_gate) return [.. _stages];
    }

    // Конец прогона: последний этап закрывается; failed — прогон кончился на нём неудачей
    // (упала сборка, «Стоп», потолок) — карточка рисует его крестиком
    public IReadOnlyList<ToolStage> Finish(bool failed)
    {
        lock (_gate)
        {
            CloseLast(clock(), failed);
            return [.. _stages];
        }
    }

    private void CloseLast(long now, bool failed)
    {
        if (_stages.Count == 0 || _stages[^1].EndedAt is not null) return;
        _stages[^1] = _stages[^1] with { EndedAt = now, Failed = failed ? true : null };
    }

    // Кончился ли прогон неудачей на своём последнем этапе. Упавшие тесты — не неудача этапа:
    // их покажут счётчики итога
    public static bool EndedBadly(TestRunResult r) =>
        r.Cancelled || r.TimedOut || (r.Phase == TestRunPhase.Build && r.ExitCode is not 0);

    // Итоговые счётчики для закрытой карточки: из отчётов (TRX, JSON), а без них — по строкам
    // консоли, если прогон дошёл до тестов. vitest считает в подсчёте ФАЙЛЫ, поэтому общее
    // число тестов у него без отчёта — сколько успело пройти. null — тестов не было (отказ,
    // очередь, упала сборка)
    public static ToolRunTotals? Totals(TestRunResult r)
    {
        if (r.Refusal is not null || r.NeverStarted) return null;
        if (r.Reports.Count > 0)
            return new ToolRunTotals(r.Reports.Sum(x => x.Passed), r.Reports.Sum(x => x.Failed),
                r.Reports.Sum(x => x.Total));
        if (r.Phase != TestRunPhase.Test || r.Counts.Done == 0) return null;
        var total = r.Kind != TestRunKind.Vitest && r.Total is { } listed ? Math.Max(listed, r.Counts.Done) : r.Counts.Done;
        return new ToolRunTotals(r.Counts.Passed, r.Counts.Failed, total);
    }
}
