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

    private async Task<(string id, string dir)> CreateProjectAsync()
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
        return (json.GetProperty("id").GetString()!, dir);
    }

    [Fact]
    public async Task Status_NoServer_ReturnsEmptyRunning()
    {
        var (projectId, _) = await CreateProjectAsync();
        var response = await _owner.GetAsync($"/api/projects/{projectId}/preview/status");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("running").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Stop_UnknownService_ReturnsOk()
    {
        var (projectId, _) = await CreateProjectAsync();
        var response = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/preview/stop",
            new { serviceId = "does-not-exist" });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Start_WithoutCommand_ReturnsBadRequest()
    {
        var (projectId, _) = await CreateProjectAsync();
        var response = await _owner.PostAsJsonAsync($"/api/projects/{projectId}/preview/start", new { });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_AsNonOwner_ReturnsForbid()
    {
        var (projectId, _) = await CreateProjectAsync();
        var other = _factory.CreateAuthenticatedClient(TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var response = await other.PostAsJsonAsync($"/api/projects/{projectId}/preview/start", new
        {
            command = "echo",
            args = new[] { "hello" },
            serviceId = "svc1"
        });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Services_DiscoversNpmScripts()
    {
        var (projectId, dir) = await CreateProjectAsync();
        await File.WriteAllTextAsync(Path.Combine(dir, "package.json"),
            """{ "scripts": { "dev": "vite", "build": "vite build" } }""");

        var response = await _owner.GetAsync($"/api/projects/{projectId}/services");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var services = json.GetProperty("services");

        var names = services.EnumerateArray()
            .Select(s => s.GetProperty("command").GetString() + " " + string.Join(' ',
                s.GetProperty("args").EnumerateArray().Select(a => a.GetString())))
            .ToList();
        // "dev" — серверный скрипт, "build" — нет.
        names.Should().Contain(n => n!.Contains("dev"));
        names.Should().NotContain(n => n!.Contains("build"));
    }

    [Fact]
    public async Task LaunchConfig_WriteThenRead_RoundTrips()
    {
        var (projectId, _) = await CreateProjectAsync();
        var put = await _owner.PutAsJsonAsync($"/api/projects/{projectId}/launch-config", new
        {
            configurations = new[]
            {
                new { name = "web", runtimeExecutable = "npm", runtimeArgs = new[] { "run", "dev" }, port = 3000 }
            }
        });
        put.EnsureSuccessStatusCode();

        var get = await _owner.GetAsync($"/api/projects/{projectId}/launch-config");
        get.EnsureSuccessStatusCode();
        var json = await get.Content.ReadFromJsonAsync<JsonElement>();
        var configs = json.GetProperty("configurations");
        configs.GetArrayLength().Should().Be(1);
        configs[0].GetProperty("name").GetString().Should().Be("web");
        configs[0].GetProperty("port").GetInt32().Should().Be(3000);
    }

    [Fact]
    public async Task Services_IncludesSavedLaunchConfig()
    {
        var (projectId, _) = await CreateProjectAsync();
        await _owner.PutAsJsonAsync($"/api/projects/{projectId}/launch-config", new
        {
            configurations = new[]
            {
                new { name = "api", runtimeExecutable = "dotnet", runtimeArgs = new[] { "run" }, port = 5005 }
            }
        });

        var response = await _owner.GetAsync($"/api/projects/{projectId}/services");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var services = json.GetProperty("services").EnumerateArray().ToList();
        services.Should().Contain(s =>
            s.GetProperty("saved").GetBoolean() &&
            s.GetProperty("name").GetString() == "api");
    }
}
