using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

// Интеграционные тесты эндпоинтов продуктовой истории (api/history/*).
// В тестовом окружении нет проектов с git-репозиториями, поэтому коммитов нет —
// GetDay для любой даты возвращает пустой день БЕЗ вызова claude (ветка dayCommits.Count == 0).
public class HistoryControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public HistoryControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ─── Авторизация ────────────────────────────────────────────────────────

    [Fact]
    public async Task Days_БезАвторизации_401()
    {
        using var anon = _factory.CreateClient();
        var response = await anon.GetAsync("/api/history/days");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ClearAll_БезАвторизации_401()
    {
        using var anon = _factory.CreateClient();
        var response = await anon.DeleteAsync("/api/history");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ─── Список дней ────────────────────────────────────────────────────────

    [Fact]
    public async Task Days_СТокеном_ВозвращаетМассив()
    {
        var response = await _client.GetAsync("/api/history/days");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array); // без git-проектов — пустой массив
    }

    // ─── Сводка дня + валидация даты ────────────────────────────────────────

    [Theory]
    [InlineData("notadate")]
    [InlineData("2026-7-1")]     // однозначные месяц/день не проходят \d{2}
    [InlineData("06-07-2026")]   // формат dd-mm-yyyy
    [InlineData("2026-07-1a")]   // лишний символ
    public async Task Day_НевалиднаяДата_400(string date)
    {
        var response = await _client.GetAsync($"/api/history/day/{date}");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Day_ВалиднаяДатаБезКоммитов_ПустойДень()
    {
        // Заведомо давняя дата без коммитов в любом источнике — GetDay вернёт пусто
        // и НЕ полезет в claude (важно: не зависим от локального Changelog:SourceRepoPath)
        var response = await _client.GetAsync("/api/history/day/2000-01-01");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("date").GetString().Should().Be("2000-01-01");
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    // ─── Инвалидация дня ────────────────────────────────────────────────────

    [Fact]
    public async Task InvalidateDay_НевалиднаяДата_400()
    {
        var response = await _client.DeleteAsync("/api/history/day/notadate");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InvalidateDay_ВалиднаяДата_204()
    {
        // Инвалидация несуществующего в кеше дня — идемпотентный no-op, но эндпоинт живой
        var response = await _client.DeleteAsync("/api/history/day/2026-07-01");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Полная очистка ─────────────────────────────────────────────────────

    [Fact]
    public async Task ClearAll_204()
    {
        var response = await _client.DeleteAsync("/api/history");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ─── Счётчик новых коммитов (для бейджа) ────────────────────────────────

    [Fact]
    public async Task NewCount_БезSince_0()
    {
        var response = await _client.GetAsync("/api/history/new-count");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("count").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task NewCount_НевалидныйSince_0()
    {
        var response = await _client.GetAsync("/api/history/new-count?since=not-a-date");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("count").GetInt32().Should().Be(0);
    }
}
