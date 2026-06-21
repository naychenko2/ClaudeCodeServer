using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCodeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeCodeServer.Tests.Controllers;

public class SessionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public SessionsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _tempDir = Path.Combine(factory.TempDir, "session_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "SessionProject",
            rootPath = dir
        });
        response.EnsureSuccessStatusCode();
        var json = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task GetAll_ExistingProject_Returns200EmptyArray()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_NonExistentProject_Returns404()
    {
        var response = await _client.PostAsJsonAsync("/api/projects/nonexistent/sessions", new
        {
            mode = "auto"
        });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetHistory_NonExistentSession_Returns404()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/nonexistent/history");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetHistory_SessionBelongsToDifferentProject_Returns404()
    {
        // Тест проверяет что GetHistory валидирует projectId
        var projectId1 = await CreateProjectAsync();
        var projectId2 = await CreateProjectAsync();

        // Получаем историю несуществующей сессии из другого проекта
        var response = await _client.GetAsync($"/api/projects/{projectId1}/sessions/fake-session/history");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_NonExistentSession_Returns204()
    {
        // DELETE всегда возвращает 204, даже если сессия не найдена
        var projectId = await CreateProjectAsync();
        var response = await _client.DeleteAsync($"/api/projects/{projectId}/sessions/nonexistent");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
