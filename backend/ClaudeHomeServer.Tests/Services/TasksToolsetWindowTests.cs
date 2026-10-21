using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Окно закрытых и потолок tasks_list (MCP) плюс параметр doneDays у REST GET /api/tasks.
[Trait("Category", "Integration")]
public class TasksToolsetWindowTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly string _projectDir;

    public TasksToolsetWindowTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _projectDir = Path.Combine(factory.TempDir, "tasks_window_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
    }

    private TaskManager Tasks => _factory.Services.GetRequiredService<TaskManager>();

    private string OwnerId => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync()
    {
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "TasksWindow", rootPath = _projectDir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    private TaskItem Seed(string projectId, string title, int? closedDaysAgo)
    {
        var t = Tasks.Create(projectId, OwnerId, new CreateTaskRequest(Title: title));
        if (closedDaysAgo is { } d)
        {
            t.Status = TaskItemStatus.Done;
            t.CompletedAt = DateTime.UtcNow.AddDays(-d);
        }
        return t;
    }

    private async Task<JsonElement> ListAsync(string sessionId, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "tasks_list", arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var text = body.GetProperty("result").GetProperty("content").EnumerateArray()
            .First().GetProperty("text").GetString()!;
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static HashSet<string> Titles(JsonElement list) =>
        list.EnumerateArray().Where(x => x.GetProperty("id").ValueKind != JsonValueKind.Null)
            .Select(x => x.GetProperty("title").GetString()!).ToHashSet();

    [Fact]
    public async Task List_БезФильтров_ОткрытыеИЗакрытыеЗаДвеНедели()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        Seed(projectId, "открытая", null);
        Seed(projectId, "закрыта-13", 13);
        Seed(projectId, "закрыта-15", 15);

        var titles = Titles(await ListAsync(sessionId, new { projectId }));

        titles.Should().BeEquivalentTo(["открытая", "закрыта-13"]);
    }

    [Fact]
    public async Task List_StatusDoneБезДат_ВсеЗакрытые()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        Seed(projectId, "открытая", null);
        Seed(projectId, "закрыта-13", 13);
        Seed(projectId, "закрыта-15", 15);

        var titles = Titles(await ListAsync(sessionId, new { projectId, status = "done" }));

        titles.Should().BeEquivalentTo(["закрыта-13", "закрыта-15"]);
    }

    [Fact]
    public async Task List_DoneDaysНоль_ТолькоОткрытые()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        Seed(projectId, "открытая", null);
        Seed(projectId, "закрыта-1", 1);

        var titles = Titles(await ListAsync(sessionId, new { projectId, doneDays = "0" }));

        titles.Should().BeEquivalentTo(["открытая"]);
    }

    [Fact]
    public async Task List_DoneDaysAll_ВсеЗакрытыеИОткрытые()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        Seed(projectId, "открытая", null);
        Seed(projectId, "закрыта-15", 15);
        Seed(projectId, "закрыта-400", 400);

        var titles = Titles(await ListAsync(sessionId, new { projectId, doneDays = "all" }));

        titles.Should().BeEquivalentTo(["открытая", "закрыта-15", "закрыта-400"]);
    }

    [Fact]
    public async Task List_DoneDaysЧислом_СужаетОкно()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        Seed(projectId, "закрыта-3", 3);
        Seed(projectId, "закрыта-10", 10);

        var titles = Titles(await ListAsync(sessionId, new { projectId, doneDays = 7 }));

        titles.Should().BeEquivalentTo(["закрыта-3"]);
    }

    [Fact]
    public async Task List_Потолок_350Задач_300ИNote()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        for (var i = 0; i < 350; i++) Seed(projectId, $"т{i}", null);

        var list = await ListAsync(sessionId, new { projectId });

        list.GetArrayLength().Should().Be(301, "300 задач плюс note первым элементом");
        var first = list[0];
        first.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        first.GetProperty("note").GetString().Should().Contain("показано 300 из 350");
    }

    [Fact]
    public async Task List_Потолок_299Задач_БезNote()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync();
        for (var i = 0; i < 299; i++) Seed(projectId, $"т{i}", null);

        var list = await ListAsync(sessionId, new { projectId });

        list.GetArrayLength().Should().Be(299);
        list.EnumerateArray().Should().OnlyContain(x => x.GetProperty("id").ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Rest_DoneDays_ОтбрасываетСтарыеЗакрытые_БезПараметраВсе()
    {
        var (projectId, _) = await CreateProjectWithSessionAsync();
        Seed(projectId, "открытая", null);
        Seed(projectId, "закрыта-3", 3);
        Seed(projectId, "закрыта-30", 30);

        async Task<HashSet<string>> Get(string query)
        {
            var resp = await _client.GetAsync($"/api/tasks?projectId={projectId}{query}");
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            var arr = await resp.Content.ReadFromJsonAsync<JsonElement>();
            return arr.EnumerateArray().Select(x => x.GetProperty("title").GetString()!).ToHashSet();
        }

        (await Get("")).Should().BeEquivalentTo(["открытая", "закрыта-3", "закрыта-30"]);
        (await Get("&doneDays=7")).Should().BeEquivalentTo(["открытая", "закрыта-3"]);
    }
}
