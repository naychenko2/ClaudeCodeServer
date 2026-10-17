using System.Net;
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
}
