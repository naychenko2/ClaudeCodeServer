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
/// W3-B4a (разрез docs/research/shared-projects-personas-cut-2026-11.md, §3.1 строки {id}/chats;
/// И-2, И-5, И-6): чаты с персоной ключуются арендатором, а не вызывающим.
///
/// Дефект прода 05.11: редактор жмёт «Новый чат» с персоной общего проекта → 404 (резолв
/// _personas.Get(id, UserId) не знал чужой проектной персоны). После перехода на
/// HttpContext.GetRequestScope(): редактор создаёт чат (200), чат — в проекте, OwnerId —
/// владелец, CreatedByUserId — вызывающий (И-2). Читатель — 403 (персона видна, права хода
/// нет), посторонний — 404 (существование не раскрываем).
///
/// И-5: список GET {id}/chats фильтруется видимостью вызывающего — приватный чат владельца
/// с этой персоны в список участника не входит (у автора — остаётся).
/// И-6: проектная персона, чей проект принадлежит не ownerId (битая запись), — исключение в
/// SessionManager, а не тихий фолбэк в чат вне проекта: с ключом арендатора такой чат
/// родился бы в личном пространстве владельца от лица участника.
/// И-6 / дефект D1 (доработка W3-B4a): своя глобальная персона участника в общем проекте —
/// тот же отказ 400 global_persona_in_shared, что у владельца и что на POST /sessions:
/// рамка проверяется по целевому проекту чата, а не внутри ветки владельца, — чата вне
/// проекта после отказа нет.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaChatTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaChatTests(TestWebApplicationFactory factory)
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

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    /// <summary>Проект владельца; role — приглашённый участник этой роли (null — участников нет).</summary>
    private async Task<string> ProjectWithMemberAsync(string? role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        if (role is not null) await SetFlagAsync(Member());
        var dir = Path.Combine(_factory.TempDir, "spc_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "ПерсонаЧаты" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        if (role is not null)
            (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
                new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    /// <summary>Проектная персона проекта владельца (её создаёт владелец — правo Create здесь
    /// не предмет шага, W3-B3).</summary>
    private async Task<(string ProjectId, string PersonaId)> SharedProjectWithProjectPersonaAsync(string? role)
    {
        var projectId = await ProjectWithMemberAsync(role);
        var r = await Owner().PostAsJsonAsync("/api/personas",
            new { name = "Командир-" + Guid.NewGuid().ToString("N")[..6], scope = "project", projectId });
        r.EnsureSuccessStatusCode();
        return (projectId, IdOf(await r.Content.ReadAsStringAsync()));
    }

    private static async Task<List<string>> PersonaChatIdsAsync(HttpClient client, string personaId) =>
        (await BodyAsync(await client.GetAsync($"/api/personas/{personaId}/chats")))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();

    /// <summary>Записанный чат — автор и область берутся из стора, а не из ответа: поля
    /// серверные, проверка — состояние хранилища после полного запроса.</summary>
    private Session Stored(string sessionId) =>
        _factory.Services.GetRequiredService<SessionManager>().GetById(sessionId)!;

    [Fact]
    public async Task РедакторСоздаётЧатСПроектнойПерсоной_200ЧатПроектаАвторРедактор()
    {
        var (projectId, personaId) = await SharedProjectWithProjectPersonaAsync("editor");

        var r = await Member().PostAsJsonAsync($"/api/personas/{personaId}/chats",
            new { mode = "auto", name = "Чат редактора" });

        r.StatusCode.Should().Be(HttpStatusCode.OK,
            "дефект 05.11: раньше был 404 — резолв персоны по ключу вызывающего");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        var chat = Stored(chatId);
        chat.ProjectId.Should().Be(projectId, "проективная персона создаёт чат только в своём проекте");
        chat.OwnerId.Should().Be(OwnerId(), "ключ области — арендатор (И-2)");
        chat.CreatedByUserId.Should().Be(MemberId(), "автор — вызывающий из sub JWT, не владелец проекта");

        // Обе стороны списка видят общий чат: и арендатор, и сам редактор (GET-половина дефекта)
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().Contain(chatId);
        (await PersonaChatIdsAsync(Member(), personaId)).Should().Contain(chatId);
    }

    [Fact]
    public async Task ЧитательНеСоздаёт_403ИБезНовогоЧата()
    {
        var (_, personaId) = await SharedProjectWithProjectPersonaAsync("reader");

        var r = await Member().PostAsJsonAsync($"/api/personas/{personaId}/chats", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "персона проекта читателю видена (Read) — отказ по праву хода, 403, а не 404");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(ProjectScopeAttribute.ErrorCode);
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().BeEmpty("отказ не оставил нового чата");
    }

    [Fact]
    public async Task ПостороннийНеСоздаёт_404()
    {
        var (_, personaId) = await SharedProjectWithProjectPersonaAsync(null);

        var r = await Member().PostAsJsonAsync($"/api/personas/{personaId}/chats", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound, "несуществующему для него — неотличим от несуществующей");
    }

    [Fact]
    public async Task ПриватныйЧатВладельцаНеВиденУчастнику()
    {
        var (_, personaId) = await SharedProjectWithProjectPersonaAsync("editor");

        // Чат владельца с персоной и его же чат-приватный (S2d-7a: автор, общий проект, без сообщений)
        var created = await Owner().PostAsJsonAsync($"/api/personas/{personaId}/chats",
            new { mode = "auto", name = "Приватный владельца" });
        created.EnsureSuccessStatusCode();
        var privateId = IdOf(await created.Content.ReadAsStringAsync());
        Stored(privateId).CreatedByUserId.Should().Be(OwnerId(),
            "автор берётся из sub вызывающего: владелец — автор своего чата, он и писатель приватности");
        (await Owner().PutAsJsonAsync($"/api/chats/{privateId}/visibility", new { @private = true }))
            .EnsureSuccessStatusCode();

        // Участник создал свой общий чат — список участника непустой, проверка не вакуумная
        var own = await Member().PostAsJsonAsync($"/api/personas/{personaId}/chats", new { mode = "auto" });
        own.EnsureSuccessStatusCode();
        var ownId = IdOf(await own.Content.ReadAsStringAsync());

        // И-5: viewer — вызывающий, не арендатор: приватный чат владельца в список участника не входит
        (await PersonaChatIdsAsync(Member(), personaId)).Should().Equal(ownId);
        // У автора приватный чат остаётся
        (await PersonaChatIdsAsync(Owner(), personaId)).Should().Contain(new[] { privateId, ownId });
    }

    [Fact]
    public async Task ПроектнаяПерсонаЧужогоВладельца_ИсключениеИБезСессии()
    {
        var projectId = await ProjectWithMemberAsync(null);
        // Битая запись (разрез §2.2): персона принадлежит участнику, её проект — владельцу.
        // Через API она не создаётся (ValidProject по вызывающему), поэтому — прямая запись стора:
        // И-6 обязан держать сервис, а не только резолв запроса.
        var broken = _factory.Services.GetRequiredService<PersonaManager>().Create(
            MemberId(), "Битая-" + Guid.NewGuid().ToString("N")[..6],
            null, null, null, null, null, PersonaScope.Project, projectId, null, null, false);

        var sessionsSvc = _factory.Services.GetRequiredService<SessionManager>();
        var before = sessionsSvc.GetAllOwnedBy(MemberId()).Count;

        var act = async () => await sessionsSvc.CreatePersonaChatAsync(MemberId(), broken.Id, ClaudeMode.Auto);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "И-6: проектная персона с проектом чужого владельца — отказ, а не тихий чат вне проекта");
        sessionsSvc.GetAllOwnedBy(MemberId()).Count.Should().Be(before, "отказ не оставил сессии");
    }

    [Fact]
    public async Task ПараллельныеСоздания_ДваЧатаСвоиАвторы()
    {
        var (projectId, personaId) = await SharedProjectWithProjectPersonaAsync("editor");

        var ownerTask = Owner().PostAsJsonAsync($"/api/personas/{personaId}/chats",
            new { mode = "auto", name = "Чат владельца" });
        var memberTask = Member().PostAsJsonAsync($"/api/personas/{personaId}/chats",
            new { mode = "auto", name = "Чат редактора" });
        var responses = await Task.WhenAll(ownerTask, memberTask);

        foreach (var r in responses) r.StatusCode.Should().Be(HttpStatusCode.OK);
        var ownerChatId = IdOf(await responses[0].Content.ReadAsStringAsync());
        var memberChatId = IdOf(await responses[1].Content.ReadAsStringAsync());
        ownerChatId.Should().NotBe(memberChatId, "два вызова — два чата, а не один общий");

        Stored(ownerChatId).ProjectId.Should().Be(projectId);
        Stored(memberChatId).ProjectId.Should().Be(projectId);
        Stored(ownerChatId).CreatedByUserId.Should().Be(OwnerId());
        Stored(memberChatId).CreatedByUserId.Should().Be(MemberId(),
            "автор каждого чата — свой вызывающий, гонка его не путает");
    }

    /// <summary>Своя глобальная персона участника (личная дефолт-персона).</summary>
    private static async Task<string> CreateGlobalPersonaAsync(HttpClient client, string name)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope = "global" });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task УчастникСГлобальнойПерсонойВОбщемПроекте_400ИБезЧатаВнеПроекта()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var globalId = await CreateGlobalPersonaAsync(Member(), "Личная-" + Guid.NewGuid().ToString("N")[..6]);

        // Путь фронта в сценарии дефекта D1 (defaultPersona.ts:179): «Новый чат» в проекте
        // без руководителя падает на личную дефолт-персону — POST {id}/chats с projectId в теле.
        var sessionsSvc = _factory.Services.GetRequiredService<SessionManager>();
        var outsideBefore = sessionsSvc.GetAllOwnedBy(MemberId()).Count(s => s.ProjectId is null);

        var r = await Member().PostAsJsonAsync($"/api/personas/{globalId}/chats",
            new { mode = "auto", projectId });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "дефект D1: раньше был 200 — рамка жила в ветке владельца, чат молча уходил вне проекта");
        (await BodyAsync(r)).GetProperty("code").GetString()
            .Should().Be(SessionManager.GlobalPersonaInSharedException.Code,
                "та же рамка и тот же машинный код, что у владельца и что на POST /sessions");
        (await PersonaChatIdsAsync(Member(), globalId)).Should().BeEmpty("отказ не оставил чата с персоной");
        sessionsSvc.GetAllOwnedBy(MemberId()).Count(s => s.ProjectId is null).Should().Be(outsideBefore,
            "у участника не появилось чата с projectId = null");
    }
}
