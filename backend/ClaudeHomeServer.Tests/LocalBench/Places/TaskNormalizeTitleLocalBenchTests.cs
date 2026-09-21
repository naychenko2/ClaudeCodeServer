using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>task-normalize-title</c> на живой локальной модели (тест 10.6 Батареи II,
/// ось A — контракт).
///
/// Берётся НАСТОЯЩИЙ сервис места (<see cref="TaskAiService"/>), подменяется ТОЛЬКО
/// <see cref="ICheapTextRunner"/> — на <see cref="LiveLocalRunner"/>, который ходит в
/// модель по-настоящему. Промпт при этом продуктовый: его строит сам сервис, копии в тесте
/// нет, и правка промпта в продукте меняет замер, а не расходится с ним.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает. Решение «переводить ли место
/// на локаль» принимается по вердикту батареи, а не падением сборки; числа печатаются в
/// вывод и переносятся в документ замеров руками. Утверждается ровно одно — прогон дошёл
/// до конца по всем кейсам банка.
///
/// Коллекция <see cref="TestCollections.LocalBench"/> держит замер в одиночестве: модель
/// одна, и параллельный сосед портит время. Прогон — ТОЛЬКО отдельный:
/// dotnet test --filter "Category=LocalBench"
/// Стенд не поднят (CI, чужая машина) — замер выходит без падения.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class TaskNormalizeTitleLocalBenchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Замер_нормализации_заголовка_задачи()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.TaskNormalizeTitle);
        var service = new TaskAiService(
            new Mock<IProjectManager>().Object,
            new ConfigurationBuilder().Build(),
            runner);

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var normalized = await service.NormalizeTitleAsync(
                    ownerId: null, c.Input, CancellationToken.None);
                // Что место выдало на выходе: при невалидном ответе модели продукт молча
                // возвращает исходный заголовок — по этой колонке видно, как деградация
                // выглядит для человека.
                return $"→ «{normalized.Title}»"
                       + (normalized.DueHint is null ? "" : $" [{normalized.DueHint}]");
            },
            judge: (_, turns) => TaskNormalizeTitleOracle.Violation(turns.Last.RawAnswer),
            output,
            reference: PlaceReferences.TaskNormalizeTitle);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }
}
