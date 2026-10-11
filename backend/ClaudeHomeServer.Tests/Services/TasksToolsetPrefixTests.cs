using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services;

// Префиксный поиск tasks_get (задача 77ef4a18): агент получает из текста короткий id вида
// 0f951764 и раньше уходил в обход (Grep по tasks.json мимо проверки владельца/видимости).
// Теперь tasks_get сам находит задачу по префиксу id (>= 8 hex/«-»), но ТОЛЬКО чтение:
//  • одно совпадение → карточка;
//  • несколько → Deny со списком кандидатов (id + title, до 5);
//  • префикс короче 8 → прежний отказ «не найдена»;
//  • изоляция: чужой владелец и проект вне allowed не находятся и не попадают в список;
//  • запись (tasks_update) по префиксу по-прежнему не находит задачу.
[Trait("Category", "Integration")]
public class TasksToolsetPrefixTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public TasksToolsetPrefixTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(string name)
    {
        var project = await _client.PostAsJsonAsync("/api/projects",
            new { name, rootPath = Path.Combine(_factory.TempDir, name + "_" + Guid.NewGuid().ToString("N")[..8]), createDirectory = true });
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

    // Вызов MCP-инструмента через /mcp/tasks/{sessionId}: {content[0].text, isError}.
    private async Task<(string Text, bool IsError)> CallToolAsync(string sessionId, string tool, object args)
    {
        var resp = await _client.PostAsync($"/mcp/tasks/{sessionId}",
            new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = tool, arguments = args },
            }), System.Text.Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "MCP отвечает 200 даже на отказ — isError=true в теле, а не HTTP-код");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var result = body.GetProperty("result");
        var isError = result.TryGetProperty("isError", out var e) && e.GetBoolean();
        var text = result.GetProperty("content").EnumerateArray()
            .First().GetProperty("text").GetString()!;
        return (text, isError);
    }

    // Задача с управляемым id в проекте (REST POST /api/projects/{id}/tasks): TaskManager.Create
    // берёт req.Id как есть, поэтому префикс детерминирован.
    private async Task<string> CreateProjectTaskWithIdAsync(string projectId, string id, string title)
    {
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/tasks",
            new { id, title });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Личная задача (вне проекта) с управляемым id.
    private async Task<string> CreatePersonalTaskWithIdAsync(string id, string title)
    {
        var resp = await _client.PostAsJsonAsync("/api/tasks", new { id, title });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // ─── Одно совпадение по префиксу → карточка ─────────────────────────────

    [Fact]
    public async Task ОдноСовпадение_ПоПрефиксу_ВозвращаетКарточку()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("prefix-one");
        var createdId = await CreateProjectTaskWithIdAsync(projectId,
            "11112222-3333-4444-5555-666677778888", "Задача по префиксу");

        var (text, isError) = await CallToolAsync(sessionId, "tasks_get", new { id = "11112222" });

        isError.Should().BeFalse("ровно одно совпадение по префиксу — карточка, а не отказ");
        var card = JsonSerializer.Deserialize<JsonElement>(text);
        card.GetProperty("id").GetString().Should().Be(createdId);
        card.GetProperty("title").GetString().Should().Be("Задача по префиксу");
    }

    // ─── Два совпадения по префиксу → отказ со списком кандидатов ───────────

    [Fact]
    public async Task ДваСовпадения_ПоПрефиксу_ОтказСоСписком()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("prefix-two");
        await CreateProjectTaskWithIdAsync(projectId,
            "aabbccdd-1111-2222-3333-444455556666", "Кандидат A");
        await CreateProjectTaskWithIdAsync(projectId,
            "aabbccdd-9999-8888-7777-666655554444", "Кандидат B");

        var (text, isError) = await CallToolAsync(sessionId, "tasks_get", new { id = "aabbccdd" });

        isError.Should().BeTrue("несколько совпадений по префиксу — отказ, а не молчаливый выбор");
        text.Should().Contain("Несколько задач");
        text.Should().Contain("aabbccdd-1111-2222-3333-444455556666").And.Contain("Кандидат A");
        text.Should().Contain("aabbccdd-9999-8888-7777-666655554444").And.Contain("Кандидат B");
    }

    // ─── Префикс короче 8 → прежний отказ «не найдена» ───────────────────────

    [Fact]
    public async Task ПрефиксКорочеВосьми_ОтказКакРаньше()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("prefix-short");
        await CreateProjectTaskWithIdAsync(projectId,
            "22223333-4444-5555-6666-777788889999", "Задача с коротким префиксом");

        // 7 hex-символов — короче порога: точного совпадения нет, префиксный поиск не включается
        var (text, isError) = await CallToolAsync(sessionId, "tasks_get", new { id = "2222333" });

        isError.Should().BeTrue();
        text.Should().Contain("не найдена");
        text.Should().NotContain("Несколько задач");
    }

    // ─── Изоляция: чужой владелец не находится и не попадает в список ───────

    [Fact]
    public async Task ЧужойВладелец_НеНаходитсяИНеВСписке()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("prefix-foreign");

        // Личная задача ВТОРОГО владельца с префиксом, совпадающим по форме
        using var secondClient = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var foreignResp = await secondClient.PostAsJsonAsync("/api/tasks",
            new { id = "ffeedd-1111-2222-3333-444455556666", title = "Чужая задача" });
        foreignResp.EnsureSuccessStatusCode();

        var (text, isError) = await CallToolAsync(sessionId, "tasks_get", new { id = "ffeedd" });

        isError.Should().BeTrue("задача другого владельца не видна даже с подходящим префиксом");
        text.Should().Contain("не найдена");
        text.Should().NotContain("ffeedd-1111-2222-3333-444455556666",
            "чужая задача не должна попасть даже в текст отказа");
        text.Should().NotContain("Чужая задача");
    }

    // ─── Изоляция: проект вне allowed не находится и не попадает в список ────

    [Fact]
    public async Task ПроектВнеAllowed_НеНаходитсяИНеВСписке()
    {
        // Сессия в ownProject; задача в другом (непривязанном) проекте того же владельца
        var (ownProject, sessionId) = await CreateProjectWithSessionAsync("prefix-scope-own");
        var other = await _client.PostAsJsonAsync("/api/projects",
            new { name = "prefix-scope-other-" + Guid.NewGuid().ToString("N")[..8],
                  rootPath = Path.Combine(_factory.TempDir, "prefix-scope-other_" + Guid.NewGuid().ToString("N")[..8]),
                  createDirectory = true });
        other.EnsureSuccessStatusCode();
        var otherProjectId = JsonSerializer.Deserialize<JsonElement>(await other.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        await CreateProjectTaskWithIdAsync(otherProjectId,
            "deadbeef-1111-2222-3333-444455556666", "Задача в чужом проекте");

        var (text, isError) = await CallToolAsync(sessionId, "tasks_get", new { id = "deadbeef" });

        isError.Should().BeTrue("задача из проекта вне allowed не видна из сессии в ownProject");
        text.Should().Contain("не найдена");
        text.Should().NotContain("deadbeef-1111-2222-3333-444455556666");
        text.Should().NotContain("Задача в чужом проекте");
    }

    // ─── Запись: tasks_update по префиксу по-прежнему не находит задачу ──────

    [Fact]
    public async Task Update_ПоПрефиксу_НеНаходитЗадачу()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync("prefix-write");
        var createdId = await CreateProjectTaskWithIdAsync(projectId,
            "33334444-5555-6666-7777-888899990000", "Задача для записи");

        // tasks_update идёт полным id (GetAccessible): префикс не резолвится → «не найдена»
        var (text, isError) = await CallToolAsync(sessionId, "tasks_update", new { id = "33334444", title = "Новое" });

        isError.Should().BeTrue("запись по префиксу не должна молча менять задачу");
        text.Should().Contain("не найдена");
        // Карточка не изменилась: заголовок прежний
        var (getText, _) = await CallToolAsync(sessionId, "tasks_get", new { id = createdId });
        var card = JsonSerializer.Deserialize<JsonElement>(getText);
        card.GetProperty("title").GetString().Should().Be("Задача для записи");
    }
}
