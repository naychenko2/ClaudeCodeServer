using System.Text.Json;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Tasks.Tests.Fixtures;

// Загрузчик золотых фикстур FeatureProjector (Fixtures/feature-runs/*.json, задача 1f4e6a620).
// Фикстуры — упрощённая выгрузка: kind — свободный текст ("epic"/"design"/"review"/"build"),
// status — строка. Десериализуем в лёгкий DTO (минуя enum TaskItem.Kind) и мапим в TaskItem
// только те поля, которые FeatureProjector реально читает (role/featureId/stepRef/lane/reviewOf/
// verification.verdict/status).
internal static class FeatureRunFixture
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string FixturesDir =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "feature-runs");

    public static List<TaskItem> Load(string name)
    {
        var path = Path.Combine(FixturesDir, name);
        var raw = File.ReadAllText(path);
        var fixtures = JsonSerializer.Deserialize<List<FixtureTask>>(raw, Options)
            ?? throw new InvalidOperationException($"Фикстура {name} пуста");
        return fixtures.Select(ToTask).ToList();
    }

    private static TaskItem ToTask(FixtureTask f) => new()
    {
        Id = f.Id,
        Title = f.Title,
        Status = f.Status switch
        {
            "done" => TaskItemStatus.Done,
            "inProgress" => TaskItemStatus.InProgress,
            _ => TaskItemStatus.Todo,
        },
        Role = f.Role,
        FeatureId = f.FeatureId,
        StepRef = f.StepRef,
        Lane = f.Lane,
        ReviewOf = f.ReviewOf,
        WorktreeBranch = f.WorktreeBranch,
        Verification = f.Verification is not null
            ? new TaskVerification { Verdict = f.Verification.Verdict }
            : null,
    };

    private sealed record FixtureTask(
        string Id, string Title, string? Persona, string? Status,
        string? Created, string? Completed, string? Kind, string? Role,
        string? FeatureId, string? WorktreeBranch, string? StepRef, string? Lane,
        List<string>? ReviewOf, FixtureVerification? Verification);

    private sealed record FixtureVerification(string? Verdict);
}
