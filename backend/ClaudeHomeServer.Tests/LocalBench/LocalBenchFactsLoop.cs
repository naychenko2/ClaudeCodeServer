using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Цикл прогона места оси B: прогрев, строгая последовательность, сверка с разметкой,
/// файл пар для судьи.
///
/// Отдельный цикл, а не параметр <see cref="LocalBenchLoop"/>, потому что у осей разная
/// форма итога: у контракта строка кейса — «валиден / совпал с эталоном», у фактов —
/// «сколько фактов доехало и что стало с отказом». Свести их в одну строку значило бы
/// печатать половину колонок пустыми в обоих прогонах. Общее у циклов — правила замера, и
/// они НЕ дублируются: прогрев устроен одинаково по одной причине (первый вызов холоден
/// по префикс-кэшу), а проверка перекрытий зовёт ту же <see cref="LocalBenchLoop.FindOverlap"/>.
/// </summary>
public static class LocalBenchFactsLoop
{
    /// <param name="invoke">
    /// Прогнать кейс через НАСТОЯЩИЙ сервис места. Возвращает выжимку — то, что место
    /// отдало на выходе: recall считается по выходу, а не по сырому ответу модели, потому
    /// что человек видит именно выход, и продуктовый разбор — часть места.
    /// </param>
    public static async Task<LocalBenchFactsReport> RunAsync(
        LocalBenchCaseBank bank,
        BenchRunner runner,
        Func<LocalBenchCase, Task<string?>> invoke,
        ITestOutputHelper output)
    {
        // Разметка читается ДО первого вызова модели: банк с незаземлённой разметкой мерил
        // бы фантазию разметчика, и узнать об этом после часового прогона — значит выкинуть
        // прогон целиком.
        var sheets = bank.Cases.ToDictionary(c => c.Id, LocalBenchFactSheet.Of);
        foreach (var c in bank.Cases)
        {
            var ungrounded = sheets[c.Id].Ungrounded(c.Input);
            Assert.True(ungrounded.Count == 0,
                $"кейс «{c.Id}»: во входе нет якорей фактов "
                + string.Join(", ", ungrounded.Select(f => f.Id))
                + " — размечен не факт входа, а домысел");
        }
        var refusals = bank.Cases.Sum(c => sheets[c.Id].Refusals.Count);
        output.WriteLine($"Разметка: {bank.Cases.Sum(c => sheets[c.Id].Facts.Count)} фактов "
                         + $"на {bank.Cases.Count} кейсов, из них отказов {refusals}; "
                         + "все якоря найдены во входах");

        // Прогрев весов и прогрев места: первый проход по кейсу в статистику НЕ идёт.
        await runner.WarmUpAsync();
        runner.ResetShots();
        await invoke(bank.Cases[0]);
        var warmup = runner.LastShot;
        output.WriteLine(warmup is null
            ? "Прогрев места: сервис в модель не сходил"
            : $"Прогрев места (в статистику не идёт): {warmup.DurationMs} мс, "
              + $"{warmup.CompletionTokens} ток, причина остановки {warmup.FinishReason ?? "-"}");

        var report = new LocalBenchFactsReport(bank.Place, runner.Describe);
        var dump = new LocalBenchJudgeDump(bank.Place, runner.Describe);

        // Строго последовательно: правило 1 спецификации — не больше одного активного
        // обращения к локальной модели, иначе метрика времени мерит очередь в движке.
        foreach (var c in bank.Cases)
        {
            runner.ResetShots();
            var summary = await invoke(c);

            var turns = new LocalBenchTurns(runner.Shots);
            Assert.True(turns.Any, // место обязано сходить в модель — иначе замер ни о чём
                $"кейс «{c.Id}»: место не сходило в модель ни разу");

            var outcomes = sheets[c.Id].Judge(summary);
            report.Add(LocalBenchFactsRow.From(c.Id, outcomes, turns, summary));
            dump.Add(c.Id, c.Input, summary, outcomes);
        }

        var overlap = LocalBenchLoop.FindOverlap(runner.AllShots);
        Assert.True(overlap is null,
            $"вызовы шли параллельно — интервалы перекрылись: {overlap}");
        output.WriteLine($"Последовательность подтверждена: {runner.AllShots.Count} вызовов "
                         + "(с прогревочными), перекрытий нет");
        output.WriteLine($"Пары для судьи: {dump.Write()}");

        return report;
    }
}
