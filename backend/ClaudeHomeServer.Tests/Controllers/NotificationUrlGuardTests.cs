using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S7: url уведомления пишет модель (MCP notifications_create и REST POST /api/notifications,
/// куда ходит stdio-ветка сервисным токеном). Принимается только путь приложения — чужой
/// адрес отклоняется, иначе клик по «Одобрите шаг» уводил бы на фишинговую страницу.
/// </summary>
public class NotificationUrlGuardTests : IDisposable
{
    private readonly TestWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    public static TheoryData<string> ForeignUrls() =>
    [
        "https://evil.example/phish",
        "//evil.example",
        "javascript:alert(1)",
        "\\\\evil.example",
        "/\t/evil.example",
        "#//evil.example",
    ];

    private static async Task<string> CreateSessionAsync(HttpClient client)
    {
        var project = await client.PostAsJsonAsync("/api/projects", new { name = $"notif-{Guid.NewGuid():N}" });
        project.EnsureSuccessStatusCode();
        var projectId = (await project.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var session = await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "acceptEdits" });
        session.EnsureSuccessStatusCode();
        return (await session.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> CallCreateAsync(HttpClient client, string sessionId, string url)
    {
        var resp = await client.PostAsJsonAsync($"/mcp/notifications/{sessionId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new
            {
                name = "notifications_create",
                arguments = new { title = "Одобрите шаг процесса", body = "Нажмите, чтобы продолжить", url },
            },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result");
    }

    private static async Task<int> TotalCountAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/notifications")).GetProperty("totalCount").GetInt32();

    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;

    private static bool IsError(JsonElement result) =>
        result.TryGetProperty("isError", out var e) && e.GetBoolean();

    [Theory]
    [MemberData(nameof(ForeignUrls))]
    public async Task McpCreate_ForeignUrl_Rejected(string url)
    {
        var client = _factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);
        var before = await TotalCountAsync(client);

        var result = await CallCreateAsync(client, sessionId, url);

        IsError(result).Should().BeTrue("чужой адрес в уведомлении — фишинг");
        Text(result).Should().Contain("путём приложения");
        (await TotalCountAsync(client)).Should().Be(before, "уведомление с чужим url не создаётся");
    }

    [Theory]
    [InlineData("#/chats/42")]
    [InlineData("/notifications")]
    public async Task McpCreate_AppRelativeUrl_Kept(string url)
    {
        var client = _factory.CreateAuthenticatedClient();
        var sessionId = await CreateSessionAsync(client);

        var result = await CallCreateAsync(client, sessionId, url);

        IsError(result).Should().BeFalse(Text(result));
        JsonSerializer.Deserialize<JsonElement>(Text(result)).GetProperty("url").GetString().Should().Be(url);
    }

    [Theory]
    [MemberData(nameof(ForeignUrls))]
    public async Task RestCreate_ForeignUrl_Rejected(string url)
    {
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/api/notifications", new { title = "Одобрите", body = "шаг", url });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RestCreate_AppRelativeUrl_Created()
    {
        var client = _factory.CreateAuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/api/notifications",
            new { title = "Готово", body = "Задача выполнена", url = "#/chats/7" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString().Should().Be("#/chats/7");
    }

    [Theory]
    [InlineData("#/chats/1", true)]
    [InlineData("/chats/1", true)]
    [InlineData("chats/1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAppRelative_Shape(string? url, bool expected) =>
        NotificationUrls.IsAppRelative(url).Should().Be(expected);
}
