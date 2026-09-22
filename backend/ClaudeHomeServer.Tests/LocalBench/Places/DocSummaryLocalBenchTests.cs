using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>doc-summary</c> на живой модели (Батарея II, ось B).
///
/// Берётся НАСТОЯЩИЙ сервис места (<see cref="DocumentAiService"/>): промпт, бюджет
/// символов и разбор ответа — продуктовые. Подменяется только
/// <see cref="ICheapTextRunner"/> — исполнителем прогона.
///
/// Конвертер документов (<c>markitdown</c>) в маршруте не участвует: место принимает
/// ГОТОВЫЙ текст (<c>SummaryAsync(ownerId, text, ct)</c>), добыча текста из pdf/docx — дело
/// вызывающего. Поэтому вход кейса подаётся текстом напрямую, и внешний процесс на замер
/// не влияет.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов recall тест не утверждает; утверждается ровно одно — прогон
/// дошёл до конца по всем кейсам банка.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class DocSummaryLocalBenchTests(ITestOutputHelper output)
{
    private const string Owner = "bench-user";

    [Fact]
    public async Task Замер_краткого_содержания_документа()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.DocSummary);
        var service = BuildService(runner);

        var report = await LocalBenchFactsLoop.RunAsync(bank, runner,
            invoke: c => service.SummaryAsync(Owner, c.Input, CancellationToken.None),
            output);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    private static DocumentAiService BuildService(BenchRunner runner)
    {
        var config = new ConfigurationBuilder().Build();
        var markitdown = new MarkitdownService(config, NullLogger<MarkitdownService>.Instance);
        return new DocumentAiService(markitdown, runner, config);
    }
}
