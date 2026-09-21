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
/// Замер места <c>task-dedup</c> на живой модели (Батарея II, ось A).
///
/// Кандидаты — НАСТОЯЩИЕ задачи трекера с их настоящими id: место должно вернуть id из
/// списка либо null, и подставные «task-1/task-2» сделали бы задачу легче настоящей
/// (короткий выдуманный id модель повторяет охотнее, чем guid).
///
/// ЗАМЕР, А НЕ ГЕЙТ: порогов валидности тест не утверждает.
/// </summary>
[Trait("Category", "LocalBench")]
[Collection(TestCollections.LocalBench)]
public class TaskDedupLocalBenchTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Замер_поиска_дублей_задач()
    {
        var runner = await BenchExecutor.CreateAsync(output);
        if (runner is null) return;

        var bank = LocalBenchCases.Load(LocalActionCatalog.TaskDedup);
        var service = new TaskAiService(
            new Mock<IProjectManager>().Object,
            new ConfigurationBuilder().Build(),
            runner);

        var report = await LocalBenchLoop.RunAsync(bank, runner,
            invoke: async c =>
            {
                var candidates = Candidates(c);
                var found = await service.FindDuplicateAsync(ownerId: null, c.Input,
                    TaskClassifyLocalBenchTests.Text(c.Context, "description"),
                    candidates, CancellationToken.None);
                // Рядом с ответом — эталон банка: есть ли у этой задачи настоящий дубль.
                var expected = TaskClassifyLocalBenchTests.Text(c.Expect, "duplicateId");
                var title = found.Id is null
                    ? "дубля нет"
                    : Title(candidates, found.Id);
                var verdict = string.Equals(found.Id, expected, StringComparison.Ordinal)
                    ? "совпало с эталоном"
                    : expected is null ? "эталон: дубля нет" : "эталон: " + Title(candidates, expected);
                return $"→ {title}; {verdict}";
            },
            judge: (c, turns) => TaskDedupOracle.Violation(turns.Last.RawAnswer,
                Candidates(c).Select(x => x.Id).ToHashSet(StringComparer.Ordinal)),
            output,
            reference: PlaceReferences.TaskDedup);

        report.WriteTo(output);
        Assert.Equal(bank.Cases.Count, report.Total);
    }

    private static string Title(IReadOnlyList<(string Id, string Title)> candidates, string id)
    {
        var title = candidates.FirstOrDefault(x => x.Id == id).Title ?? id;
        return title.Length <= 34 ? title : title[..33] + "…";
    }

    private static IReadOnlyList<(string Id, string Title)> Candidates(LocalBenchCase c)
    {
        if (c.Context?.TryGetProperty("candidates", out var el) != true
            || el.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"В кейсе «{c.Id}» нет context.candidates — списка существующих задач");
        return el.EnumerateArray()
            .Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("title").GetString()!))
            .ToList();
    }
}
