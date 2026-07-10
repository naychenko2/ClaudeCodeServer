using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Интеграция notes-task-sync через HTTP: чекбокс заметки → задача → двусторонняя
// синхронизация. Проверяет связку NotesController + NoteTaskParser + NoteTaskSyncService
// + TaskManager целиком (юниты покрывают только парсер). Номера строк и контент
// читаем обратно через API — тест устойчив к трансформациям контента при создании.
public class NotesTaskSyncIntegrationTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client;   // testuser — флаг notes включён
    private readonly HttpClient _noFlag;   // seconduser — флаг выключен

    public NotesTaskSyncIntegrationTests(TestWebApplicationFactory factory)
    {
        _client = factory.CreateAuthenticatedClient();
        _noFlag = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        // Синхронизация гейтится зонтичным флагом notes (бывший notes-task-sync
        // влит в него рефакторингом 8b94482) — включаем только основному юзеру
        _client.PutAsJsonAsync("/api/feature-flags/notes", new { enabled = true })
            .GetAwaiter().GetResult().EnsureSuccessStatusCode();
    }

    private static string Url(string id) => $"/api/notes/{Uri.EscapeDataString(id)}";

    private async Task<string> CreateNoteAsync(string title, string content)
    {
        var resp = await _client.PostAsJsonAsync("/api/notes", new { title, content, source = "personal" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<List<JsonElement>> NoteTasksAsync(string id)
    {
        var resp = await _client.GetAsync($"{Url(id)}/tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<string> NoteContentAsync(string id)
    {
        var resp = await _client.GetAsync(Url(id));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("content").GetString()!;
    }

    private async Task<JsonElement> GetTaskAsync(string taskId) =>
        await (await _client.GetAsync($"/api/tasks/{taskId}")).Content.ReadFromJsonAsync<JsonElement>();

    // ─── Гейтинг флага ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NoteTasks_БезФлага_403()
    {
        var id = await CreateNoteAsync("Список", "- [ ] дело");
        (await _noFlag.GetAsync($"{Url(id)}/tasks")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
    }

    // ─── Разбор чекбоксов ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListForNote_РаспознаётЧекбоксыИСрок()
    {
        var id = await CreateNoteAsync("Дела", "- [ ] Полить цветы 📅 2026-07-15\n- [x] Уже сделано");
        var tasks = await NoteTasksAsync(id);

        tasks.Should().HaveCount(2);
        var water = tasks.First(t => t.GetProperty("text").GetString() == "Полить цветы");
        water.GetProperty("done").GetBoolean().Should().BeFalse();
        water.GetProperty("due").GetString().Should().Be("2026-07-15");
        water.GetProperty("taskId").ValueKind.Should().Be(JsonValueKind.Null);   // ещё не промоутнут
    }

    // ─── Промоут ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_СоздаётЗадачу_ИСвязьВидна()
    {
        var id = await CreateNoteAsync("Дела", "- [ ] Полить цветы 📅 2026-07-15");
        var line = (await NoteTasksAsync(id))[0].GetProperty("line").GetInt32();

        var resp = await _client.PostAsJsonAsync($"{Url(id)}/tasks/promote", new { line });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var task = await resp.Content.ReadFromJsonAsync<JsonElement>();
        task.GetProperty("title").GetString().Should().Be("Полить цветы");
        task.GetProperty("dueDate").GetString().Should().Be("2026-07-15");
        var taskId = task.GetProperty("id").GetString();

        // связь отражается в списке чекбоксов
        (await NoteTasksAsync(id))[0].GetProperty("taskId").GetString().Should().Be(taskId);
        // и задача попала в общий список
        var all = await (await _client.GetAsync("/api/tasks")).Content.ReadFromJsonAsync<JsonElement>();
        all.EnumerateArray().Should().Contain(t => t.GetProperty("id").GetString() == taskId);
    }

    [Fact]
    public async Task Promote_Повторно_ВозвращаетТуЖеЗадачу_БезДубля()
    {
        var id = await CreateNoteAsync("Дела", "- [ ] Одна задача");
        var line = (await NoteTasksAsync(id))[0].GetProperty("line").GetInt32();

        var first = (await (await _client.PostAsJsonAsync($"{Url(id)}/tasks/promote", new { line }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var second = (await (await _client.PostAsJsonAsync($"{Url(id)}/tasks/promote", new { line }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        second.Should().Be(first);
        var all = await (await _client.GetAsync("/api/tasks")).Content.ReadFromJsonAsync<JsonElement>();
        all.EnumerateArray().Count(t => t.GetProperty("id").GetString() == first).Should().Be(1);
    }

    // ─── Двусторонняя синхронизация ────────────────────────────────────────────

    [Fact]
    public async Task Toggle_ИзЗаметки_ПравитMdИСтатусЗадачи()
    {
        var id = await CreateNoteAsync("Дела", "- [ ] Дело для тоггла");
        var line = (await NoteTasksAsync(id))[0].GetProperty("line").GetInt32();
        var taskId = (await (await _client.PostAsJsonAsync($"{Url(id)}/tasks/promote", new { line }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var resp = await _client.PostAsJsonAsync($"{Url(id)}/tasks/toggle", new { line, done = true });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await NoteContentAsync(id)).Should().Contain("[x]");
        (await GetTaskAsync(taskId)).GetProperty("status").GetString().Should().Be("done");
    }

    [Fact]
    public async Task ЗавершениеЗадачи_СтавитГалочкуВЗаметке()
    {
        var id = await CreateNoteAsync("Дела", "- [ ] Закрыть через задачу");
        var line = (await NoteTasksAsync(id))[0].GetProperty("line").GetInt32();
        var taskId = (await (await _client.PostAsJsonAsync($"{Url(id)}/tasks/promote", new { line }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // Завершаем задачу с ДРУГОЙ стороны — через tasks-API
        var put = await _client.PutAsJsonAsync($"/api/tasks/{taskId}", new { status = "done" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // Обратная запись поставила галочку в .md
        (await NoteContentAsync(id)).Should().Contain("[x]");
    }

    [Fact]
    public async Task SetDue_ПравитMdИСрокСвязаннойЗадачи()
    {
        var id = await CreateNoteAsync("Дела", "- [ ] Дело без срока");
        var line = (await NoteTasksAsync(id))[0].GetProperty("line").GetInt32();
        var taskId = (await (await _client.PostAsJsonAsync($"{Url(id)}/tasks/promote", new { line }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var resp = await _client.PostAsJsonAsync($"{Url(id)}/tasks/set-due", new { line, due = "2026-08-01" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        (await NoteContentAsync(id)).Should().Contain("📅 2026-08-01");
        (await GetTaskAsync(taskId)).GetProperty("dueDate").GetString().Should().Be("2026-08-01");
    }
}
