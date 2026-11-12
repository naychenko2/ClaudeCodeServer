using ClaudeHomeServer.Services.Mcp.Http;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// G2: stageId принимает только создание задачи (слепок этапа неизменен, И3/И4).
// Мутация: добавить ["stageId"] в схему tasks_update — тест краснеет.
public class TasksToolsetStageIdSchemaTests
{
    private static bool HasStageId(string tool) =>
        TasksToolset.ProjectChatTools.Single(t => t.Name == tool)
            .InputSchema["properties"]!.AsObject().ContainsKey("stageId");

    [Fact]
    public void StageId_ЕстьВTasksCreate_НетВTasksUpdate()
    {
        HasStageId("tasks_create").Should().BeTrue();
        HasStageId("tasks_update").Should().BeFalse("слепок этапа неизменен: в update stageId не принимается");
    }
}
