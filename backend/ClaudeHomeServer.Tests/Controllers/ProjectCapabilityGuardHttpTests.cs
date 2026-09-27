using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Сторож G1 (ADR-016 §4, план §4), поведенческая часть: <c>RootPath</c> локального проекта
/// указывает в несуществующий каталог-ловушку. Каждый вход файловой и выключенной групп
/// обязан ответить отказом <c>local_project</c> — не «не найдено» и не ошибкой ввода-вывода:
/// так видно, что отказ случился раньше диска. Ловушка после всех вызовов не появилась —
/// значит, и пишущие входы до диска не дошли.
/// Отражательная часть (каждый вход размечен) — <c>ProjectCapabilityGuardTests</c>.
/// </summary>
public sealed class ProjectCapabilityGuardHttpTests : IDisposable
{
    private sealed class FakeDeviceExec : IDeviceExecChannel
    {
        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) => deviceId == "dev"
            ? new(deviceId, "home", true, "linux-x64", "1.0.0", "2.0.0", "2.0.0",
                [DeviceCapabilities.Exec], true, null)
            : null;
        public Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProjectCapabilityGuardHttpTests()
    {
        _factory = new TestWebApplicationFactory
        {
            ExtraServices = s => s.AddSingleton<IDeviceExecChannel>(new FakeDeviceExec()),
        };
        _client = _factory.CreateAuthenticatedClient();
    }

    public void Dispose() => _factory.Dispose();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private string MkDir()
    {
        var dir = Path.Combine(_factory.TempDir, "g1_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task<string> CreateServerProjectAsync(string root)
    {
        var r = await _client.PostAsJsonAsync("/api/projects", new { name = "S-" + Guid.NewGuid().ToString("N")[..6], rootPath = root });
        r.EnsureSuccessStatusCode();
        return (await BodyAsync(r)).GetProperty("id").GetString()!;
    }

    // Локальный проект в каталог-ловушку: серверный проект переезжает на устройство
    private async Task<(string Id, string Trap)> CreateTrapProjectAsync()
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var id = await CreateServerProjectAsync(MkDir());
        var trap = Path.Combine(_factory.TempDir, "trap_" + Guid.NewGuid().ToString("N")[..8]);
        (await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev", rootPath = trap }))
            .EnsureSuccessStatusCode();
        Directory.Exists(trap).Should().BeFalse();
        return (id, trap);
    }

    private static async Task ShouldBeLocalProjectRefusal(HttpResponseMessage r, string what)
    {
        r.StatusCode.Should().Be(HttpStatusCode.Conflict, what);
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(ProjectCapabilityGuard.Code, what);
    }

    [Fact]
    public async Task ВходыФайловойИВыключеннойГрупп_ОтказLocalProject_ДоДиска()
    {
        var (id, trap) = await CreateTrapProjectAsync();
        var p = $"/api/projects/{id}";

        (string What, Func<Task<HttpResponseMessage>> Call)[] calls =
        [
            ("files list", () => _client.GetAsync($"{p}/files")),
            ("files tree", () => _client.GetAsync($"{p}/files/tree")),
            ("files content", () => _client.GetAsync($"{p}/files/content?path=a.txt")),
            ("files mkdir", () => _client.PostAsJsonAsync($"{p}/files/mkdir", new { path = "x" })),
            ("files create", () => _client.PostAsJsonAsync($"{p}/files/create", new { path = "a.txt", content = "1" })),
            ("files save", () => _client.PutAsJsonAsync($"{p}/files/content?path=a.txt", new { content = "1" })),
            ("files changed-by", () => _client.PostAsJsonAsync($"{p}/files/changed-by", new { paths = new[] { "a.txt" } })),
            ("sync", () => _client.GetAsync($"{p}/sync")),
            ("git status", () => _client.GetAsync($"{p}/git/status")),
            ("git init", () => _client.PostAsync($"{p}/git/init", null)),
            ("preview services", () => _client.GetAsync($"{p}/services")),
            ("skills", () => _client.GetAsync($"{p}/skills")),
            ("skills install (projectId в теле)", () => _client.PostAsJsonAsync("/api/skills/install",
                new { source = "owner/repo", skill = "s", scope = "project", projectId = id })),
            ("preset apply", () => _client.PostAsJsonAsync($"{p}/preset", new { presetKey = "blank" })),
            ("knowledge", () => _client.GetAsync($"{p}/knowledge")),
            ("code-graph", () => _client.GetAsync($"{p}/code-graph")),
            ("docs", () => _client.GetAsync($"{p}/docs")),
            ("dossiers", () => _client.GetAsync($"{p}/dossiers")),
            ("map-hygiene", () => _client.GetAsync($"{p}/map-hygiene/scan")),
        ];
        foreach (var (what, call) in calls)
            await ShouldBeLocalProjectRefusal(await call(), what);

        Directory.Exists(trap).Should().BeFalse("ни один вход не должен был дойти до диска");
    }

    [Fact]
    public async Task ПлатформенныеВходы_РаботаютУЛокальногоПроекта()
    {
        var (id, _) = await CreateTrapProjectAsync();

        (await _client.GetAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync($"/api/projects/{id}/tasks")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ВложениеЧатаЛокальногоПроекта_ОтказLocalProject()
    {
        var (id, trap) = await CreateTrapProjectAsync();
        var created = await _client.PostAsJsonAsync($"/api/projects/{id}/sessions", new { mode = "auto" });
        created.EnsureSuccessStatusCode();
        var sessionId = (await BodyAsync(created)).GetProperty("id").GetString()!;

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "a.bin");
        await ShouldBeLocalProjectRefusal(await _client.PostAsync($"/api/chats/{sessionId}/files/upload", form), "upload");
        Directory.Exists(trap).Should().BeFalse();
    }

    [Fact]
    public async Task ИнструментыWsp_ОтказLocalProject_ДоДиска()
    {
        var (localId, trap) = await CreateTrapProjectAsync();
        var chatProject = await CreateServerProjectAsync(MkDir());
        var created = await _client.PostAsJsonAsync($"/api/projects/{chatProject}/sessions", new { mode = "acceptEdits" });
        created.EnsureSuccessStatusCode();
        var sessionId = (await BodyAsync(created)).GetProperty("id").GetString()!;

        foreach (var (tool, args) in new (string, object)[]
                 {
                     ("files_tree", new { projectId = localId }),
                     ("files_write", new { projectId = localId, path = "a.txt", content = "1" }),
                     ("files_mkdir", new { projectId = localId, path = "x" }),
                     ("knowledge_status", new { projectId = localId }),
                 })
        {
            var resp = await _client.PostAsJsonAsync($"/mcp/wsp/{sessionId}", new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            });
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var result = (await BodyAsync(resp)).GetProperty("result");
            result.GetProperty("isError").GetBoolean().Should().BeTrue(tool);
            result.GetProperty("content")[0].GetProperty("text").GetString()
                .Should().StartWith(ProjectCapabilityGuard.Code, tool);
        }
        Directory.Exists(trap).Should().BeFalse();
    }

    [Fact]
    public async Task ПереездНаУстройство_ЗаписьЗнанийСервернойПапкиСнимается()
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var dir = MkDir();
        var id = await CreateServerProjectAsync(dir);
        var wkStore = _factory.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        wkStore.GetOrCreate(dir);
        wkStore.GetByPath(dir).Should().NotBeNull();

        (await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev", rootPath = "/home/u/p" }))
            .EnsureSuccessStatusCode();

        wkStore.GetByPath(dir).Should().BeNull("серверная папка больше не проект — запись знаний не должна сиротеть");
    }

    [Fact]
    public async Task ПереездНаУстройство_СоседПоПапке_ЗаписьЗнанийОстаётся()
    {
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.LocalProjects}", new { enabled = true }))
            .EnsureSuccessStatusCode();
        var dir = MkDir();
        var id = await CreateServerProjectAsync(dir);
        var wkStore = _factory.Services.GetRequiredService<WorkspaceKnowledgeStore>();
        wkStore.GetOrCreate(dir);
        // Сосед по папке — проект другого владельца (у одного владельца папка одна)
        var projects = _factory.Services.GetRequiredService<ClaudeHomeServer.Services.ProjectManager>();
        projects.Create("neighbour", dir, "other-owner", "other");

        (await _client.PutAsJsonAsync($"/api/projects/{id}/device", new { deviceId = "dev", rootPath = "/home/u/p" }))
            .EnsureSuccessStatusCode();

        wkStore.GetByPath(dir).Should().NotBeNull("папкой ещё пользуется соседний серверный проект");
    }
}
