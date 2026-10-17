using System.Collections;
using System.Net;
using System.Reflection;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/handoff — перенос в новый чат, холодный путь (docs/features/chat-handoff.md).
// Сервис покрыт ChatHandoffServiceTests; здесь — контроллерные аспекты: владение и для чата вне
// проекта, и для проектной сессии (404 чужому, 200 владельцу), форма ответа, отказы 409.
// Модель — дефолтная заглушка фабрики (ответ «[]»): записка непустая, вызов без сети.
public class ChatsControllerHandoffTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private SessionManager Sessions => factory.Services.GetRequiredService<SessionManager>();

    private HttpClient Stranger => factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string OwnerId => factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private async Task<string> SaveHistoryAsync()
    {
        var csid = "handoff-" + Guid.NewGuid().ToString("N")[..12];
        await factory.Services.GetRequiredService<ChatHistoryService>().SaveAsync(csid,
        [
            new StoredUserMessage("Поправь значки проектов"),
            new StoredTextMessage("Поправил"),
        ]);
        return csid;
    }

    private async Task<Session> ProjectChatAsync()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_handoff_" + Guid.NewGuid().ToString("N"))).FullName;
        var resp = await _client.PostAsJsonAsync("/api/projects", new { name = "HandoffProject", rootPath = dir });
        resp.EnsureSuccessStatusCode();
        var projectId = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        return await Sessions.CreateAsync(projectId, ClaudeMode.Auto, resumeSessionId: await SaveHistoryAsync());
    }

    private async Task<Session> PlainChatAsync() =>
        await Sessions.CreateChatAsync(OwnerId, ClaudeMode.Auto, resumeSessionId: await SaveHistoryAsync());

    private Task<HttpResponseMessage> PostHandoff(HttpClient client, string id) =>
        client.PostAsync($"/api/chats/{id}/handoff", null);

    [Fact]
    public async Task ПроектныйЧат_ЧужомуНеНайден_Владельцу200()
    {
        var chat = await ProjectChatAsync();

        (await PostHandoff(Stranger, chat.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var resp = await PostHandoff(_client, chat.Id);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var newId = body.GetProperty("chatId").GetString();
        newId.Should().NotBeNullOrEmpty().And.NotBe(chat.Id);
        body.GetProperty("projectId").GetString().Should().Be(chat.ProjectId);
        body.GetProperty("draft").GetString().Should().EndWith($"Исходный чат: {chat.Id} — детали через chats_history.");
        Sessions.GetById(newId!).Should().NotBeNull();
    }

    [Fact]
    public async Task ЧатВнеПроекта_ЧужомуНеНайден_Владельцу200()
    {
        var chat = await PlainChatAsync();

        (await PostHandoff(Stranger, chat.Id)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var resp = await PostHandoff(_client, chat.Id);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("projectId").ValueKind.Should().Be(JsonValueKind.Null);
        Sessions.GetById(body.GetProperty("chatId").GetString()!)!.OwnerId.Should().Be(OwnerId);
    }

    [Fact]
    public async Task НесуществующийЧат_404() =>
        (await PostHandoff(_client, "no-such-chat")).StatusCode.Should().Be(HttpStatusCode.NotFound);

    [Theory]
    [InlineData("group", "Групповой чат нельзя переносить")]
    [InlineData("team", "«Командная реализация» нельзя переносить")]
    [InlineData("worktree", "отдельном рабочем дереве нельзя переносить")]
    public async Task ГруппаШтабWorktree_409(string kind, string expected)
    {
        var chat = await PlainChatAsync();
        var live = Sessions.GetById(chat.Id)!;
        switch (kind)
        {
            case "group": live.Participants = ["p1", "p2"]; break;
            case "team": live.TeamImplement = new SessionTeamImplement(); break;
            case "worktree": live.WorktreePath = Path.GetTempPath(); break;
        }
        var before = Sessions.GetAll().Count;

        var resp = await PostHandoff(_client, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain(expected);
        Sessions.GetAll().Count.Should().Be(before, "отказ не создаёт чат");
    }

    [Fact]
    public async Task ПустойЧат_409()
    {
        var chat = await Sessions.CreateChatAsync(OwnerId, ClaudeMode.Auto);

        var resp = await PostHandoff(_client, chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("переносить нечего");
    }

    // ─── Тёплый путь: записку пишет сам чат своим ходом ─────────────────────────

    private Task<HttpResponseMessage> PostWarm(string id) =>
        _client.PostAsJsonAsync($"/api/chats/{id}/handoff", new { warm = true });

    private FakeLlmSessionAdapter Adapter(string chatId) => factory.LlmAdapters.Adapters[chatId];

    // Живой аккумулятор и OnMessageAsync — приватное ядро; события хода подаём мимо CLI
    // (тот же приём, что в ModuleRecordFreshChatTests)
    private object Entry(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((IDictionary)field.GetValue(Sessions)!)[sessionId]!;
    }

    private async Task EmitAsync(string sessionId, ServerMessage msg)
    {
        var entry = Entry(sessionId);
        var acc = (TurnAccumulator?)entry.GetType().GetField("Accumulator")!.GetValue(entry);
        if (acc is null)
        {
            await (Task)typeof(SessionManager).GetMethod("EnsureAccumulatorAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Sessions, [entry])!;
            acc = (TurnAccumulator)entry.GetType().GetField("Accumulator")!.GetValue(entry)!;
        }
        await (Task)typeof(SessionManager).GetMethod("OnMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Sessions, [sessionId, acc, msg, 0L])!;
    }

    // Ход передачи ушёл в адаптер (ожидатель выставлен) — можно отвечать за модель
    private async Task WaitWarmTurnAsync(string chatId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!(factory.LlmAdapters.Adapters.TryGetValue(chatId, out var a) && a.SentMessages.Count > 0))
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("ход передачи не ушёл в адаптер");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Тёплый_УспешныйХод_ЗапискаЧата_ВЧерновике()
    {
        var chat = await PlainChatAsync();
        var mode = chat.Mode;
        var before = Sessions.GetAll().Count;

        var post = PostWarm(chat.Id);
        await WaitWarmTurnAsync(chat.Id);
        Adapter(chat.Id).SentMessages.Should().ContainSingle()
            .Which.Should().Contain("Не вызывай инструменты").And.Contain("### Следующий шаг");
        await EmitAsync(chat.Id, new TextDeltaMessage("### Цель\nтёплая записка"));
        await EmitAsync(chat.Id, new ResultMessage("success", 10, 1, null, null));
        var resp = await post;

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("warm").GetBoolean().Should().BeTrue();
        body.GetProperty("draft").GetString().Should().Contain("### Цель\nтёплая записка")
            .And.EndWith($"Исходный чат: {chat.Id} — детали через chats_history.");
        Sessions.GetAll().Count.Should().Be(before + 1);
        Sessions.GetById(chat.Id)!.Mode.Should().Be(mode, "режим чата ходом передачи не меняется");
    }

    [Theory]
    [InlineData("error")]
    [InlineData("result-error")]
    [InlineData("interrupted")]
    [InlineData("empty")]
    public async Task Тёплый_ОшибкаХода_502_ЧатНеСоздан(string kind)
    {
        var chat = await PlainChatAsync();
        var before = Sessions.GetAll().Count;

        var post = PostWarm(chat.Id);
        await WaitWarmTurnAsync(chat.Id);
        switch (kind)
        {
            case "error":
                await EmitAsync(chat.Id, new ErrorMessage("API упал"));
                break;
            case "result-error":
                await EmitAsync(chat.Id, new TextDeltaMessage("ползаписки"));
                await EmitAsync(chat.Id, new ResultMessage("error_during_execution", 10, 1, null, null));
                break;
            case "interrupted":
                await EmitAsync(chat.Id, new TextDeltaMessage("ползаписки"));
                await EmitAsync(chat.Id, new ExitedMessage());
                break;
            case "empty":
                await EmitAsync(chat.Id, new ResultMessage("success", 10, 1, null, null));
                break;
        }
        var resp = await post;

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        if (kind == "error") (await resp.Content.ReadAsStringAsync()).Should().Contain("API упал");
        Sessions.GetAll().Count.Should().Be(before, "без записки новый чат не создаётся");
    }

    // Любое невыполненное предусловие — тихо холодным путём: 200, warm=false, хода в чате нет
    [Theory]
    [InlineData("working")]
    [InlineData("waiting")]
    [InlineData("workloop")]
    [InlineData("bg-agents")]
    [InlineData("archived")]
    public async Task Тёплый_ПредусловиеНеВыполнено_ХолодныйПуть(string kind)
    {
        var chat = await PlainChatAsync();
        var live = Sessions.GetById(chat.Id)!;
        switch (kind)
        {
            case "working": live.Status = SessionStatus.Working; break;
            case "waiting": live.Status = SessionStatus.Waiting; break;
            case "workloop": live.WorkLoop = new SessionWorkLoop(); break;
            case "bg-agents": Adapter(chat.Id).BgAlive = true; break;
            case "archived":
                live.UpdatedAt = DateTime.UtcNow.AddHours(-1);
                live.ArchivedAt = DateTime.UtcNow;
                break;
        }

        var resp = await PostWarm(chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("warm").GetBoolean().Should().BeFalse("ушли на холодный путь");
        Adapter(chat.Id).SentMessages.Should().BeEmpty("ход передачи в чат не подавался");
    }

    [Fact]
    public async Task Тёплый_ГрупповойЧат_ТотЖеОтказ409()
    {
        var chat = await PlainChatAsync();
        Sessions.GetById(chat.Id)!.Participants = ["p1", "p2"];

        var resp = await PostWarm(chat.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Adapter(chat.Id).SentMessages.Should().BeEmpty();
    }
}
