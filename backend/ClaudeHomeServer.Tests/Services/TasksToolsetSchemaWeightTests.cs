using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Вес описаний tasks_* в стартовом запросе (задача 85fbaee8): до ужатия — 22 824 символа
// (5 617 токенов Strata count_tokens), после — ≈12 400 (≈3 570). Потолок с запасом на рост — 13 500.
// Плюс правила, на которые опираются роли конвейера: ужатие не должно их съесть.
// Мутации: вернуть любое длинное описание (+1 200 символов) — тест веса краснеет; убрать из
// ReviewOfSchema слово «rework» — тест правил краснеет.
public class TasksToolsetSchemaWeightTests
{
    private const int WeightCeilingChars = 13_500;

    private static readonly JsonSerializerOptions Raw = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public void ВесСхем_ВПределах()
    {
        foreach (var (label, tools) in new[] { ("project", TasksToolset.ProjectChatTools), ("personal", TasksToolset.PersonalTools) })
        {
            var chars = tools.Sum(t => t.Name.Length + t.Description.Length + t.InputSchema.ToJsonString(Raw).Length);
            chars.Should().BeLessThanOrEqualTo(WeightCeilingChars,
                $"{label}: схемы tasks_* — главный пожиратель контекста стартового запроса");
        }
    }

    private static string DescriptionOf(string tool, string prop)
    {
        var schema = TasksToolset.ProjectChatTools.Single(t => t.Name == tool).InputSchema;
        return ((JsonObject)((JsonObject)schema["properties"]!)[prop]!)["description"]!.GetValue<string>();
    }

    [Fact]
    public void ПравилаРолей_ОстаютсяВОписаниях()
    {
        DescriptionOf("tasks_create", "reviewOf").Should().Contain("rework").And.Contain("review/qa/final");
        DescriptionOf("tasks_create", "planSteps").Should().Contain("reviewOf").And.Contain("400");
        DescriptionOf("tasks_update", "planSteps").Should().Contain("reviewOf");
        DescriptionOf("tasks_update", "evidence").Should().Contain("закрытие требует");
        DescriptionOf("tasks_complete", "evidence").Should().Contain("закрытие требует");
        DescriptionOf("tasks_update", "verification").Should().Contain("withdrawn").And.Contain("returnReason");
        DescriptionOf("tasks_update", "featureNote").Should().Contain("в каждом ходе");
        DescriptionOf("tasks_update", "featureEstimate").Should().Contain("в каждом ходе");
        DescriptionOf("tasks_update", "personaId").Should().Contain("снять");
        DescriptionOf("tasks_create", "acceptance").Should().Contain("исполнитель их не меняет");
        TasksToolset.ProjectChatTools.Single(t => t.Name == "tasks_complete").Description
            .Should().Contain("pregate").And.Contain("tasks_run_executor");
    }
}
