using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Изоляция и живой путь http-тулсета заметок (ADR-012, фаза 2 волна 2): заметки — per-owner
/// файлы, эндпоинт торчит наружу вместе с Kestrel, поэтому сессия из хвоста обязана
/// принадлежать владельцу ТОКЕНА. Парный тест для задач — TasksHttpDelegationGateTests,
/// для памяти — MemoryHttpTransportTests.
/// </summary>
public class NotesHttpOwnerIsolationTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Client => _factory.CreateAuthenticatedClient();

    private async Task<(string ProjectId, string SessionId)> CreateProjectWithSessionAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"notes-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(
            await project.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        var sessionId = JsonSerializer.Deserialize<JsonElement>(
            await session.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        return (projectId, sessionId);
    }

    private async Task<JsonElement> CallToolAsync(HttpClient client, string sessionId, string tool, object args)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/notes/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = tool, arguments = args },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Живой путь приёмки: notes_create на хвосте своей сессии создаёт заметку в notes/
    /// проекта чата, notes_list её видит (ядро заметок работает без единого процесса node).
    /// </summary>
    [Fact]
    public async Task ЖивойПуть_СозданиеИЧтениеЗаметок_Работают()
    {
        var (projectId, sessionId) = await CreateProjectWithSessionAsync(Client);

        var created = await CallToolAsync(Client, sessionId, "notes_create",
            new { title = "Заметка из http-тулсета", content = "Текст с [[Заглушкой связи]]" });
        created.GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();
        var note = JsonSerializer.Deserialize<JsonElement>(
            created.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!);
        note.GetProperty("title").GetString().Should().Be("Заметка из http-тулсета");
        note.GetProperty("source").GetString().Should().Be(projectId,
            "источник по умолчанию — notes/ проекта чата-вызывателя");
        var noteId = note.GetProperty("id").GetString()!;

        var listed = await CallToolAsync(Client, sessionId, "notes_list", new { });
        var listText = listed.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        listText.Should().Contain(noteId, "свежесозданная заметка видна в списке того же владельца");
    }

    /// <summary>
    /// Токен B с хвостом сессии владельца A: ни состава, ни вызова — доступ к заметкам
    /// закрывается целиком, а не «пустым списком заметок».
    /// </summary>
    [Fact]
    public async Task ЧужойТокен_НиСоставаНиВызова()
    {
        var (_, sessionIdA) = await CreateProjectWithSessionAsync(Client);
        using var clientB = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var list = await clientB.PostAsJsonAsync($"/mcp/notes/{sessionIdA}",
            new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        list.EnsureSuccessStatusCode();
        JsonSerializer.Deserialize<JsonElement>(await list.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("tools").GetArrayLength()
            .Should().Be(0, "чужая сессия — пустой состав (fail-closed)");

        var call = await CallToolAsync(clientB, sessionIdA, "notes_read", new { id = "что-угодно" });
        call.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!
            .Should().Contain("другому владельцу");
    }
}
