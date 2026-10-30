using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Узкая рамка хода в платформенном MCP (S2a-3a, решение 29.10): в чате ОБЩЕГО проекта (есть участники)
/// wsp/tasks/notes видят только проект чата и отказывают во всём личном пространстве владельца — даже
/// самому владельцу. Личный проект остаётся с широкой рамкой. Вердикт читается из ITurnFrameSource по
/// заголовку X-Caller-Session-Id; в отказе нет id и названий из личных данных (маркеры PERSONAL-*).
/// </summary>
public class McpNarrowFrameTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public McpNarrowFrameTests() => _client = _factory.CreateAuthenticatedClient();

    public void Dispose() => _factory.Dispose();

    private static string IdOf(JsonElement e) => e.GetProperty("id").GetString()!;

    private async Task<string> CreateProjectAsync(string name)
    {
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreateSessionAsync(string projectId)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        resp.EnsureSuccessStatusCode();
        return IdOf(JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync()));
    }

    private async Task<(string Text, bool IsError)> CallAsync(string server, string chat, string tool, object args)
    {
        var resp = await _client.AsCaller(chat).PostAsJsonAsync($"/mcp/{server}/{chat}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content")[0].GetProperty("text").GetString()!, isError);
    }

    /// <summary>Личный проект с маркерами (проект, задача, заметка «Личный») и общий проект с чатом.</summary>
    private async Task<Fixture> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var personalName = $"PERSONAL-проект-{tag}";
        var personalProject = await CreateProjectAsync(personalName);
        var personalChat = await CreateSessionAsync(personalProject);
        var taskResp = await _client.PostAsJsonAsync($"/api/projects/{personalProject}/tasks",
            new { title = $"PERSONAL-задача-{tag}" });
        taskResp.EnsureSuccessStatusCode();
        var taskId = IdOf(JsonSerializer.Deserialize<JsonElement>(await taskResp.Content.ReadAsStringAsync()));
        var noteTitle = $"PERSONAL-заметка-{tag}";
        var (noteJson, noteErr) = await CallAsync("notes", personalChat, "notes_create",
            new { title = noteTitle, content = "личное содержимое", source = "personal" });
        noteErr.Should().BeFalse(noteJson);
        var noteId = IdOf(JsonSerializer.Deserialize<JsonElement>(noteJson));

        var sharedProject = await CreateProjectAsync($"Общий-{tag}");
        _factory.Services.GetRequiredService<ProjectManager>().SetMembers(sharedProject,
            [new ProjectMember { UserId = "участник-" + tag, Role = ProjectRole.Editor }]);
        var sharedChat = await CreateSessionAsync(sharedProject);
        return new Fixture(tag, personalName, personalProject, personalChat, taskId, noteTitle, noteId,
            sharedProject, sharedChat);
    }

    private sealed record Fixture(string Tag, string PersonalName, string PersonalProject, string PersonalChat,
        string TaskId, string NoteTitle, string NoteId, string SharedProject, string SharedChat)
    {
        public void AssertNoPersonal(string text) => text.Should()
            .NotContain("PERSONAL").And.NotContain(PersonalProject).And.NotContain(TaskId).And.NotContain(NoteId);
    }

    // --- wsp ---

    [Theory]
    [InlineData("search_unified")]
    [InlineData("deploy_status")]
    [InlineData("deploy_start")]
    [InlineData("projects_create")]
    [InlineData("kb_list")]
    public async Task Wsp_УзкаяРамка_ИнструментыВнеПроекта_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync("wsp", f.SharedChat, tool,
            new { query = f.Tag, name = "x" });
        isError.Should().BeTrue(text);
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Wsp_УзкаяРамка_ЛичныйПроект_ФайлыИПроектыОтказ_БезЛичныхДанных()
    {
        var f = await ArrangeAsync();
        foreach (var (tool, args) in new (string, object)[]
        {
            ("files_tree", new { projectId = f.PersonalProject }),
            ("projects_get", new { projectId = f.PersonalProject }),
            ("files_delete", new { projectId = f.SharedProject, path = "x" }),
            ("chats_delete", new { sessionId = f.PersonalChat }),
            ("chats_history", new { sessionId = f.PersonalChat }),
            ("chats_list", new { }),
            ("tags_apply", new { entityType = "task", entityId = f.TaskId, tags = new[] { "t" }, projectId = f.PersonalProject }),
        })
        {
            var (text, isError) = await CallAsync("wsp", f.SharedChat, tool, args);
            isError.Should().BeTrue($"{tool}: {text}");
            f.AssertNoPersonal(text);
        }
    }

    [Fact]
    public async Task Wsp_УзкаяРамка_СписокПроектов_ТолькоСвой()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync("wsp", f.SharedChat, "projects_list", new { });
        isError.Should().BeFalse(text);
        text.Should().Contain(f.SharedProject);
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Wsp_УзкаяРамка_СвойПроект_ДоступенПоПравамРоли()
    {
        var f = await ArrangeAsync();
        var (tree, treeErr) = await CallAsync("wsp", f.SharedChat, "files_tree", new { projectId = f.SharedProject });
        treeErr.Should().BeFalse(tree);
        var (chats, chatsErr) = await CallAsync("wsp", f.SharedChat, "chats_list", new { projectId = f.SharedProject });
        chatsErr.Should().BeFalse(chats);
    }

    [Fact]
    public async Task Wsp_ЛичныйПроект_ШирокаяРамка_ПоискИДеплойНеСужены()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync("wsp", f.PersonalChat, "search_unified", new { query = f.Tag });
        isError.Should().BeFalse(text);
        var (list, listErr) = await CallAsync("wsp", f.PersonalChat, "projects_list", new { });
        listErr.Should().BeFalse(list);
        list.Should().Contain(f.PersonalProject).And.Contain(f.SharedProject);
    }

    // --- tasks ---

    [Theory]
    [InlineData("tasks_list_projects")]
    [InlineData("tasks_search")]
    public async Task Tasks_УзкаяРамка_СписокПроектовИПоиск_Отказ(string tool)
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync("tasks", f.SharedChat, tool, new { query = "PERSONAL" });
        isError.Should().BeTrue(text);
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Tasks_УзкаяРамка_ScopeAllИЧужойProjectId_Отказ()
    {
        var f = await ArrangeAsync();
        var (all, allErr) = await CallAsync("tasks", f.SharedChat, "tasks_list", new { scope = "all" });
        allErr.Should().BeTrue(all);
        f.AssertNoPersonal(all);
        var (foreign, foreignErr) = await CallAsync("tasks", f.SharedChat, "tasks_list",
            new { projectId = f.PersonalProject });
        foreignErr.Should().BeTrue(foreign);
        f.AssertNoPersonal(foreign);
    }

    [Fact]
    public async Task Tasks_УзкаяРамка_СвойПроект_ДоступенИНеВидитЛичныхЗадач()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync("tasks", f.SharedChat, "tasks_list", new { });
        isError.Should().BeFalse(text);
        f.AssertNoPersonal(text);
    }

    [Fact]
    public async Task Tasks_ЛичныйПроект_ScopeAll_РаботаетКакПрежде()
    {
        var f = await ArrangeAsync();
        var (text, isError) = await CallAsync("tasks", f.PersonalChat, "tasks_list_projects", new { });
        isError.Should().BeFalse(text);
    }

    // --- notes ---

    [Fact]
    public async Task Notes_УзкаяРамка_ЛичныйИсточникИПоискПоВсем_Отказ()
    {
        var f = await ArrangeAsync();
        foreach (var (tool, args) in new (string, object)[]
        {
            ("notes_list", new { source = "personal" }),
            ("notes_read", new { id = f.NoteId }),
            ("notes_update", new { id = f.NoteId, content = "x" }),
            ("notes_delete", new { id = f.NoteId }),
            ("notes_create", new { title = "x", source = "personal" }),
            ("notes_graph", new { }),
            ("notes_daily", new { }),
            ("notes_semantic_search", new { query = "PERSONAL" }),
        })
        {
            var (text, isError) = await CallAsync("notes", f.SharedChat, tool, args);
            isError.Should().BeTrue($"{tool}: {text}");
            f.AssertNoPersonal(text);
        }
    }

    [Fact]
    public async Task Notes_УзкаяРамка_ПоискНеНаходитЛичныхЗаметок_СвойИсточникРаботает()
    {
        var f = await ArrangeAsync();
        var (search, searchErr) = await CallAsync("notes", f.SharedChat, "notes_search", new { query = "PERSONAL" });
        searchErr.Should().BeFalse(search);
        f.AssertNoPersonal(search);
        var (created, createdErr) = await CallAsync("notes", f.SharedChat, "notes_create",
            new { title = "Заметка проекта " + f.Tag, content = "общая" });
        createdErr.Should().BeFalse(created);
        var (read, readErr) = await CallAsync("notes", f.SharedChat, "notes_read",
            new { id = IdOf(JsonSerializer.Deserialize<JsonElement>(created)) });
        readErr.Should().BeFalse(read);
    }

    [Fact]
    public async Task Notes_ЛичныйПроект_ШирокаяРамка_ЛичнаяЗаметкаЧитается()
    {
        var f = await ArrangeAsync();
        var (read, readErr) = await CallAsync("notes", f.PersonalChat, "notes_read", new { id = f.NoteId });
        readErr.Should().BeFalse(read);
        read.Should().Contain(f.NoteTitle);
    }

    // --- fail-closed ---

    [Fact]
    public async Task ВызовБезЗаголовкаЧата_УзкаяРамка_ДажеВЛичномПроекте()
    {
        var f = await ArrangeAsync();
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync($"/mcp/wsp/{f.PersonalChat}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "search_unified", arguments = new { query = f.Tag } },
        });
        resp.EnsureSuccessStatusCode();
        var result = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue("нет заголовка — рамка узкая (fail-closed)");
    }
}
