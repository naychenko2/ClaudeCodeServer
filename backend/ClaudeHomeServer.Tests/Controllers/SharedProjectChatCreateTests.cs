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
/// Создание чата в общем проекте (S2d-1c): редактор создаёт чат и становится его автором
/// (CreatedByUserId — только из sub вызывающего; поля автора в DTO запроса нет, именованное
/// поле в теле игнорируется). Читатель — 403 (проект ему виден, права хода нет — отказ различается
/// правом, а не статусом), посторонний — 404 (существование не раскрываем). Глобальная персона
/// чату общего проекта не назначается и редактору — 400 global_persona_in_shared (перевод Create
/// с Write на RunTurn её не ослабляет). Личный проект владельца и чат вне проекта — прежнее поведение.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectChatCreateTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectChatCreateTests(TestWebApplicationFactory factory)
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
        var dir = Path.Combine(_factory.TempDir, "scc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Создание" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    private static async Task<List<string>> ChatIdsAsync(HttpClient client, string projectId) =>
        (await BodyAsync(await client.GetAsync($"/api/projects/{projectId}/sessions")))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();

    /// <summary>Записанный чат — автор берётся из стора, а не из ответа: поле серверное
    /// (фронт его не читает), проверка — состояние хранилища после полного запроса.</summary>
    private Session Stored(string sessionId) =>
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!;

    [Fact]
    public async Task РедакторСоздаётЧат_201ИСтановитсяАвтором()
    {
        var projectId = await ProjectWithMemberAsync("editor");

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", name = "Чат редактора" });

        r.StatusCode.Should().Be(HttpStatusCode.Created, "создание чата — действие хода, редактору открыто (S2d-1c)");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        Stored(chatId).CreatedByUserId.Should().Be(MemberId(),
            "автор — вызывающий из sub JWT, а не владелец проекта");
        // Чат принадлежит проекту: владелец видит его как любой чат проекта (автор на видимость
        // не влияет, S2d-1)
        (await ChatIdsAsync(Owner(), projectId)).Should().Contain(chatId);
    }

    [Fact]
    public async Task АвторТолькоИзJWT_ПолеТелаЗапросаИгнорируется()
    {
        var projectId = await ProjectWithMemberAsync("editor");

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", createdByUserId = OwnerId() });

        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        Stored(chatId).CreatedByUserId.Should().Be(MemberId(),
            "поля автора в контракте запроса нет: автор всегда берётся из sub вызывающего");
    }

    [Fact]
    public async Task ЧитательНеСоздаёт_403ИБезНовогоЧата()
    {
        var projectId = await ProjectWithMemberAsync("reader");

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "проект читателю виден — отказ по праву, 403, а не 404");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(ProjectScopeAttribute.ErrorCode);
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEmpty("отказ не оставил в проекте нового чата");
    }

    [Fact]
    public async Task ПостороннийНеСоздаёт_404()
    {
        var projectId = await ProjectWithMemberAsync(null);

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound, "невидимому проекту существование не раскрываем");
    }

    [Fact]
    public async Task РедакторСГлобальнойПерсоной_400СКодомИБезНовогоЧата()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var personaId = await CreateGlobalPersonaAsync(Member(), "Глобальная-" + Guid.NewGuid().ToString("N")[..6]);

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", personaId });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await BodyAsync(r)).GetProperty("code").GetString()
            .Should().Be(SessionManager.GlobalPersonaInSharedException.Code,
                "перевод Create на RunTurn не ослабил рамку: та же проверка, что для владельца");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEmpty("отказ не оставил в проекте нового чата");
    }

    private static async Task<string> CreateGlobalPersonaAsync(HttpClient client, string name)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope = "global" });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ВладелецЛичногоПроектаСоздаётКакРаньше_АвторВладелец()
    {
        var projectId = await ProjectWithMemberAsync(null);

        var r = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.Created, "личный проект — прежний путь");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        Stored(chatId).CreatedByUserId.Should().Be(OwnerId(),
            "в личном проекте автор — владелец: тот же путь контроллера, автор не меняет видимости");
    }

    [Fact]
    public async Task ЧатВнеПроекта_ПрежнееПоведениеБезАвтора()
    {
        var r = await Owner().PostAsJsonAsync("/api/chats", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.Created, "путь чатов вне проекта S2d-1c не тронут");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        Stored(chatId).CreatedByUserId.Should().BeNull(
            "CreateChatAsync автора не принимает: поведение прежнего пути не изменилось");
    }
}
