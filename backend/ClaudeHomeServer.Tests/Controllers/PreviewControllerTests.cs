using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

public class PreviewControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly string _tempDir;

    public PreviewControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _owner = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "prev_tests");
        Directory.CreateDirectory(_tempDir);
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = Path.Combine(_tempDir, "prev_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = "PrevProject",
            rootPath = dir
        });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Status_NoServer_ReturnsStopped()
    {
        var projectId = await CreateProjectAsync();
        var response = await _owner.GetAsync($"/api/projects/{projectId}/preview/status");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("status").GetString().Should().Be("stopped");
    }

    [Fact]
    public async Task Stop_NoServer_ReturnsOk()
    {
        var projectId = await CreateProjectAsync();
        var response = await _owner.PostAsync($"/api/projects/{projectId}/preview/stop", null);
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Start_WithoutCommand_ReturnsBadRequest()
    {
        var projectId = await CreateProjectAsync();
        var response = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/preview/start", new { });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_AsNonOwner_ReturnsForbid()
    {
        var projectId = await CreateProjectAsync();
        var other = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var response = await other.PostAsJsonAsync($"/api/projects/{projectId}/preview/start", new
        {
            command = "echo",
            args = new[] { "hello" }
        });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }
}
