using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/chats/{id}/history: без after — прежний плоский массив, с after — конверт докачки
// (Перф-2 B5). Логика курсора общая с проектной ручкой (ChatHistoryTail), здесь проверяется
// проводка и изоляция per-owner.
[Trait("Category", "Host")]
public class ChatsHistoryTailTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private static List<StoredMessage> TwoTurns() =>
    [
        new StoredUserMessage("первый"),
        new StoredTextMessage("ответ 1"),
        new StoredResultMessage("success", 10, 1),
        new StoredUserMessage("второй"),
        new StoredTextMessage("ответ 2"),
        new StoredResultMessage("success", 20, 2),
    ];

    // Посев как в SessionsControllerTests: история кладётся под csid ДО создания чата, а чат
    // создаётся с resumeSessionId — аккумулятор поднимает её с диска, и ручка отдаёт именно её
    private async Task<string> CreateChatWithHistoryAsync()
    {
        var csid = "chat-tail-" + Guid.NewGuid().ToString("N")[..16];
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ChatHistoryService>().SaveAsync(csid, TwoTurns());

        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto", resumeSessionId = csid });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var resp = await _client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task БезКурсора_ПлоскийМассив()
    {
        var id = await CreateChatWithHistoryAsync();

        var body = await GetJsonAsync($"/api/chats/{id}/history");

        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task КурсорСоВпавшимОтпечатком_ТолькоХвост()
    {
        var id = await CreateChatWithHistoryAsync();
        var fp = ChatHistoryTail.PrefixFingerprint(TwoTurns(), 2);

        var body = await GetJsonAsync($"/api/chats/{id}/history?after=2&fp={fp}");

        body.GetProperty("reset").GetBoolean().Should().BeFalse();
        body.GetProperty("messages").GetArrayLength().Should().Be(3);
        body.GetProperty("anchorIndex").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task КурсорВКонцеИстории_ПустойХвост()
    {
        var id = await CreateChatWithHistoryAsync();
        var fp = ChatHistoryTail.PrefixFingerprint(TwoTurns(), 5);

        var body = await GetJsonAsync($"/api/chats/{id}/history?after=5&fp={fp}");

        body.GetProperty("reset").GetBoolean().Should().BeFalse();
        body.GetProperty("messages").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task НесходящийсяОтпечаток_ResetИПолнаяИстория()
    {
        var id = await CreateChatWithHistoryAsync();

        var body = await GetJsonAsync($"/api/chats/{id}/history?after=2&fp=0000000000000000");

        body.GetProperty("reset").GetBoolean().Should().BeTrue();
        body.GetProperty("messages").GetArrayLength().Should().Be(6);
    }

    [Fact]
    public async Task МинусОдин_ResetИЯкорьДляПервойЗагрузки()
    {
        var id = await CreateChatWithHistoryAsync();

        var body = await GetJsonAsync($"/api/chats/{id}/history?after=-1");

        body.GetProperty("reset").GetBoolean().Should().BeTrue();
        body.GetProperty("messages").GetArrayLength().Should().Be(6);
        body.GetProperty("anchorIndex").GetInt32().Should().Be(5);
        body.GetProperty("anchorFp").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ЧужойИлиНесуществующийЧат_404_ДажеСКурсором()
    {
        var resp = await _client.GetAsync("/api/chats/нет-такого/history?after=2&fp=x");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
