using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// POST /api/projects/{id}/archive-run и гейт первого прохода у PUT /api/projects/{id}/archive-days:
// «Сохранить» в настройках проекта = согласие на правило (снимает гейт фонового тика) и сразу
// убирает залежи ЭТОГО проекта — отдельной кнопки «Применить сейчас» у проекта нет.
public class ProjectArchiveRunEndpointTests(TestWebApplicationFactory factory)
    : IClassFixture<TestWebApplicationFactory>
{
    private readonly HttpClient _client = factory.CreateAuthenticatedClient();

    [Fact]
    public async Task Проход_УбираетОстывшиеЧатыПроекта()
    {
        await SetFlagAsync(true);
        var userId = await UserIdAsync();
        var projectId = await CreateProjectAsync(_client, "Проект прохода архива");
        SetPersonalDays(userId, null);
        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/archive-days", new { days = 7 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var stale = await NewStaleChatAsync(projectId, ageDays: 30);
        var fresh = await Sessions().CreateAsync(projectId, ClaudeMode.Auto);

        var resp = await _client.PostAsync($"/api/projects/{projectId}/archive-run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("archived").GetInt32().Should().Be(1);
        var batchId = body.GetProperty("batchId").GetString();
        batchId.Should().NotBeNullOrEmpty();
        stale.IsArchived.Should().BeTrue();
        stale.ArchivedBy.Should().Be("rule");
        stale.ArchiveBatchId.Should().Be(batchId);
        fresh.IsArchived.Should().BeFalse("свежий чат порог не прошёл");
    }

    [Fact]
    public async Task Проход_ПорогНеЗаданНигде_Ноль()
    {
        await SetFlagAsync(true);
        var userId = await UserIdAsync();
        SetPersonalDays(userId, null);
        var projectId = await CreateProjectAsync(_client, "Проект без порога архива");
        var stale = await NewStaleChatAsync(projectId, ageDays: 100);

        var resp = await _client.PostAsync($"/api/projects/{projectId}/archive-run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "нет порога — не ошибка, а пустой проход");
        var body = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        body.GetProperty("archived").GetInt32().Should().Be(0);
        body.GetProperty("batchId").ValueKind.Should().Be(JsonValueKind.Null);
        stale.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task Проход_ЧужойПроект_404()
    {
        await SetFlagAsync(true);
        var stranger = factory.CreateAuthenticatedClient(
            TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);
        var projectId = await CreateProjectAsync(stranger, "Чужой проект прохода архива");

        var resp = await _client.PostAsync($"/api/projects/{projectId}/archive-run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Проход_ФлагВыключен_400()
    {
        await SetFlagAsync(true);
        var projectId = await CreateProjectAsync(_client, "Проект прохода без флага");
        await SetFlagAsync(false);

        var resp = await _client.PostAsync($"/api/projects/{projectId}/archive-run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Автоправило архива выключено");
    }

    // Согласие на правило — сохранение порога проекта: без снятия гейта фоновый тик
    // обходил бы владельца стороной (пока не нажата кнопка по всем сферам)
    [Fact]
    public async Task СохранениеПорогаПроекта_СнимаетГейтПервогоПрохода()
    {
        await SetFlagAsync(true);
        var userId = await UserIdAsync();
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetArchiveRuleFirstRunAt(userId, null);
        var projectId = await CreateProjectAsync(_client, "Проект согласия на архив");

        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/archive-days", new { days = 14 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        users.GetById(userId)!.ArchiveRuleFirstRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task СбросПорогаПроекта_ГейтНеСнимает()
    {
        await SetFlagAsync(true);
        var userId = await UserIdAsync();
        var users = factory.Services.GetRequiredService<UserStore>();
        users.SetArchiveRuleFirstRunAt(userId, null);
        var projectId = await CreateProjectAsync(_client, "Проект сброса порога архива");

        (await _client.PutAsJsonAsync($"/api/projects/{projectId}/archive-days", new { days = (int?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        users.GetById(userId)!.ArchiveRuleFirstRunAt.Should().BeNull(
            "сброс порога — не согласие на правило");
    }

    // --- Помощники ---

    private SessionManager Sessions() => factory.Services.GetRequiredService<SessionManager>();

    private async Task SetFlagAsync(bool enabled) =>
        (await _client.PutAsJsonAsync($"/api/feature-flags/{FeatureFlagKeys.ChatAutoArchive}", new { enabled }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

    private void SetPersonalDays(string userId, int? days) =>
        factory.Services.GetRequiredService<UserStore>().SetArchiveAfterDays(userId, days);

    private async Task<string> UserIdAsync()
    {
        var me = JsonSerializer.Deserialize<JsonElement>(
            await (await _client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync());
        return me.GetProperty("userId").GetString()!;
    }

    private static async Task<string> CreateProjectAsync(HttpClient client, string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "ccs_arch_run_" + Guid.NewGuid().ToString("N"))).FullName;
        var resp = await client.PostAsJsonAsync("/api/projects", new { name, rootPath = dir });
        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    private async Task<Session> NewStaleChatAsync(string projectId, int ageDays)
    {
        var chat = await Sessions().CreateAsync(projectId, ClaudeMode.Auto);
        chat.UpdatedAt = DateTime.UtcNow - TimeSpan.FromDays(ageDays);
        return chat;
    }
}
