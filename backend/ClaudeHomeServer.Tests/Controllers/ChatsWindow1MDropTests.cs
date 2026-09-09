using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/window-1m/drop — «продолжить в стандартном окне 200 тысяч токенов»
// под карточкой отказа Window1MUnavailable. Единственный путь, которым суффикс [1m] снимается
// с чата: автоматического среза в ходе больше нет (тихая деградация длинного разговора в 200K
// — мина), поэтому решение принимает человек, а сервер только исполняет.
public class ChatsWindow1MDropTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    private async Task<string> CreateChatAsync(string? model)
    {
        var resp = await _client.PostAsJsonAsync("/api/chats", new { mode = "auto", model });
        resp.EnsureSuccessStatusCode();
        var chat = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        return chat.GetProperty("id").GetString()!;
    }

    private static SessionManager SessionsOf(TestWebApplicationFactory f) =>
        f.Services.GetRequiredService<SessionManager>();

    [Fact]
    public async Task ЧатНаОкне1M_СуффиксСнят_ОстаётсяБазовыйАлиас()
    {
        var id = await CreateChatAsync("opus[1m]");

        var resp = await _client.PostAsync($"/api/chats/{id}/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        SessionsOf(factory).GetById(id)!.Model.Should().Be("opus",
            "базовый алиас закрепляется в чате явно — иначе следующий ход вернул бы окно 1M");
    }

    [Fact]
    public async Task ЧатБезОкна1M_400_СниматьНечего()
    {
        var id = await CreateChatAsync("sonnet");

        var resp = await _client.PostAsync($"/api/chats/{id}/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        SessionsOf(factory).GetById(id)!.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task ЧужойЧат_404()
    {
        var resp = await _client.PostAsync("/api/chats/нет-такого/window-1m/drop", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
