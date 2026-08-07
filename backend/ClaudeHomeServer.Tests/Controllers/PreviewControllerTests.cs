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

    // ── Сервисы, поднятые вне продукта ────────────────────────────────────────

    /// <summary>Слушающий сокет на свободном порту — эмуляция сервера, запущенного снаружи.</summary>
    private static System.Net.Sockets.TcpListener StartListener(out int port)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        return listener;
    }

    private async Task SaveServiceAsync(string projectId, string name, int? port)
    {
        var response = await _owner.PutAsJsonAsync($"/api/projects/{projectId}/launch-config", new
        {
            configurations = new[]
            {
                new { name, runtimeExecutable = "npm", runtimeArgs = new[] { "run", "dev" }, port }
            }
        });
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Services_PortListening_MarksServiceExternal()
    {
        var (projectId, _) = await CreateProjectAsync();
        using var listener = StartListener(out var port);
        await SaveServiceAsync(projectId, "external-web", port);

        var response = await _owner.GetAsync($"/api/projects/{projectId}/services");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var svc = json.GetProperty("services").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "external-web");

        svc.GetProperty("status").GetString().Should().Be("external");
        svc.GetProperty("runningPort").GetInt32().Should().Be(port);
    }

    [Fact]
    public async Task Services_PortFree_KeepsServiceIdle()
    {
        var (projectId, _) = await CreateProjectAsync();
        // Порт занимаем и сразу освобождаем — так он гарантированно свободен
        var listener = StartListener(out var port);
        listener.Stop();
        await SaveServiceAsync(projectId, "idle-web", port);

        var response = await _owner.GetAsync($"/api/projects/{projectId}/services");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var svc = json.GetProperty("services").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "idle-web");

        svc.GetProperty("status").GetString().Should().Be("idle");
    }

    [Fact]
    public async Task ActiveExternal_ListeningService_ReturnsItsPort()
    {
        var (projectId, _) = await CreateProjectAsync();
        using var listener = StartListener(out var port);
        await SaveServiceAsync(projectId, "external-web", port);

        var services = await (await _owner.GetAsync($"/api/projects/{projectId}/services"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var serviceId = services.GetProperty("services").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "external-web")
            .GetProperty("id").GetString();

        var response = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/preview/active-external", new { serviceId });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("port").GetInt32().Should().Be(port);
    }

    [Fact]
    public async Task ActiveExternal_PortNotListening_ReturnsBadRequest()
    {
        var (projectId, _) = await CreateProjectAsync();
        var listener = StartListener(out var port);
        listener.Stop();
        await SaveServiceAsync(projectId, "dead-web", port);

        var services = await (await _owner.GetAsync($"/api/projects/{projectId}/services"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var serviceId = services.GetProperty("services").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "dead-web")
            .GetProperty("id").GetString();

        var response = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/preview/active-external", new { serviceId });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ActiveExternal_ServiceWithoutPort_ReturnsBadRequest()
    {
        var (projectId, _) = await CreateProjectAsync();
        await SaveServiceAsync(projectId, "no-port", null);

        var services = await (await _owner.GetAsync($"/api/projects/{projectId}/services"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var serviceId = services.GetProperty("services").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "no-port")
            .GetProperty("id").GetString();

        var response = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/preview/active-external", new { serviceId });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Составная конфигурация Rider из одного участника, чей порт слушается снаружи.
    /// Своего порта у группы нет — превью обязано взять его у участника, иначе прокси
    /// отдаёт «Dev-сервер не запущен».
    /// </summary>
    private static async Task WriteExternalGroupAsync(string dir, int port)
    {
        Directory.CreateDirectory(Path.Combine(dir, "src", "App", "Properties"));
        await File.WriteAllTextAsync(Path.Combine(dir, "src", "App", "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await File.WriteAllTextAsync(Path.Combine(dir, "src", "App", "Properties", "launchSettings.json"),
            $$"""{ "profiles": { "http": { "applicationUrl": "http://localhost:{{port}}" } } }""");

        Directory.CreateDirectory(Path.Combine(dir, ".run"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".run", "Backend.run.xml"), """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Backend" type="LaunchSettings" factoryName=".NET Launch Settings Profile">
                <option name="LAUNCH_PROFILE_PROJECT_FILE_PATH" value="$PROJECT_DIR$/src/App/App.csproj" />
                <option name="LAUNCH_PROFILE_NAME" value="http" />
              </configuration>
            </component>
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, ".run", "Compound.run.xml"), """
            <component name="ProjectRunConfigurationManager">
              <configuration default="false" name="Всё сразу" type="com.intellij.execution.configurations.multilaunch" factoryName="MultiLaunchConfiguration">
                <rows>
                  <ExecutableRowSnapshot>
                    <option name="executable">
                      <ExecutableSnapshot>
                        <option name="id" value="runConfig:.NET Launch Settings Profile.Backend" />
                      </ExecutableSnapshot>
                    </option>
                  </ExecutableRowSnapshot>
                </rows>
              </configuration>
            </component>
            """);
    }

    [Fact]
    public async Task Services_GroupOfExternalMembers_IsExternal()
    {
        var (projectId, dir) = await CreateProjectAsync();
        using var listener = StartListener(out var port);
        await WriteExternalGroupAsync(dir, port);

        var json = await (await _owner.GetAsync($"/api/projects/{projectId}/services"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var group = json.GetProperty("services").EnumerateArray()
            .First(s => s.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array);

        // Не «started»: своего процесса у группы нет, и назначать её надо внешним эндпоинтом
        group.GetProperty("status").GetString().Should().Be("external");
    }

    [Fact]
    public async Task ActiveExternal_GroupWithoutOwnPort_TakesMemberPort()
    {
        var (projectId, dir) = await CreateProjectAsync();
        using var listener = StartListener(out var port);
        await WriteExternalGroupAsync(dir, port);

        var services = await (await _owner.GetAsync($"/api/projects/{projectId}/services"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var groupId = services.GetProperty("services").EnumerateArray()
            .First(s => s.TryGetProperty("members", out var m) && m.ValueKind == JsonValueKind.Array)
            .GetProperty("id").GetString();

        var response = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/preview/active-external", new { serviceId = groupId });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("port").GetInt32().Should().Be(port);
    }

    [Fact]
    public async Task ActiveExternal_UnknownService_ReturnsNotFound()
    {
        var (projectId, _) = await CreateProjectAsync();
        var response = await _owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/preview/active-external", new { serviceId = "does-not-exist" });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ActiveExternal_AsNonOwner_ReturnsForbid()
    {
        var (projectId, _) = await CreateProjectAsync();
        var other = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var response = await other.PostAsJsonAsync(
            $"/api/projects/{projectId}/preview/active-external", new { serviceId = "any" });
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }
}
