using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Замер места <c>task-classify</c> на живой модели (Батарея II, ось A).
///
/// Берётся НАСТОЯЩИЙ сервис места (<see cref="TaskAiService"/>), подменяется ТОЛЬКО
/// <see cref="ICheapTextRunner"/> — исполнителем прогона (локаль либо облако).
/// Промпт продуктовый: его строит сам сервис.
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class TaskClassifyLocalBenchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Замер_приоритета_и_меток_задачи()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.TaskClassify);
        var labels = SharedLabels(bank);
        var service = new TaskAiService(
            new Mock<IProjectManager>().Object,
            new ConfigurationBuilder().Build(),
            runner);

        output.WriteLine($"Словарь меток владельца: {labels.Count}");

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var classified = await service.ClassifyAsync(ownerId: null, c.Input,
                    Text(c.Context, "description"), labels, projectId: null, CancellationToken.None);
                // Рядом с ответом печатается эталон — приоритет, который у задачи стоит
                // в трекере на самом деле. По этой паре человек и судит разумность
                // выбора: оракул контракта в неё не смотрит.
                var expected = Text(c.Expect, "priority") ?? "-";
                return $"→ {classified.Priority ?? "нет"} [{string.Join(", ", classified.Labels)}]"
                       + $"; в трекере {expected}";
            },
            judge: (_, turns) => TaskClassifyOracle.Violation(turns.Last.RawAnswer),
            output);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    private static IReadOnlyList<string> SharedLabels(LocalBenchCaseBank bank)
    {
        if (bank.Shared?.TryGetProperty("labels", out var el) != true
            || el.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"В банке «{bank.Place}» нет shared.labels — словаря меток владельца");
        return el.EnumerateArray().Select(x => x.GetString() ?? "")
            .Where(x => x.Length > 0).ToList();
    }

    internal static string? Text(JsonElement? source, string property) =>
        source?.TryGetProperty(property, out var el) == true && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
