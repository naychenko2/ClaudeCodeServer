using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Д-2: гейт EnsureNotClosedAtCreate при создании дефекта. Дефект нельзя создать
// «сразу в Done» — и в статусе, и через попадание в колонку категории Done.
// Сейчас метод смотрит только Status; columnId в Done-колонку проходит как Todo
// (а на доске визуально окажется в Done — обход гейта). После правки должен бросать
// 400 «нельзя создавать сразу в Done».
[Trait("Category", "Integration")]
public class DefectCreateBoardGateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public DefectCreateBoardGateTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "defect_board_gate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/projects",
            new { name = "DefectBoardGate", rootPath = _projectDir, createDirectory = true });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task SetBoardAsync(string projectId, object columns)
    {
        var resp = await _client.PutAsJsonAsync($"/api/projects/{projectId}/board-columns", columns);
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Create_ДефектВDoneКолонкуНоНеВDone_400()
    {
        // Кастомная доска с одной колонкой категории Done. Создаём дефект в Todo
        // с явным columnId этой колонки — обход через доску не должен проходить:
        // гейт EnsureNotClosedAtCreate обязан учесть категорию колонки.
        var projectId = await CreateProjectAsync();
        await SetBoardAsync(projectId, new
        {
            columns = new[]
            {
                new { id = "done-col", name = "Done", category = "done" },
            },
        });

        var resp = await _client.PostAsJsonAsync("/api/tasks", new
        {
            title = "Дефект в Done-колонке",
            projectId,
            kind = "defect",
            status = "todo",
            columnId = "done-col",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Contain("нельзя создавать сразу в Done");
    }
}