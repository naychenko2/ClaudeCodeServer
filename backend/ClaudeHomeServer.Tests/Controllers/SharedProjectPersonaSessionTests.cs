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
/// Собеседник чата проекта (W3-B4b, разрез docs/research/shared-projects-personas-cut-2026-11.md §3.2,
/// И-1/И-2). Инвариант: участник никогда не получает чат в общем проекте с глобальной персоной
/// (своей или владельца). Создание без personaId: руководитель резолвится по ключу арендатора;
/// нет руководителя — 400 no_project_persona (провижн личного ассистента — право владельца).
/// Создание с personaId: одна точка правды ResolvePersona + сверка «проектная персона — только
/// своего проекта»; персона другого проекта и чужая глобальная — 404, неотличимы от несуществующей.
/// Смена собеседника: участник — только в чате, автор которого он; владелец — в любом (иначе 403
/// owner_settings, как прочие настройки чата); читатель — 403 по праву хода. Два конкурентных
/// SetPersona одного чата — под замком спикера: состояние целое, побеждает последний.
/// </summary>
[Trait("Category", "Host")]
public class SharedProjectPersonaSessionTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public SharedProjectPersonaSessionTests(TestWebApplicationFactory factory)
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

    private SessionManager Sessions() => _factory.Services.GetRequiredService<SessionManager>();

    private static async Task SetFlagAsync(HttpClient client) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = true }))
            .EnsureSuccessStatusCode();

    private static string IdOf(string json) =>
        JsonSerializer.Deserialize<JsonElement>(json).GetProperty("id").GetString()!;

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    /// <summary>Проект владельца с приглашённым участником этой роли.</summary>
    private async Task<string> ProjectWithMemberAsync(string role)
    {
        var owner = Owner();
        await SetFlagAsync(owner);
        await SetFlagAsync(Member());
        var dir = Path.Combine(_factory.TempDir, "sps_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var created = await owner.PostAsJsonAsync("/api/projects",
            new { name = "Собеседник" + Guid.NewGuid().ToString("N")[..6], rootPath = dir });
        created.EnsureSuccessStatusCode();
        var projectId = IdOf(await created.Content.ReadAsStringAsync());

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/members",
            new { username = TestWebApplicationFactory.SecondUsername, role })).EnsureSuccessStatusCode();
        return projectId;
    }

    /// <summary>Проектная персона владельца этого проекта (effort — чтобы итог гонки SetPersona
    /// был различим: собеседник и его усилие — одна пара).</summary>
    private async Task<string> CreateProjectPersonaAsync(string projectId, string name, string? effort = null)
    {
        var r = await Owner().PostAsJsonAsync("/api/personas",
            new { name, scope = "project", projectId, effort });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    private async Task<string> CreateGlobalPersonaAsync(HttpClient client, string name)
    {
        var r = await client.PostAsJsonAsync("/api/personas", new { name, scope = "global" });
        r.EnsureSuccessStatusCode();
        return IdOf(await r.Content.ReadAsStringAsync());
    }

    /// <summary>Руководитель проекта: проектная персона владельца + make-default
    /// (единственная REST-точка DefaultPersonaId).</summary>
    private async Task<string> SetLeaderAsync(string projectId)
    {
        var personaId = await CreateProjectPersonaAsync(projectId, "Руководитель-" + Guid.NewGuid().ToString("N")[..6]);
        (await Owner().PostAsJsonAsync($"/api/personas/{personaId}/make-default", new { })).EnsureSuccessStatusCode();
        return personaId;
    }

    /// <summary>Записанный чат — из стора, а не из ответа: проверяется состояние хранилища.</summary>
    private Session Stored(string sessionId) => Sessions().GetById(sessionId)!;

    private static async Task<List<string>> ChatIdsAsync(HttpClient client, string projectId) =>
        (await BodyAsync(await client.GetAsync($"/api/projects/{projectId}/sessions")))
            .EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();

    private async Task<List<string>> MemberPersonaIdsAsync() =>
        (await BodyAsync(await Member().GetAsync("/api/personas")))
            .EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList();

    // ── создание без personaId: руководитель по ключу арендатора ─────────────────────

    [Fact]
    public async Task РедакторСоздаётЧатСРуководителем_201ИЧатСРуководителемАНеСвоейПодменой()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var leadId = await SetLeaderAsync(projectId);

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.Created,
            "руководитель резолвится по ключу арендатора: участнику он доступен, своей подмены нет");
        var chatId = IdOf(await r.Content.ReadAsStringAsync());
        Stored(chatId).PersonaId.Should().Be(leadId,
            "чат в проекте с руководителем ведёт руководитель (командные механики — только ему)");
    }

    [Fact]
    public async Task РедакторСоздаётЧатБезРуководителя_400NoProjectPersonaИПровижаУчастникаНеБыло()
    {
        var projectId = await ProjectWithMemberAsync("editor");

        // Удаляем штатного ассистента участника (он заведён провижном при старте хоста), чтобы
        // «провижа не запускалась» проверялась отсутствием новой персоны, а не идемпотентностью.
        var assistantId = _factory.Services.GetRequiredService<UserStore>().GetById(MemberId())!.DefaultPersonaId;
        if (assistantId is not null)
            (await Member().DeleteAsync($"/api/personas/{assistantId}")).EnsureSuccessStatusCode();
        var before = await MemberPersonaIdsAsync();

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });

        r.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "нет руководителя — личной подмены участнику нет: провижн глобальной персоны право владельца");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be("no_project_persona",
            "машинный код: фронт по нему показывает «попросите владельца назначить» (W3-F2a)");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEmpty("отказ не оставил в проекте нового чата");
        (await MemberPersonaIdsAsync()).Should().BeEquivalentTo(before,
            "провижн не запущен: у участника не появилось новой глобальной персоны");
    }

    // ── создание с personaId: одна точка правды и сверка проекта ──────────────────────

    [Fact]
    public async Task СозданиеЧатаСПерсонойДругогоПроекта_404ИЧатНеСоздан()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var otherProject = await ProjectWithMemberAsync("editor"); // второй проект того же владельца
        var personaOfOther = await CreateProjectPersonaAsync(otherProject, "ЧужогоПроекта-" + Guid.NewGuid().ToString("N")[..6]);
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", personaId = personaOfOther });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "проектная персона назначается только чату своего проекта: двойной поиск «по caller, " +
            "потом по арендатору», который раньше пропускал её сюда, снят (И-1)");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before, "отказ не оставил нового чата");
    }

    [Fact]
    public async Task УчастникНеРезолвитГлобальнуюПерсонуВладельца_404ИЧатНеСоздан()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var ownerGlobal = await CreateGlobalPersonaAsync(Owner(), "ГлобальнаяВладельца-" + Guid.NewGuid().ToString("N")[..6]);
        var before = await ChatIdsAsync(Owner(), projectId);

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions",
            new { mode = "auto", personaId = ownerGlobal });

        r.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "глобальная персона владельца участнику не видна ни по какому id (И-3): неотличима от несуществующей");
        (await ChatIdsAsync(Owner(), projectId)).Should().BeEquivalentTo(before, "отказ не оставил нового чата");
    }

    // ── смена собеседника: автор-участник свой чат, владелец любой ────────────────────

    [Fact]
    public async Task РедакторМеняетСобеседникаВСвоёмЧате_200()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        await SetLeaderAsync(projectId); // чат без personaId редактору — только с руководителем
        var other = await CreateProjectPersonaAsync(projectId, "ВтораяКомандная-" + Guid.NewGuid().ToString("N")[..6]);

        var created = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        created.EnsureSuccessStatusCode();
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions/{chatId}/persona",
            new { personaId = other });

        r.StatusCode.Should().Be(HttpStatusCode.OK,
            "автор-участник меняет собеседника в чате, автор которого он (W3-B4b)");
        Stored(chatId).PersonaId.Should().Be(other);
    }

    [Fact]
    public async Task РедакторНеМеняетСобеседникаВЧатеВладельца_403OwnerSettingsИСобеседникПрежний()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        var leadId = await SetLeaderAsync(projectId);
        var other = await CreateProjectPersonaAsync(projectId, "ВтораяКомандная-" + Guid.NewGuid().ToString("N")[..6]);

        var created = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        created.EnsureSuccessStatusCode();
        var chatId = IdOf(await created.Content.ReadAsStringAsync());
        Stored(chatId).PersonaId.Should().Be(leadId);

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions/{chatId}/persona",
            new { personaId = other });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "чужой чат проекта участнику виден, но собеседник в нём — настройка: решает автор или владелец");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(OwnerSettingsGate.ErrorCode,
            "тот же код, что у прочих настроек чата (S2a-10b)");
        Stored(chatId).PersonaId.Should().Be(leadId, "отказ не пишет собеседника");
    }

    [Fact]
    public async Task ЧитательНеМеняетСобеседника_403ПоПравуХода()
    {
        var projectId = await ProjectWithMemberAsync("reader");
        var leadId = await SetLeaderAsync(projectId);

        var created = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        created.EnsureSuccessStatusCode();
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        var r = await Member().PostAsJsonAsync($"/api/projects/{projectId}/sessions/{chatId}/persona",
            new { personaId = leadId });

        r.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "у читателя нет права вести ход — тот же отказ, что у отправки сообщения (атрибут RunTurn)");
        (await BodyAsync(r)).GetProperty("code").GetString().Should().Be(ProjectScopeAttribute.ErrorCode);
    }

    // ── гонка двух SetPersona: состояние целое ────────────────────────────────────────

    [Fact]
    public async Task ParallelSetPersona_SameChat_ConsistentState()
    {
        var projectId = await ProjectWithMemberAsync("editor");
        await SetLeaderAsync(projectId);
        var created = await Owner().PostAsJsonAsync($"/api/projects/{projectId}/sessions", new { mode = "auto" });
        created.EnsureSuccessStatusCode();
        var chatId = IdOf(await created.Content.ReadAsStringAsync());

        // Два собеседника различимы усилием: «пара целая» проверяется по соответствию
        // (собеседник → его effort), а не по одному полю
        var a = await CreateProjectPersonaAsync(projectId, "Гонка-A-" + Guid.NewGuid().ToString("N")[..6], effort: "low");
        var b = await CreateProjectPersonaAsync(projectId, "Гонка-B-" + Guid.NewGuid().ToString("N")[..6], effort: "high");

        var sessions = Sessions();
        for (var i = 0; i < 10; i++)
        {
            // Спиновой барьер: обе темы уже крутятся у gate, общий старт — по Volatile.Write.
            // TCS/монитора тут не годятся: их пробуждение разнесено на десятки микросекунд,
            // а критическая секция смены спикера — микросекунды: с разнесённым стартом
            // гонка была бы мнимой и без замка проходила бы зелёной.
            var gate = 0;
            var t1 = Task.Run(() =>
            {
                while (System.Threading.Volatile.Read(ref gate) == 0) System.Threading.Thread.SpinWait(64);
                return sessions.SetPersona(chatId, OwnerId(), a, null);
            });
            var t2 = Task.Run(() =>
            {
                while (System.Threading.Volatile.Read(ref gate) == 0) System.Threading.Thread.SpinWait(64);
                return sessions.SetPersona(chatId, OwnerId(), b, null);
            });
            System.Threading.Thread.SpinWait(256);
            System.Threading.Volatile.Write(ref gate, 1);
            var results = await Task.WhenAll(t1, t2);
            results.Should().NotContain(s => s == null, "обе смены прошли: отказа владельцу здесь нет");

            var stored = Stored(chatId);
            if (stored.PersonaId == a)
                stored.Effort.Should().Be("low", $"итерация {i}: собеседник A и его усилие — одна пара, смесь половин недопустима");
            else if (stored.PersonaId == b)
                stored.Effort.Should().Be("high", $"итерация {i}: собеседник B и его усилие — одна пара, смесь половин недопустима");
            else
                stored.PersonaId.Should().BeOneOf(a, b, $"итерация {i}: чат обязан нести одного из запрошенных собеседников целиком");
        }
    }
}
