using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// /api/host-files/content: read-only просмотр абсолютных хостовых путей вне корня проекта.
// Гейт: local-юзер видит весь хост, container-юзер — только монтирования песочницы.
public class HostFilesControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public HostFilesControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _tempDir = Path.Combine(factory.TempDir, "host_files_tests");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task GetContent_RelativePath_Returns400()
    {
        var response = await _client.GetAsync("/api/host-files/content?path=relative.txt");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetContent_LocalUser_ReadsAbsolutePathOutsideAnyProject()
    {
        var file = Path.Combine(_tempDir, "outside.txt");
        File.WriteAllText(file, "hello from host");

        var response = await _client.GetAsync($"/api/host-files/content?path={Uri.EscapeDataString(file)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        body.GetProperty("content").GetString().Should().Be("hello from host");
        body.GetProperty("isBinary").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetContent_NonExistentFile_Returns404()
    {
        var file = Path.Combine(_tempDir, "ghost.txt");

        var response = await _client.GetAsync($"/api/host-files/content?path={Uri.EscapeDataString(file)}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetContent_DirectoryPath_Returns404()
    {
        var response = await _client.GetAsync($"/api/host-files/content?path={Uri.EscapeDataString(_tempDir)}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // container-юзер вне монтирований песочницы (ProjectsRoot/profiles/tmp) → 403.
    // Реального docker не требует: гейт срабатывает до любого файлового доступа.
    [Fact]
    public async Task GetContent_ContainerUserOutsideMounts_Returns403()
    {
        var username = "sbxuser_" + Guid.NewGuid().ToString("N")[..8];
        var create = await _client.PostAsJsonAsync("/api/users", new
        {
            username,
            password = "password12345",
            role = "user",
            executionEnvironment = "container",
        });
        create.EnsureSuccessStatusCode();

        var sbxClient = _factory.CreateClient();
        var login = await sbxClient.PostAsJsonAsync("/api/auth/login", new { username, password = "password12345" });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        sbxClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var file = Path.Combine(_tempDir, "sandboxed.txt");
        File.WriteAllText(file, "secret");

        var response = await sbxClient.GetAsync($"/api/host-files/content?path={Uri.EscapeDataString(file)}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
