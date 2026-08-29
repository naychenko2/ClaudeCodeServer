using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Filters;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// GET /api/mcp/session-context — источник состава для MCP-тула context_list (A4).
/// Сессия берётся из заголовка X-Caller-Session-Id (его ставит общий api() MCP-сервера),
/// а не из параметра: иначе модель спросила бы состав чужого чата.
/// </summary>
public class McpSessionContextTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly TestWebApplicationFactory _factory;
    private readonly string _tempDir;

    public McpSessionContextTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "mcp_ctx_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<(string ProjectId, string SessionId, string ProjectDir)> CreateSessionAsync(
        HttpClient? client = null)
    {
        client ??= _client;
        var dir = Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "CtxProject",
            rootPath = dir
        });
        projectResponse.EnsureSuccessStatusCode();
        var project = JsonSerializer.Deserialize<JsonElement>(await projectResponse.Content.ReadAsStringAsync());
        var projectId = project.GetProperty("id").GetString()!;

        var sessionResponse = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new
        {
            mode = "auto"
        });
        sessionResponse.EnsureSuccessStatusCode();
        var session = JsonSerializer.Deserialize<JsonElement>(await sessionResponse.Content.ReadAsStringAsync());
        return (projectId, session.GetProperty("id").GetString()!, dir);
    }

    private static async Task<HttpResponseMessage> GetContextAsync(HttpClient client, string? callerSessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/mcp/session-context");
        if (callerSessionId is not null)
            request.Headers.Add(DenyOnDelegatedTurnAttribute.CallerHeader, callerSessionId);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task СвояСессия_ОтдаётПроектИСостав()
    {
        var (projectId, sessionId, projectDir) = await CreateSessionAsync();
        var filePath = Path.Combine(projectDir, "docs", "readme.md");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        await File.WriteAllTextAsync(filePath, "# readme");
        var put = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}/context", new[]
        {
            new { type = "file", id = "docs/readme.md", title = "README" },
        });
        put.EnsureSuccessStatusCode();

        var response = await GetContextAsync(_client, sessionId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("projectId").GetString().Should().Be(projectId,
            "тул подставляет projectId сессии в files_read/tasks_get");
        var entries = body.GetProperty("entries");
        entries.GetArrayLength().Should().Be(1);
        entries[0].GetProperty("id").GetString().Should().Be("docs/readme.md");
        entries[0].GetProperty("title").GetString().Should().Be("README");
        entries[0].GetProperty("missing").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task БитаяЗапись_ПриходитСMissing()
    {
        // Признак «не найден» считает сервер — одна точка с полосой вкладок; тул подменяет
        // такую запись строкой-предупреждением, а не отдаёт модели битый адрес
        var (projectId, sessionId, _) = await CreateSessionAsync();
        var put = await _client.PutAsJsonAsync($"/api/projects/{projectId}/sessions/{sessionId}/context", new[]
        {
            new { type = "file", id = "docs/gone.md" },
        });
        put.EnsureSuccessStatusCode();

        var response = await GetContextAsync(_client, sessionId);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("entries")[0].GetProperty("missing").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ЧужаяСессия_Returns403()
    {
        // Сессия второго пользователя, запрос — токеном первого: пустой список был бы
        // неотличим от «в контексте ничего нет», поэтому именно отказ
        var otherClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var (_, otherSessionId, _) = await CreateSessionAsync(otherClient);

        var response = await GetContextAsync(_client, otherSessionId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task БезЗаголовкаСессии_Returns400()
    {
        var response = await GetContextAsync(_client, callerSessionId: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task НесуществующаяСессия_Returns404()
    {
        var response = await GetContextAsync(_client, "nonexistent");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
