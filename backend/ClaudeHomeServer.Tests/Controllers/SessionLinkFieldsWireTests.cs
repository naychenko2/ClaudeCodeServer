using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Сторож границы сериализации: вычисленные связи «чат ↔ задача» (parentSessionId, taskDone)
// уходят на фронт из ЛЮБОЙ точки отдачи Session, а не только из списочных.
//
// Дефект, который тест закрывает: проекция стояла пер-эндпоинтно и держалась дисциплиной
// вызова — 6 списочных точек её звали, ~20 одиночных (переименование, архив, персона,
// участники, настройки) отдавали Session мимо неё. Фронт (ChatPanel.onSessionUpdated)
// подменяет объект чата ответом одиночного эндпоинта, поэтому чат без parentSessionId
// выпадал из дерева после любой правки настроек. Теперь поля дописывает конвертер типа
// (SessionJsonConverter в опциях MVC) — проверяем это по ФАКТУ HTTP-ответа.
public class SessionLinkFieldsWireTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<string> CreateChatAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    // Чат-исполнитель ВЫПОЛНЕННОЙ задачи, созданной в чате-постановщике: ожидаемые связи —
    // parentSessionId = чат-постановщик, taskDone = true. Задача настоящая (TaskManager из DI),
    // а не стаб: проверяется весь путь конвертер → SessionTaskLinks → ITaskLookup → TaskManager.
    private async Task<(string Author, string Executor)> CreateExecutorChatOfDoneTaskAsync()
    {
        var authorChatId = await CreateChatAsync();
        var ownerId = factory.Services.GetRequiredService<UserStore>()
            .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

        var task = factory.Services.GetRequiredService<TaskManager>().Create(
            projectId: null, ownerId,
            new CreateTaskRequest(Title: "связи чата на wire", Status: TaskItemStatus.Done,
                SourceSessionId: authorChatId));

        var executor = await factory.Services.GetRequiredService<SessionManager>()
            .CreateChatAsync(ownerId, ClaudeMode.Auto, taskExecution: true, taskId: task.Id);

        return (authorChatId, executor.Id);
    }

    private static void ShouldHaveLinks(JsonElement chat, string expectedParent)
    {
        chat.TryGetProperty("parentSessionId", out var parent).Should().BeTrue(
            "поле связи обязано быть в wire — по нему фронт строит дерево чатов");
        parent.GetString().Should().Be(expectedParent);
        chat.TryGetProperty("taskDone", out var done).Should().BeTrue(
            "поле связи обязано быть в wire — по нему работает чип «Завершён»");
        done.GetBoolean().Should().BeTrue();
    }

    private static JsonElement Json(string body) => JsonSerializer.Deserialize<JsonElement>(body);

    [Fact]
    public async Task ОдиночныйGet_ОтдаётСвязи()
    {
        var (author, executor) = await CreateExecutorChatOfDoneTaskAsync();

        var resp = await _client.GetAsync($"/api/chats/{executor}");
        resp.EnsureSuccessStatusCode();

        ShouldHaveLinks(Json(await resp.Content.ReadAsStringAsync()), author);
    }

    [Fact]
    public async Task Переименование_ОтдаётСвязи()
    {
        // Та самая точка дефекта: фронт подменяет объект чата ЭТИМ ответом.
        var (author, executor) = await CreateExecutorChatOfDoneTaskAsync();

        var resp = await _client.PutAsJsonAsync($"/api/chats/{executor}", new { name = "переименован" });
        resp.EnsureSuccessStatusCode();

        var chat = Json(await resp.Content.ReadAsStringAsync());
        chat.GetProperty("name").GetString().Should().Be("переименован");
        ShouldHaveLinks(chat, author);
    }

    [Fact]
    public async Task Архивация_ОтдаётСвязи()
    {
        var (author, executor) = await CreateExecutorChatOfDoneTaskAsync();

        var resp = await _client.PutAsJsonAsync($"/api/chats/{executor}/archived", new { archived = true });
        resp.EnsureSuccessStatusCode();

        ShouldHaveLinks(Json(await resp.Content.ReadAsStringAsync()), author);
    }

    [Fact]
    public async Task СписокЧатов_ОтдаётСвязи()
    {
        var (author, executor) = await CreateExecutorChatOfDoneTaskAsync();

        var resp = await _client.GetAsync("/api/chats");
        resp.EnsureSuccessStatusCode();

        var mine = Json(await resp.Content.ReadAsStringAsync()).EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == executor);
        ShouldHaveLinks(mine, author);
    }

    [Fact]
    public async Task ОбычныйЧатБезЗадачи_СвязиПустые()
    {
        // Поля есть всегда (фронт-тип Session их ждёт), но у чата без задачи — пустые.
        var id = await CreateChatAsync();

        var resp = await _client.GetAsync($"/api/chats/{id}");
        resp.EnsureSuccessStatusCode();
        var chat = Json(await resp.Content.ReadAsStringAsync());

        chat.GetProperty("parentSessionId").ValueKind.Should().Be(JsonValueKind.Null);
        chat.GetProperty("taskDone").GetBoolean().Should().BeFalse();
    }
}
