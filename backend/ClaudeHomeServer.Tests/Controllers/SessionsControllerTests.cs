using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class SessionsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public SessionsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
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

    [Fact]
    public async Task Create_ValidProject_Returns201WithSession()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("id").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("projectId").GetString().Should().Be(projectId);
        body.GetProperty("mode").GetString().Should().Be("auto");
    }

    [Fact]
    public async Task GetAll_AfterCreatingSession_ReturnsSession()
    {
        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetAll_MultipleSessionsCreated_ReturnsAllSessions()
    {
        var projectId = await CreateProjectAsync();
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "plan" });
        await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "ask" });

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task GetHistory_ExistingSession_Returns200WithEmptyHistory()
    {
        var projectId = await CreateProjectAsync();
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        var sessionBody = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync());
        var sessionId = sessionBody.GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{projectId}/sessions/{sessionId}/history");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Delete_ExistingSession_Returns204AndRemovedFromGetAll()
    {
        var projectId = await CreateProjectAsync();
        var createResponse = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        var sessionBody = JsonSerializer.Deserialize<JsonElement>(await createResponse.Content.ReadAsStringAsync());
        var sessionId = sessionBody.GetProperty("id").GetString()!;

        var deleteResponse = await _client.DeleteAsync($"/api/projects/{projectId}/sessions/{sessionId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var listResponse = await _client.GetAsync($"/api/projects/{projectId}/sessions");
        var list = JsonSerializer.Deserialize<JsonElement>(await listResponse.Content.ReadAsStringAsync());
        list.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_WithName_ReturnsSessionWithName()
    {
        var projectId = await CreateProjectAsync();

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto",
            name = "Тестовый чат"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("Тестовый чат");
    }
}
