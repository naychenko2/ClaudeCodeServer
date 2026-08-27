using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/chats/{id}/migrate-provider — кнопка «Продолжить на …» при исчерпании лимита.
// Модель тут обязательна: фронт резолвит её из назначения места ДО вызова. Проверка живёт
// у эндпоинта, а не в SessionManager: там пустая модель — законное «По умолчанию» из
// настроек чата (переезд на родной Claude без закреплённой модели).
public class ChatsMigrateProviderTests(TestWebApplicationFactory factory)
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MigrateProvider_ПустаяМодель_400(string? model)
    {
        var id = await CreateChatAsync();

        var resp = await _client.PostAsJsonAsync($"/api/chats/{id}/migrate-provider", new { model });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Не указана модель");
    }
}
