using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services.Mcp.Http;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Компактные ответы tasks_* (задача 85fbaee8) и рубильник Mcp:TasksCompactResponses.
// Мутации:
// • вернуть в case "tasks_create" прежний Json(ForCaller(...)) — Create_Компактный_* краснеет;
// • убрать ветку compact в TaskJson — Get_Компактный_* краснеет;
// • читать CompactMode константой true — РежимПолный_* краснеет (откат конфигом не работает);
// • не передавать "subtasks" в MutationJson у add_subtask — AddSubtask_ВозвращаетIdПодзадач краснеет.
[Trait("Category", "Integration")]
[Trait("Category", "Host")]
public class TasksToolsetCompactResponseTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetCompactResponseTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<string> CreateSessionAsync()
    {
        var dir = Path.Combine(_factory.TempDir, "mcp_compact_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name = "McpCompact" + Guid.NewGuid().ToString("N")[..6], rootPath = dir, createDirectory = true });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await project.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var session = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await session.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.AsCaller(sessionId).PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        return (result.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString()!, isError);
    }

    private static JsonElement Parse(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    private async Task<string> CreateTaskAsync(string sessionId, object args)
    {
        var (text, err) = await CallToolAsync(sessionId, "tasks_create", args);
        err.Should().BeFalse(text);
        return Parse(text).GetProperty("id").GetString()!;
    }

    private static bool HasNull(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => true,
        JsonValueKind.Object => el.EnumerateObject().Any(p => HasNull(p.Value)),
        JsonValueKind.Array => el.EnumerateArray().Any(HasNull),
        _ => false,
    };

    [Fact]
    public async Task Create_Компактный_БезЭхаОписанияИNull_СГуидомДляФронта()
    {
        var sessionId = await CreateSessionAsync();
        var (text, err) = await CallToolAsync(sessionId, "tasks_create",
            new { title = "Компактная", description = "Очень длинное описание " + new string('я', 500), priority = "high" });
        err.Should().BeFalse(text);

        var card = Parse(text);
        card.TryGetProperty("description", out _).Should().BeFalse("присланное описание не повторяем");
        HasNull(card).Should().BeFalse("null-поля в ответе не едут");
        card.TryGetProperty("reminderSentAt", out _).Should().BeFalse();
        card.GetProperty("status").GetString().Should().Be("todo");
        card.GetProperty("priority").GetString().Should().Be("high");
        card.GetProperty("shortId").GetString().Should().Be(card.GetProperty("id").GetString()![..8]);
        // useSessionArtifacts достаёт guid из result именно такой регуляркой
        Regex.IsMatch(text, "\"id\"\\s*:\\s*\"[0-9a-fA-F-]{36}\"").Should().BeTrue();
        text.Length.Should().BeLessThan(700, "карточка без описания и null — короткая");
    }

    [Fact]
    public async Task Create_Компактный_КритерииПриёмкиВозвращаютсяСIdСервера()
    {
        var sessionId = await CreateSessionAsync();
        var (text, err) = await CallToolAsync(sessionId, "tasks_create",
            new { title = "С критериями", acceptance = new[] { new { text = "первый" }, new { text = "второй" } } });
        err.Should().BeFalse(text);
        var ids = Parse(text).GetProperty("acceptance").EnumerateArray().Select(a => a.GetProperty("id").GetString()).ToList();
        ids.Should().Equal("c1", "c2");
    }

    [Fact]
    public async Task Update_Компактный_ВозвращаетСтатусБезЭхаТекстов()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskAsync(sessionId, new { title = "К обновлению", description = "исходное" });

        var (text, err) = await CallToolAsync(sessionId, "tasks_update",
            new { id, status = "inProgress", description = "новое длинное описание" });
        err.Should().BeFalse(text);
        var card = Parse(text);
        card.GetProperty("id").GetString().Should().Be(id);
        card.GetProperty("status").GetString().Should().Be("inProgress");
        card.TryGetProperty("description", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Complete_Компактный_ВозвращаетСтатусDoneБезЭхаИтога()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskAsync(sessionId, new { title = "К закрытию" });

        var (text, err) = await CallToolAsync(sessionId, "tasks_complete",
            new { id, resultMarkdown = "Итог работы " + new string('ы', 300) });
        err.Should().BeFalse(text);
        var card = Parse(text);
        card.GetProperty("status").GetString().Should().Be("done");
        card.TryGetProperty("resultMarkdown", out _).Should().BeFalse("итог прислан вызывающим");
    }

    [Fact]
    public async Task AddSubtask_ВозвращаетIdПодзадач()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskAsync(sessionId, new { title = "С подзадачами" });

        var (text, err) = await CallToolAsync(sessionId, "tasks_add_subtask", new { taskId = id, title = "п1" });
        err.Should().BeFalse(text);
        var subtask = Parse(text).GetProperty("subtasks").EnumerateArray().Single();
        subtask.GetProperty("id").GetString().Should().NotBeNullOrEmpty("toggle_subtask ищет по id");

        var (toggled, err2) = await CallToolAsync(sessionId, "tasks_toggle_subtask",
            new { taskId = id, subtaskId = subtask.GetProperty("id").GetString(), isDone = true });
        err2.Should().BeFalse(toggled);
        Parse(toggled).GetProperty("subtasks").EnumerateArray().Single().GetProperty("isDone").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Get_Компактный_СОписаниемБезNullИСлужебного_FullВозвращаетВсё()
    {
        var sessionId = await CreateSessionAsync();
        var id = await CreateTaskAsync(sessionId, new { title = "Для чтения", description = "полное описание" });

        var (compact, err) = await CallToolAsync(sessionId, "tasks_get", new { id });
        err.Should().BeFalse(compact);
        var card = Parse(compact);
        card.GetProperty("description").GetString().Should().Be("полное описание", "чтение отдаёт текст");
        HasNull(card).Should().BeFalse();
        card.TryGetProperty("order", out _).Should().BeFalse();
        card.TryGetProperty("completionDelivered", out _).Should().BeFalse();

        var (full, err2) = await CallToolAsync(sessionId, "tasks_get", new { id, full = true });
        err2.Should().BeFalse(full);
        var all = Parse(full);
        all.TryGetProperty("order", out _).Should().BeTrue("full=true — карточка как есть");
        all.TryGetProperty("completionDelivered", out _).Should().BeTrue();
        all.TryGetProperty("reminderSentAt", out var r).Should().BeTrue();
        r.ValueKind.Should().Be(JsonValueKind.Null);
        compact.Length.Should().BeLessThan(full.Length);
    }

    [Fact]
    public async Task РежимПолный_КонфигомВозвращаетПрежнийОтвет_БезВыкатки()
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var sessionId = await CreateSessionAsync();
        var previous = config[TasksCompactView.ConfigKey];
        try
        {
            config[TasksCompactView.ConfigKey] = "false";
            var (text, err) = await CallToolAsync(sessionId, "tasks_create",
                new { title = "Полный режим", description = "описание на месте" });
            err.Should().BeFalse(text);
            var card = Parse(text);
            card.GetProperty("description").GetString().Should().Be("описание на месте");
            card.TryGetProperty("reminderSentAt", out _).Should().BeTrue("полный режим — прежняя карточка со всеми полями");
            card.TryGetProperty("order", out _).Should().BeTrue();

            config[TasksCompactView.ConfigKey] = "true";
            var (back, _) = await CallToolAsync(sessionId, "tasks_create", new { title = "Снова компактный", description = "x" });
            Parse(back).TryGetProperty("description", out _).Should().BeFalse("переключение живое, без перезапуска");
        }
        finally
        {
            config[TasksCompactView.ConfigKey] = previous;
        }
    }
}
