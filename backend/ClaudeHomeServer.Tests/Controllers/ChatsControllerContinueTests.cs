using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/continue — «Продолжить в новом чате». Оркестрация покрыта
// ChatContinueServiceTests; здесь контракт эндпоинта: 200 {newSessionId, handoffNoteId},
// 400 (нечего передавать), 404 (чужой/несуществующий), 409 (чат занят). Настоящую отправку
// (процесс CLI) подменяет шов ChatContinueService.SendOverride.
public class ChatsControllerContinueTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<string> CreateSessionAsync()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_continue_" + Guid.NewGuid().ToString("N"))).FullName;
        var proj = await _client.PostAsJsonAsync("/api/projects", new { name = "ContinueProject", rootPath = dir });
        proj.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await proj.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        var resp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // Живая запись сессии (приватный реестр) — накопитель хода и очередь сообщений
    private object EntryOf(string sessionId)
    {
        var sessions = factory.Services.GetRequiredService<SessionManager>();
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)field.GetValue(sessions)!)[sessionId]!;
    }

    private void SeedAgentTurn(string sessionId)
    {
        List<StoredMessage> turns =
        [
            new StoredUserMessage("вопрос"), new StoredTextMessage("ответ"), new StoredResultMessage("success", 1, 1),
        ];
        var entry = EntryOf(sessionId);
        entry.GetType().GetField("Accumulator")!.SetValue(entry, new TurnAccumulator(turns, "csid-" + sessionId));
    }

    private void ReplyWith(string note) =>
        factory.Services.GetRequiredService<ChatContinueService>().SendOverride = (_, _, _, sys) =>
            Task.FromResult<SendAndWaitResult>(sys
                ? new SendAndWaitResult.Completed(new TurnResult(note, 1, null))
                : new SendAndWaitResult.Running());

    [Fact]
    public async Task Продолжение_УспешныйПуть_200_ФормаОтвета()
    {
        var id = await CreateSessionAsync();
        SeedAgentTurn(id);
        ReplyWith("## Цель\nпродолжить");

        var resp = await _client.PostAsJsonAsync($"/api/chats/{id}/continue", new { note = "начни с тестов" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("newSessionId").GetString().Should().NotBeNullOrEmpty().And.NotBe(id);
        body.TryGetProperty("handoffNoteId", out _).Should().BeTrue("поле есть в контракте, даже если заметки нет");
    }

    [Fact]
    public async Task Продолжение_ТелоНеПередано_200()
    {
        var id = await CreateSessionAsync();
        SeedAgentTurn(id);
        ReplyWith("## Цель\nпродолжить");

        var resp = await _client.PostAsync($"/api/chats/{id}/continue", JsonContent.Create(new { }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "note необязательна");
    }

    [Fact]
    public async Task Продолжение_НетХодовАгента_400()
    {
        var id = await CreateSessionAsync();
        ReplyWith("не должно вызываться");

        var resp = await _client.PostAsJsonAsync($"/api/chats/{id}/continue", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("нет ходов агента");
    }

    [Fact]
    public async Task Продолжение_ЧатЗанят_409_СТекстомОшибки()
    {
        var id = await CreateSessionAsync();
        SeedAgentTurn(id);
        ReplyWith("не должно вызываться");
        var entry = EntryOf(id);
        var pending = (System.Collections.IList)entry.GetType().GetField("Pending")!.GetValue(entry)!;
        pending.Add(new SessionManager.QueuedMessage(Guid.NewGuid().ToString("N"), "в очереди", null, null,
            0, DateTime.UtcNow, Kind: SessionManager.PendingKind.User));

        var resp = await _client.PostAsJsonAsync($"/api/chats/{id}/continue", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("идёт ход");
    }

    [Fact]
    public async Task Продолжение_НесуществующийЧат_404()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats/no-such-chat/continue", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Продолжение_ЧужойЧат_404()
    {
        var id = await CreateSessionAsync();
        SeedAgentTurn(id);
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await stranger.PostAsJsonAsync($"/api/chats/{id}/continue", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
