using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Цикл прогона места по банку кейсов: прогрев, строгая последовательность, сбор строк.
///
/// Живёт в харнессе, а не в тесте места, ровно потому, что ошибиться тут легко и молча:
/// забытый прогрев завышает время первого кейса в разы, а параллельный прогон искажает
/// его у всех. Новое место подключается двумя делегатами — «прогнать кейс через
/// настоящий сервис» и «рассудить сырой ответ», — и получает правильный замер даром.
/// </summary>
public static class LocalBenchLoop
{
    /// <param name="invoke">
    /// Прогнать кейс через НАСТОЯЩИЙ сервис места. Возвращает то, что место отдало на
    /// выходе, — краткой строкой для колонки результата (её видит человек в продукте).
    /// </param>
    /// <param name="judge">
    /// Оракул контракта: null — ответ валиден, иначе текст нарушения. Судит сырой ответ
    /// модели, а не выход сервиса: место молча деградирует на исходные данные, и по его
    /// выходу отказ локали неотличим от успеха.
    /// </param>
    public static async Task<LocalBenchReport> RunAsync(
        LocalBenchCaseBank bank,
        LiveLocalRunner runner,
        Func<LocalBenchCase, Task<string?>> invoke,
        Func<LocalBenchCase, LocalBenchShot, string?> judge,
        ITestOutputHelper output)
    {
        // Прогрев весов: холостой вызов, чтобы модель загрузилась в память.
        await runner.WarmUpAsync();

        // Прогрев места: один полный проход через настоящий сервис, результат которого в
        // статистику НЕ идёт. Первый вызов холоден по префикс-кэшу движка, а кэш снимает
        // основную часть работы префилла (замеры 2026-09-06) — без этого прохода первый
        // кейс банка меряет кэш-промах и тянет за собой всю сводку.
        runner.ResetShots();
        await invoke(bank.Cases[0]);
        var warmup = runner.LastShot;
        output.WriteLine(warmup is null
            ? "Прогрев места: сервис в модель не сходил"
            : $"Прогрев места (в статистику не идёт): {warmup.DurationMs} мс, "
              + $"{warmup.CompletionTokens} ток, причина остановки {warmup.FinishReason ?? "-"}");

        var report = new LocalBenchReport(bank.Place);
        // Строго последовательно: правило 1 спецификации Батареи — не больше одной
        // активной задачи на локальную модель, иначе метрика времени недостоверна.
        foreach (var c in bank.Cases)
        {
            runner.ResetShots();
            var outcome = await invoke(c);

            var shot = runner.LastShot;
            Assert.NotNull(shot); // место обязано сходить в модель — иначе замер ни о чём
            var violation = judge(c, shot);
            report.Add(new LocalBenchRow(
                CaseId: c.Id,
                Valid: violation is null,
                Violation: shot.Error is null ? violation : $"{violation} ({shot.Error})",
                Truncated: shot.Truncated,
                Answered: shot.Answered,
                DurationMs: shot.DurationMs,
                CompletionTokens: shot.CompletionTokens,
                Note: violation is null ? outcome : null));
        }

        // Последовательность — не на веру: перекрывшиеся во времени вызовы означают, что
        // замер мерил очередь в движке, а не место. Коллекция xUnit это предотвращает,
        // проверка доказывает.
        var overlap = FindOverlap(runner.AllShots);
        Assert.True(overlap is null,
            $"вызовы шли параллельно — интервалы перекрылись: {overlap}");
        output.WriteLine($"Последовательность подтверждена: {runner.AllShots.Count} вызовов "
                         + "(с прогревочными), перекрытий нет");

        return report;
    }

    // Первое перекрытие соседних вызовов в хронологическом порядке. null — их нет.
    internal static string? FindOverlap(IReadOnlyList<LocalBenchShot> shots)
    {
        var ordered = shots.Where(s => s.FinishedTicks > 0)
            .OrderBy(s => s.StartedTicks).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (current.StartedTicks < previous.FinishedTicks)
                return $"вызов {i} ({current.ActionKey}) начался до конца предыдущего "
                       + $"на {TicksToMs(previous.FinishedTicks - current.StartedTicks)} мс";
        }
        return null;
    }

    private static long TicksToMs(long ticks) => ticks * 1000 / Stopwatch.Frequency;
}
