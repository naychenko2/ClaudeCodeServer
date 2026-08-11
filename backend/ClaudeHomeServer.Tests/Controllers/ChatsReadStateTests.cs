using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Отметка прочтения чата (PUT /api/chats/{id}/read) — синк непрочитанности между
// устройствами. Один эндпоинт на оба типа сессий (как /parent и /loop); отметка
// не двигает updatedAt — иначе чат прыгал бы в списке и сам метил себя непрочитанным.
public class ChatsReadStateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ChatsReadStateTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private async Task<string> CreateProjectlessChatAsync()
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private async Task<string> CreateProjectSessionAsync()
    {
        var dir = Path.Combine(_factory.TempDir, "readstate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var projectResp = await _client.PostAsJsonAsync("/api/projects", new { name = "ReadState", rootPath = dir });
        projectResp.EnsureSuccessStatusCode();
        var project = JsonSerializer.Deserialize<JsonElement>(await projectResp.Content.ReadAsStringAsync());
        var projectId = project.GetProperty("id").GetString()!;

        var sessionResp = await _client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        sessionResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = JsonSerializer.Deserialize<JsonElement>(await sessionResp.Content.ReadAsStringAsync());
        return session.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetChatAsync(string id)
    {
        var resp = await _client.GetAsync($"/api/chats/{id}");
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MarkRead_ЧатВнеПроекта_204_ИОтметкаВидна()
    {
        var id = await CreateProjectlessChatAsync();
        var before = await GetChatAsync(id);
        var updatedBefore = before.GetProperty("updatedAt").GetDateTime();

        var resp = await _client.PutAsync($"/api/chats/{id}/read", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await GetChatAsync(id);
        after.GetProperty("lastReadAt").GetDateTime().Should().BeOnOrAfter(updatedBefore);
        // Инвариант: отметка прочтения — не активность, updatedAt не двигается
        after.GetProperty("updatedAt").GetDateTime().Should().Be(updatedBefore);
    }

    [Fact]
    public async Task MarkRead_ПроектнаяСессия_204()
    {
        var id = await CreateProjectSessionAsync();

        var resp = await _client.PutAsync($"/api/chats/{id}/read", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GetChatAsync(id)).GetProperty("lastReadAt").ValueKind
            .Should().Be(JsonValueKind.String, "отметка должна сохраниться и для проектной сессии");
    }

    [Fact]
    public async Task MarkRead_ЧужойЧат_404()
    {
        var id = await CreateProjectlessChatAsync();
        using var stranger = _factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

        var resp = await stranger.PutAsync($"/api/chats/{id}/read", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetChatAsync(id)).GetProperty("lastReadAt").ValueKind
            .Should().Be(JsonValueKind.Null, "чужая отметка не должна применяться");
    }

    [Fact]
    public async Task MarkRead_НесуществующийЧат_404()
    {
        var resp = await _client.PutAsync("/api/chats/no-such-chat/read", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
