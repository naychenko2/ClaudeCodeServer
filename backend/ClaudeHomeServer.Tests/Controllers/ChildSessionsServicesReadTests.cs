using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Х-W4-31 (дефект d7c8477b): прямой доступ ребёнка к взрослым page-level опросам экрана —
/// списку чатов (GET api/projects/{id}/sessions) и списку сервисов (GET api/projects/{id}/services) —
/// закрыт всем, кроме своего проекта-предмета. Тот же образец, что у «Знаний» (Б20a, решение 81):
/// [ChildAllowed] на ручке + «свой ли это предмет» внутри неё. Свой предмет — 200 (в проекте-предмете
/// нет взрослых чатов — TurnsOnlyForChildChats, список отдаёт ровно свои уроки); свой обычный или
/// чужой предмет — 403 not_child_subject (отказ виден именно как детская граница, а не невидимость 404:
/// фикстура даёт ребёнку видимость чужого проекта — участник + рубильник). Взрослый путь без изменений.
///
/// Прочие ручки этих классов (создание/правка/удаление чата, persona, context, icon-batch;
/// start/stop/active/external, launch.json) [ChildAllowed] не имеют намеренно — их отсекает гейт И1
/// до тела: отдельный тест на 403 child_mode. Состав размеченных — в ChildModeEndpointGuardTests;
/// принадлежность меток закрытым панелям «chats»/«preview» — в ChildSurfaceGuardTests.
///
/// Фикстура — как у ChildTasksTests: ребёнок публичным путём стора, его проект-предмета — провижером
/// предмета (единственная точка создания); чужой предмет — проект второго ребёнка; куратор —
/// Editor-участник при своём флаге общих проектов. Свой tag имени и предмета на каждый тест:
/// хост класса общий.
///
/// Мутации (каждая краснит ровно названное):
/// • снять [ChildAllowed] на GetAll/Services — СвойПредмет_* краснеют (403 child_mode вместо 200);
/// • снять детскую проверку в GetAll / в Services — ЧужойПредмет_СписокЧатов /
///   ЧужойПредмет_СписокСервисов краснеют (200 вместо 403);
/// • снять условие IsChildSubject в IsOwnSubjectProject — СвойОбычныйПроект_* краснеют
///   (200 вместо 403).
/// Каждая ручка проверяется своим кейсом (Х-W4-47, хвост ревью e748c039): падение первой
/// не скрывает вторую.
/// </summary>
[Trait("Category", "Host")]
public class ChildSessionsServicesReadTests : IClassFixture<ChildSessionsServicesReadTests.Hosts>
{
    // Рубильник инстанса общих проектов: без него ребёнок-участник чужого проекта не доходит
    // до тела ручки (ProjectAccessService — fail-closed), и 403 на чужом предмете был бы 404
    // невидимости, а не отказом детской проверки (тот же приём, что в ChildKnowledgeReadTests).
    public sealed class Hosts : TestWebApplicationFactory
    {
        public Hosts() => ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private const string ChildPassword = "kid-sessvc-password";

    private readonly Hosts _host;

    public ChildSessionsServicesReadTests(Hosts host) => _host = host;

    private UserStore Users => _host.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => _host.Services.GetRequiredService<ProjectManager>();

    // --- Фикстура (образец ChildTasksTests.NewChild) ----------------------------------

    // Ребёнок с включённым предметом и его проект-предмета — только через провижер
    // (единственная точка создания предмета).
    private (string ChildId, HttpClient Child, Project Subject) NewChild(string tag)
    {
        var username = "kid.sessvc." + tag;
        var kid = Users.Add(username, ChildPassword, "user");
        var subject = new ChildSubject
        {
            Id = tag, Label = "Предмет " + tag, Glyph = "Аа", Color = "info",
        };
        Users.SetChildProfile(kid.Id, new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor-1", ProviderKey = "pool-key", Model = "sonnet",
            Spaces = { [ChildSpaces.Lessons] = new SpaceLimits
            {
                Windows = [new TimeWindow { From = "00:00", To = "23:59" }], DailyMinutes = 600,
            } },
            Subjects = [subject],
        }, resetRules: false).Should().BeTrue("профиль ребёнка пишется публичным путём стора");

        var provided = _host.Services.GetRequiredService<ChildSubjectProjectProvisioner>()
            .EnsureSubjectProjectAsync(kid.Id, subject).GetAwaiter().GetResult();
        provided.Should().NotBeNull("проект-предмета обеспечивает только его провижер");
        return (kid.Id, _host.CreateAuthenticatedClient(username, ChildPassword), provided!);
    }

    // Видимость чужого проекта-предмета ребёнку: участник Reader + его флаг общих проектов —
    // чтобы 403 давала детская проверка, а не ProjectScope.
    private void MakeVisible(string childId, string otherId, Project foreign)
    {
        Projects.SetMembers(foreign.Id, [new ProjectMember
        {
            UserId = childId, Role = ProjectRole.Reader, AddedBy = otherId,
        }]);
        Users.SetFeatureFlag(childId, FeatureFlagKeys.SharedProjects, true);
    }

    // Свой обычный проект ребёнка: без маркера предмета, владение — детское.
    private Project RegularProjectOf(string childId, string tag)
    {
        var dir = Path.Combine(_host.TempDir, "regular_" + tag);
        return Projects.Create("обычный " + tag, dir, childId, Users.GetById(childId)!.Username,
            createDirectory: true);
    }

    // Куратор: взрослый Editor в проекте-предмета + его флаг общих проектов (тот же состав,
    // что CuratorOf в ChildTasksTests) — проверка «взрослому ручки проходят» идёт живым путём.
    private HttpClient CuratorOf(Project project)
    {
        var adult = Users.FindByUsername(TestWebApplicationFactory.TestUsername)!;
        adult.FeatureFlags = new Dictionary<string, bool> { [FeatureFlagKeys.SharedProjects] = true };
        Projects.SetMembers(project.Id, [new ProjectMember
        {
            UserId = adult.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId!,
        }]);
        return _host.CreateAuthenticatedClient();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync());

    // --- свой проект-предмета: обе ручки 200 -------------------------------------------

    // Мутация «снять [ChildAllowed] на GetAll» — этот тест краснеет (403 child_mode вместо 200).
    [Fact]
    public async Task СвойПредмет_СписокЧатов_200()
    {
        var (_, child, subject) = NewChild("ownlist");
        var resp = await child.GetAsync($"/api/projects/{subject.Id}/sessions");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "список чатов своего проекта-предмета — открыт ребёнку (метка + своя граница)");
        // Своего списка уроков здесь ещё нет: он читается через ChildChatsController (api/child/spaces),
        // этот ответ — ровно то, что видно в проекте (созданных чатов нет).
        (await JsonAsync(resp)).EnumerateArray().Should().BeEmpty();
    }

    // Мутация «снять [ChildAllowed] на Services» — этот тест краснеет (403 child_mode вместо 200).
    [Fact]
    public async Task СвойПредмет_СписокСервисов_200()
    {
        var (_, child, subject) = NewChild("ownsvc");
        var resp = await child.GetAsync($"/api/projects/{subject.Id}/services");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "список сервисов своего проекта-предмета — открыт ребёнку (тот же образец, что список чатов)");
        (await JsonAsync(resp)).GetProperty("services").EnumerateArray().Should().BeEmpty(
            "в папке предмета манифестов запуска нет — список пустой, но сама ручка открыта");
    }

    // --- чужой проект-предмета: 403 not_child_subject — по отдельному кейсу на ручку ----
    // Х-W4-47 (хвост ревью e748c039): раньше обе ручки проверялись в одном методе, и падение
    // первой скрывало вторую. Каждый кейс стоит сам по себе: своя фикстура, своя ручка.

    // Мутация «снять детскую проверку в GetAll» — этот тест краснеет (200 вместо 403).
    [Fact]
    public async Task ЧужойПредмет_СписокЧатов_403_NotChildSubject()
    {
        var (kidId, child, _) = NewChild("foreign-list");
        var (otherId, _, otherSubject) = NewChild("other-list");
        MakeVisible(kidId, otherId, otherSubject);

        var sessions = await child.GetAsync($"/api/projects/{otherSubject.Id}/sessions");
        sessions.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "чужой проект-предмета — не свой; видимость есть, отказывает детская проверка");
        (await JsonAsync(sessions)).GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // Мутация «снять детскую проверку в Services» — этот тест краснеет (200 вместо 403).
    [Fact]
    public async Task ЧужойПредмет_СписокСервисов_403_NotChildSubject()
    {
        var (kidId, child, _) = NewChild("foreign-svc");
        var (otherId, _, otherSubject) = NewChild("other-svc");
        MakeVisible(kidId, otherId, otherSubject);

        var services = await child.GetAsync($"/api/projects/{otherSubject.Id}/services");
        services.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "список сервисов чужого проекта-предмета — не свой; видимость есть, отказывает детская проверка");
        (await JsonAsync(services)).GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // --- свой обычный проект: 403 not_child_subject — тоже по кейсу на ручку -------------

    // Мутация «снять условие IsChildSubject в IsOwnSubjectProject» — этот тест краснеет:
    // одно владение пропускает обычный проект (200 вместо 403).
    [Fact]
    public async Task СвойОбычныйПроект_СписокЧатов_403_NotChildSubject()
    {
        var (kidId, child, _) = NewChild("regular-list");
        var regular = RegularProjectOf(kidId, "regular-list");

        var sessions = await child.GetAsync($"/api/projects/{regular.Id}/sessions");
        sessions.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "обычный проект — не проект-предмета: список чатов ребёнку закрыт и в своём проекте");
        (await JsonAsync(sessions)).GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // Мутация «снять условие IsChildSubject в IsOwnSubjectProject» — и этот тест краснеет.
    [Fact]
    public async Task СвойОбычныйПроект_СписокСервисов_403_NotChildSubject()
    {
        var (kidId, child, _) = NewChild("regular-svc");
        var regular = RegularProjectOf(kidId, "regular-svc");

        var services = await child.GetAsync($"/api/projects/{regular.Id}/services");
        services.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "обычный проект — не проект-предмета: список сервисов ребёнку закрыт и в своём проекте");
        (await JsonAsync(services)).GetProperty("code").GetString().Should().Be("not_child_subject");
    }

    // --- взрослому ручки проходят без изменений ------------------------------------------

    [Fact]
    public async Task ВзрослыйКуратор_ОбеРучки_200_НаПроектеРебёнка()
    {
        var (_, _, subject) = NewChild("curator");
        using var curator = CuratorOf(subject);

        var sessions = await curator.GetAsync($"/api/projects/{subject.Id}/sessions");
        sessions.StatusCode.Should().Be(HttpStatusCode.OK,
            "взрослый участник (куратор) читает список чатов проекта как раньше");

        var services = await curator.GetAsync($"/api/projects/{subject.Id}/services");
        services.StatusCode.Should().Be(HttpStatusCode.OK, "и список сервисов — как раньше");
    }

    // --- метка ровно на списках: прочие ручки классов закрыты гейтом --------------------

    [Fact]
    public async Task СозданиеЧатаИСтартСервиса_Ребёнку_403_ChildMode()
    {
        var (_, child, subject) = NewChild("closed");

        var create = await child.PostAsync($"/api/projects/{subject.Id}/sessions",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "создание чата в проекте-предмете — только детская точка (POST api/child/spaces/…/chat); " +
            "эта ручка [ChildAllowed] не имеет — её отсекает гейт И1 до тела");
        (await create.Content.ReadAsStringAsync()).Should().Contain("child_mode",
            "отказ отдаёт гейт И1 до тела ручки (ChildModeGateMiddleware)");

        var start = await child.PostAsync($"/api/projects/{subject.Id}/preview/start",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        start.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await start.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }
}
