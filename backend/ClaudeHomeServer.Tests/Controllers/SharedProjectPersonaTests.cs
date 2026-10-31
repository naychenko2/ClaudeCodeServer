using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Собеседник чата общего проекта (shared-projects, S2a-8 + доработка R-S2a-8). Глобальная
/// персона чату общего проекта не назначается ни в одной точке назначения: ни смена собеседника,
/// ни создание чата с явной personaId, ни чат от лица персоны с projectId общего проекта, ни
/// групповой чат (создание и смена состава) — везде 400 с кодом global_persona_in_shared.
/// Рамка хода в общем проекте узкая для всех, включая арендатора (TurnFrame, решение 29.10),
/// а глобальная персона — личное пространство арендатора. Проектная персона того же проекта,
/// личный проект и чат вне проекта — прежнее поведение.
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

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    /// <summary>id чатов проекта — чтобы проверить, что отказ не оставил нового чата.</summary>
    private static async Task<List<string>> ProjectChatIdsAsync(HttpClient client, string projectId) =>
        (await BodyAsync(await client.GetAsync($"/api/projects/{projectId}/sessions")))
            .EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToList();

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

    [Fact]
    public async Task СозданиеЧатаОбщегоПроектаСГлобальнойПерсоной_400СКодомИЧатНеСоздан()
    {
        var (projectId, _) = await ProjectWithChatAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");
        var before = await ProjectChatIdsAsync(Owner(), projectId);

        var r = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto", personaId });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(SessionManager.GlobalPersonaInSharedException.Code,
            "тот же машинный код, что при смене собеседника");
        (await ProjectChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказ не оставил в проекте нового чата");
    }

    [Fact]
    public async Task ЧатОтЛицаГлобальнойПерсоныВОбщемПроекте_400СКодомИЧатНеСоздан()
    {
        var (projectId, _) = await ProjectWithChatAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");
        var before = await ProjectChatIdsAsync(Owner(), projectId);

        var r = await Owner().PostAsJsonAsync($"/api/personas/{personaId}/chats", new { projectId });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(SessionManager.GlobalPersonaInSharedException.Code);
        (await ProjectChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказ не оставил в проекте нового чата");
    }

    [Fact]
    public async Task McpChatsCreateВОбщемПроектеСГлобальнойПерсоной_ОтказСКодомИЧатНеСоздан()
    {
        var (projectId, callerChatId) = await ProjectWithChatAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");
        var before = await ProjectChatIdsAsync(Owner(), projectId);

        var resp = await Owner().AsCaller(callerChatId).PostAsJsonAsync($"/mcp/wsp/{callerChatId}", new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name = "chats_create", arguments = new { projectId, personaId } },
        });
        resp.EnsureSuccessStatusCode();
        var result = (await BodyAsync(resp)).GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        JsonSerializer.Deserialize<JsonElement>(text).GetProperty("code").GetString()
            .Should().Be(SessionManager.GlobalPersonaInSharedException.Code);
        (await ProjectChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказ не оставил в проекте нового чата");
    }

    [Fact]
    public async Task ГрупповойЧатОбщегоПроектаСГлобальнымУчастником_400СКодомИЧатНеСоздан()
    {
        var (projectId, _) = await ProjectWithChatAsync("editor");
        var lead = await CreatePersonaAsync(Owner(), "Проектная-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var global = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");
        var before = await ProjectChatIdsAsync(Owner(), projectId);

        var r = await Owner().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { lead, global } });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(SessionManager.GlobalPersonaInSharedException.Code);
        (await ProjectChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказ не оставил в проекте нового чата");
    }

    [Fact]
    public async Task СменаСоставаГрупповогоЧатаОбщегоПроекта_ГлобальнаяПерсона_400СКодомИСоставНеМеняется()
    {
        var (projectId, _) = await ProjectWithChatAsync("editor");
        var p1 = await CreatePersonaAsync(Owner(), "Проектная-1-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var p2 = await CreatePersonaAsync(Owner(), "Проектная-2-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var global = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");

        var created = await Owner().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { p1, p2 } });
        created.StatusCode.Should().Be(HttpStatusCode.OK, "групповой чат проектных персон общего проекта — прежнее поведение");
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        var r = await Owner().PutAsJsonAsync($"/api/chats/{chatId}/participants", new { personaIds = new[] { p1, global } });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(SessionManager.GlobalPersonaInSharedException.Code);
        // Отказ не трогает состав чата.
        (await ChatAsync(Owner(), chatId)).GetProperty("participants").EnumerateArray()
            .Select(e => e.GetString()).Should().Equal(p1, p2);
    }

    // ── прежнее поведение ────────────────────────────────────────────────────────────

    [Fact]
    public async Task СозданиеЧатаОбщегоПроектаСПроектнойПерсоной_Разрешено()
    {
        var (projectId, _) = await ProjectWithChatAsync("editor");
        var personaId = await CreatePersonaAsync(Owner(),
            "Проектная-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);

        var r = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto", personaId });

        r.StatusCode.Should().Be(HttpStatusCode.Created, "персона проекта того же проекта — как раньше");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        (await ChatAsync(Owner(), chatId)).GetProperty("personaId").GetString().Should().Be(personaId);
    }

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
