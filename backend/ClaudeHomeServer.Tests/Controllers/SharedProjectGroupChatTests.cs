using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Групповые чаты с проектными персонами общего проекта (W3-B4c, разрез §3.2). Зона — по ведущей:
/// проектная персона резолвится ResolvePersona с правом хода (И-1), сервис зовётся с ключом
/// арендатора (И-2): редактор создаёт группу из персон проекта и становится её автором, читателю
/// отказ 403 (отказ различается правом, а не статусом). Группа проектного чата состоит только из
/// персон этого проекта: глобальная персона владельца участнику несуществующая (404, И-3), отказ
/// не трогает состав. Группа вне проекта (свои глобальные персоны участника) — прежний путь.
/// Путь владельца и отказ глобальной в общем проекте (global_persona_in_shared) держат
/// SharedProjectPersonaTests, здесь он не повторяется.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectGroupChatTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectGroupChatTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Member() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string MemberId() => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.SecondUsername)!.Id;

    private string OwnerId() => _factory.Services.GetRequiredService<UserStore>()
        .FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    /// <summary>Проект владельца; role — приглашённый участник этой роли (null — участников нет,
    /// проект личный).</summary>
    private async Task<string> ProjectWithMemberAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        if (role is not null) await SetFlagAsync(Member());
        var dir = Path.Combine(_factory.TempDir, "spg_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Группа" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    private static async Task<string> CreatePersonaAsync(HttpClient client, string name,
        string? scope = null, string? projectId = null)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope, projectId });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<List<string>> ChatIdsAsync(HttpClient client, string projectId) =>
        (await BodyAsync(await client.GetAsync($"/api/projects/{projectId}/sessions")))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();

    /// <summary>Записанный чат берётся из стора, а не из ответа: автор и состав — поля сервера,
    /// проверка — состояние хранилища после полного запроса.</summary>
    private Session Stored(string sessionId) =>
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!;

    // ── редактор создаёт группу из персон проекта ──────────────────────────────────────

    [Fact]
    public async Task РедакторСоздаётГруппуПроектныхПерсон_200ЧатПроектаАвторРедактор()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var p1 = await CreatePersonaAsync(Owner(), "Проектная-1-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var p2 = await CreatePersonaAsync(Owner(), "Проектная-2-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);

        var r = await Member().PostAsJsonAsync("/api/chats/group",
            new { personaIds = new[] { p1, p2 }, mode = "auto", name = "Команда редактора" });

        r.StatusCode.Should().Be(HttpStatusCode.OK,
            "ведущая — персона проекта: право хода с ней у редактора есть (ResolvePersona RunTurn)");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        var stored = Stored(chatId);
        stored.ProjectId.Should().Be(projectId, "зона — по ведущей: чат в проекте её проекта");
        stored.OwnerId.Should().Be(OwnerId(), "ключ записи — арендатор, а не вызывающий (И-2)");
        stored.CreatedByUserId.Should().Be(MemberId(), "автор — вызывающий");
        stored.PersonaId.Should().Be(p1, "стартовый спикер — ведущая");
        stored.Participants.Should().Equal(p1, p2);
        (await ChatIdsAsync(Owner(), projectId)).Should().Contain(chatId,
            "владелец видит групповой чат проекта как любой чат проекта");
    }

    [Fact]
    public async Task ЧитательНеСоздаётГруппу_403ИБезНовогоЧатаВПроекте()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var p1 = await CreatePersonaAsync(Owner(), "Проектная-1-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var p2 = await CreatePersonaAsync(Owner(), "Проектная-2-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Member().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { p1, p2 } });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "персона проекта видна читателю с Read, права вести ход нет — отказ различается правом, а не статусом");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(ProjectScopeAttribute.ErrorCode);
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказ не оставил в проекте нового чата");
    }

    // ── глобальная персона владельца в группе проектного чата ──────────────────────────

    [Fact]
    public async Task РедакторСГлобальнойПерсонойВладельца_PostGroup_404ИЧатНеСоздан()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var p1 = await CreatePersonaAsync(Owner(), "Проектная-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var global = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Member().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { p1, global } });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "И-3: личная персона владельца участнику несуществующая, неотличимо от несуществующей");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before,
            "отказ не оставил в проекте нового чата");
    }

    [Fact]
    public async Task РедакторСГлобальнойПерсонойВладельца_PutParticipants_404ИСоставНеМеняется()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var p1 = await CreatePersonaAsync(Owner(), "Проектная-1-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var p2 = await CreatePersonaAsync(Owner(), "Проектная-2-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var global = await CreatePersonaAsync(Owner(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6], "global");

        var created = await Member().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { p1, p2 } });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        var r = await Member().PutAsJsonAsync($"/api/chats/{chatId}/participants",
            new { personaIds = new[] { p1, global } });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "та же грань, что при создании: состав проектного чата — только персоны этого проекта");
        // Отказ не трогает состав чата.
        Stored(chatId).Participants.Should().Equal(p1, p2);
    }

    // ── гонка двух составов: итог — один из двух целиком ───────────────────────────────

    [Fact]
    public async Task ParallelSetParticipants_SameChat_OneSetWins()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var a1 = await CreatePersonaAsync(Owner(), "СоставА-1-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var a2 = await CreatePersonaAsync(Owner(), "СоставА-2-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var b1 = await CreatePersonaAsync(Owner(), "СоставБ-1-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);
        var b2 = await CreatePersonaAsync(Owner(), "СоставБ-2-" + Guid.NewGuid().ToString("N")[..6], "project", projectId);

        var created = await Member().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { a1, a2 } });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        var putA = Member().PutAsJsonAsync($"/api/chats/{chatId}/participants", new { personaIds = new[] { a1, a2 } });
        var putB = Member().PutAsJsonAsync($"/api/chats/{chatId}/participants", new { personaIds = new[] { b1, b2 } });
        await Task.WhenAll(putA, putB);
        (await putA).StatusCode.Should().Be(HttpStatusCode.OK);
        (await putB).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = Stored(chatId).Participants ?? [];
        var isA = after.SequenceEqual(new[] { a1, a2 });
        var isB = after.SequenceEqual(new[] { b1, b2 });
        (isA || isB).Should().BeTrue(
            "состав пишется целиком под замком сессии: итог — один из двух запросов целиком, не смесь");
    }

    // ── группа вне проекта — прежний путь ──────────────────────────────────────────────

    [Fact]
    public async Task ГруппаСвоихГлобальныхПеронУчастника_ВнеПроектаКакРаньше()
    {
        var g1 = await CreatePersonaAsync(Member(), "Своя-1-" + Guid.NewGuid().ToString("N")[..6], "global");
        var g2 = await CreatePersonaAsync(Member(), "Своя-2-" + Guid.NewGuid().ToString("N")[..6], "global");

        var r = await Member().PostAsJsonAsync("/api/chats/group", new { personaIds = new[] { g1, g2 } });

        r.StatusCode.Should().Be(HttpStatusCode.OK,
            "ведущая — своя глобальная персона: прежний путь по UserId, резолв даёт того же арендатора");
        var stored = Stored(IdOf(await r.Content.ReadAsStringAsync()));
        stored.ProjectId.Should().BeNull("группа личных персон — вне проекта");
        stored.CreatedByUserId.Should().BeNull(
            "чат вне проекта автора не несёт: поведение прежнего пути не изменилось");
    }
}
