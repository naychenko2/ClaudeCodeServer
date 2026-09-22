using System.Text;
using ClaudeHomeServer.Services.Docs;
using ClaudeHomeServer.Services.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>doc-extract</c> на живой модели (Батарея II, ось B).
///
/// Единственное место оси B с формальным контрактом: ответ — JSON с четырьмя массивами
/// строк. Но мерится тут всё равно не контракт, а факты: продуктовый разбор
/// (<c>ParseExtract</c>) переживает почти любой мусор молча, отдавая пустые списки, и
/// «валидность» такого места ничего не сказала бы.
///
/// Recall считается по ВЫХОДУ места, а не по сырому JSON: выход — четыре списка, которые
/// человек видит в интерфейсе, и потерянное разбором поле должно считаться потерей факта,
/// а не успехом модели. Поэтому выход склеивается в текст четырьмя разделами.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class DocExtractLocalBenchTests(ITestOutputHelper output)
{
    private const string Owner = "bench-user";

    [Fact]
    public async Task Замер_структурной_выжимки_документа()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.DocExtract);
        var service = BuildService(runner);

        var report = await LocalBenchFactsLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var extract = await service.ExtractAsync(Owner, c.Input, CancellationToken.None);
                return Render(extract);
            },
            output);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    // Выход места текстом: ровно те четыре списка, что доезжают до интерфейса.
    internal static string Render(DocumentAiService.DocExtractResult? extract)
    {
        if (extract is null) return "";
        var sb = new StringBuilder();
        Append("Решения", extract.Decisions);
        Append("Даты", extract.Dates);
        Append("Участники", extract.People);
        Append("Шаги", extract.ActionItems);
        return sb.ToString().TrimEnd();

        void Append(string title, IReadOnlyList<string> items)
        {
            sb.AppendLine($"{title}:");
            if (items.Count == 0) sb.AppendLine("- (пусто)");
            else foreach (var i in items) sb.AppendLine($"- {i}");
        }
    }

    private static DocumentAiService BuildService(BenchRunner runner)
    {
        var config = new ConfigurationBuilder().Build();
        var markitdown = new MarkitdownService(config, NullLogger<MarkitdownService>.Instance);
        return new DocumentAiService(markitdown, runner, config);
    }
}
