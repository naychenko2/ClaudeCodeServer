using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// GET /api/home/summary × архив (план v4, шаг 4): архив режется на сервере СТРОГО ДО
// .Take(recent) — иначе архивированные свежие чаты занимают слоты и блок «Недавние»
// пустеет; поле archived — готовый bool с сервера (второй копии правила на фронте нет).
// Ветку active сервер не режет: активный архивный чат приходит с archived=true.
public class HomeControllerArchiveTests(TestWebApplicationFactory factory)
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

    [Fact]
    public async Task Summary_АрхивныеНеЗанимаютСлотыНедавних()
    {
        var sessions = factory.Services.GetRequiredService<SessionManager>();
        // Пятеро: чем позже создан, тем свежее UpdatedAt. В архив уйдут ДВА ПОСЛЕДНИХ —
        // без серверной фильтрации именно они заняли бы слоты Take(recent)
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) ids.Add(await CreateChatAsync());
        foreach (var id in ids.TakeLast(2))
        {
            var resp = await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });
            resp.EnsureSuccessStatusCode();
        }

        var respSummary = await _client.GetAsync("/api/home/summary?recent=3");
        respSummary.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await respSummary.Content.ReadAsStringAsync());

        var recent = body.GetProperty("recent");
        recent.EnumerateArray().Should().HaveCount(3,
            "лимит режет уже БЕЗ архивных — блок не пустеет от архивации свежих чатов");
        var recentIds = recent.EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        recentIds.Should().BeEquivalentTo(ids.Take(3),
            "слоты достались старым живым чатам, а не спрятанным");
        recent.EnumerateArray().Should().OnlyContain(e => !e.GetProperty("archived").GetBoolean());

        // Сами архивные живы как сущности: GetOwned их отдаёт, признак на месте
        foreach (var id in ids.TakeLast(2))
            sessions.GetById(id)!.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task Summary_ПолеArchivedСчитаетСервер_НаГотовомBool()
    {
        var sessions = factory.Services.GetRequiredService<SessionManager>();
        var id = await CreateChatAsync();
        var resp = await _client.PutAsJsonAsync($"/api/chats/{id}/archived", new { archived = true });
        resp.EnsureSuccessStatusCode();

        // Ветку active сервер не режет: архивный чат с живым статусом приходит в active
        // с готовым archived=true — фронт не пересчитывает updatedAt <= archivedAt
        sessions.GetById(id)!.Status = SessionStatus.Working;
        var respSummary = await _client.GetAsync("/api/home/summary");
        var body = JsonSerializer.Deserialize<JsonElement>(await respSummary.Content.ReadAsStringAsync());

        var active = body.GetProperty("active").EnumerateArray()
            .SingleOrDefault(e => e.GetProperty("id").GetString() == id);
        active.ValueKind.Should().Be(JsonValueKind.Object, "ветка active архив не режет");
        active.GetProperty("archived").GetBoolean().Should().BeTrue();
        active.TryGetProperty("archivedAt", out _).Should().BeFalse(
            "в DTO уходит готовый bool, а не даты для пересчёта на фронте");
    }
}
