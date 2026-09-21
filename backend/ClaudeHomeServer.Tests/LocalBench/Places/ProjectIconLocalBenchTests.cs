using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.ProjectIcons;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>project-icon</c> на живой модели (Батарея II, ось A).
///
/// Место двухходовое, и это первое место банка, где кейс стоит МОДЕЛИ нескольких ходов:
/// в таблице у него колонка «ход» (2 — слова и выбор, 3 — сработал повтор), а время
/// кейса — сумма ходов, потому что именно её ждёт человек.
///
/// Берётся НАСТОЯЩИЙ <see cref="ProjectIconGlyphService"/>: меню имён собирает он сам, и
/// подменять эту сборку нельзя — на ней держится весь смысл схемы.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class ProjectIconLocalBenchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Замер_подбора_значка_проекта()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.ProjectIcon);
        var service = new ProjectIconGlyphService(runner,
            NullLogger<ProjectIconGlyphService>.Instance);

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var hint = TaskClassifyLocalBenchTests.Text(c.Context, "hint");
                var result = await service.SuggestAsync(c.Input, hint, ownerId: "bench");
                // Что место выдало на выходе — с оглядкой на меру меню: ответ бывает
                // валиден по белому списку и всё же отброшен продуктом как выбор мимо
                // меню. Рядом — значок, который у проекта стоит на самом деле.
                var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "glyph") ?? "-";
                var shown = result.Ok
                    ? string.Join(", ", result.Candidates.Select(x => x.Name))
                    : "инициалы (" + result.FailReason + ")";
                return $"→ {shown}; у проекта {expected}";
            },
            judge: (_, turns) => ProjectIconOracle.Violation(turns),
            output);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }
}
