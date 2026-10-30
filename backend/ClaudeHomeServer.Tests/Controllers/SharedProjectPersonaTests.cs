using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Собеседник чата общего проекта (shared-projects, S2a-8). Глобальная персона чату общего
/// проекта не назначается: 400 с кодом global_persona_in_shared — рамка хода в общем проекте
/// узкая для всех, включая арендатора (TurnFrame, решение 29.10), а глобальная персона —
/// личное пространство арендатора. Проектная персона того же проекта, личный проект и чат
/// вне проекта — прежнее поведение.
/// </summary>
public class SharedProjectPersonaTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Проект владельца с чатом в нём; memberRole = null — участников нет, т.е. проект
    /// личный. Иначе — общий проект с участником этой роли.</summary>
    private async Task<(string ProjectId, string ChatId)> ProjectWithChatAsync(string? memberRole)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        if (memberRole is not null) await SetFlagAsync(Member());

        var dir = Path.Combine(_factory.TempDir, "spp_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Персона" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (memberRole is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role = memberRole }))
                .EnsureSuccessStatusCode();

        var chat = await owner.PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        chat.EnsureSuccessStatusCode();
        return (projectId, IdOf(await chat.Content.ReadAsStringAsync()));
    }

    private static async Task<string> CreatePersonaAsync(HttpClient client, string name,
        string? scope = null, string? projectId = null)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope, projectId });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> AssignPersonaAsync(
        HttpClient client, string projectId, string chatId, string personaId) =>
        await client.PostAsJsonAsync($"/api/projects/{projectId}/sessions/{chatId}/persona", new { personaId });

    private static async Task<JsonElement> ChatAsync(HttpClient client, string chatId)
    {
        var r = await client.GetAsync($"/api/chats/{chatId}");
        r.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
    }

    // ── отказ: глобальная персона в общем чате ───────────────────────────────────────

    [Fact]
    public async Task ГлобальнаяПерсонаЧатуОбщегоПроекта_400СКодомИЧатНеМеняется()
    {
        var (projectId, chatId) = await ProjectWithChatAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");
        var before = (await ChatAsync(Owner(), chatId)).GetProperty("personaId").GetString();

        var r = await AssignPersonaAsync(Owner(), projectId, chatId, personaId);

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());
        body.GetProperty("code").GetString().Should().Be(SessionManager.GlobalPersonaInSharedException.Code,
            "фронт различает этот отказ кодом, а не разбором текста");
        // Отказ ничего не пишет: собеседник чата остаётся прежний.
        var after = (await ChatAsync(Owner(), chatId)).GetProperty("personaId").GetString();
        after.Should().Be(before, "отказ не трогает собеседника чата");
    }

    // ── прежнее поведение ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ПроектнаяПерсонаТогоЖеПроекта_Разрешена()
    {
        var (projectId, chatId) = await ProjectWithChatAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(),
            "Проектная-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);

        var r = await AssignPersonaAsync(Owner(), projectId, chatId, personaId);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ChatAsync(Owner(), chatId)).GetProperty("personaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task ГлобальнаяПерсонаВЛичномПроекте_Разрешена()
    {
        // Тот же код, тот же путь REST — но без участников: рамка широкая, отказа нет.
        var (projectId, chatId) = await ProjectWithChatAsync(memberRole: null);
        var personaId = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");

        var r = await AssignPersonaAsync(Owner(), projectId, chatId, personaId);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ChatAsync(Owner(), chatId)).GetProperty("personaId").GetString().Should().Be(personaId);
    }

    [Fact]
    public async Task ГлобальнаяПерсонаЧатуВнеПроекта_Разрешена()
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        var chatResp = await owner.PostAsJsonAsync("/api/chats", new { mode = "auto" });
        chatResp.EnsureSuccessStatusCode();
        var chatId = IdOf(await chatResp.Content.ReadAsStringAsync());
        var personaId = await CreatePersonaAsync(owner, "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");

        var r = await owner.PostAsJsonAsync($"/api/chats/{chatId}/persona", new { personaId });

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ChatAsync(owner, chatId)).GetProperty("personaId").GetString().Should().Be(personaId);
    }
}
