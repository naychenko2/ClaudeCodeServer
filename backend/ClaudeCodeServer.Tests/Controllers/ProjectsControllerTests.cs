using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCodeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeCodeServer.Tests.Controllers;

public class ProjectsControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly string _tempProjectDir;

    public ProjectsControllerTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _tempProjectDir = Path.Combine(factory.TempDir, "projects");
        Directory.CreateDirectory(_tempProjectDir);
    }

    private string MkProjectDir(string name)
    {
        var dir = Path.Combine(_tempProjectDir, name + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<JsonElement> CreateProjectAsync(string name, string? dir = null)
    {
        var path = dir ?? MkProjectDir(name);
        var response = await _client.PostAsJsonAsync("/api/projects", new { name, rootPath = path });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public async Task GetAll_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/projects");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_ValidRequest_Returns201WithProject()
    {
        var dir = MkProjectDir("new");
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "TestProject",
            rootPath = dir
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("TestProject");
        body.GetProperty("id").GetString().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Create_NonExistentDir_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = "Bad",
            rootPath = @"C:\nonexistent\path_" + Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetById_ExistingProject_Returns200()
    {
        var project = await CreateProjectAsync("GetByIdTest");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.GetAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("id").GetString().Should().Be(id);
    }

    [Fact]
    public async Task GetById_NonExistentProject_Returns404()
    {
        var response = await _client.GetAsync("/api/projects/nonexistent-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ExistingProject_Returns200WithUpdatedName()
    {
        var project = await CreateProjectAsync("Original");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = "Updated",
            rootPath = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("name").GetString().Should().Be("Updated");
    }

    [Fact]
    public async Task Update_NonExistentProject_Returns404()
    {
        var response = await _client.PutAsJsonAsync("/api/projects/nope", new
        {
            name = "X",
            rootPath = (string?)null
        });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_NonExistentNewPath_Returns400()
    {
        var project = await CreateProjectAsync("ToUpdate");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.PutAsJsonAsync($"/api/projects/{id}", new
        {
            name = (string?)null,
            rootPath = @"C:\fake_nonexistent_" + Guid.NewGuid()
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ExistingProject_Returns204()
    {
        var project = await CreateProjectAsync("ToDelete");
        var id = project.GetProperty("id").GetString()!;

        var response = await _client.DeleteAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_NonExistentProject_Returns404()
    {
        var response = await _client.DeleteAsync("/api/projects/ghost-id");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_ThenGetById_Returns404()
    {
        var project = await CreateProjectAsync("DeleteThenGet");
        var id = project.GetProperty("id").GetString()!;
        await _client.DeleteAsync($"/api/projects/{id}");

        var response = await _client.GetAsync($"/api/projects/{id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
