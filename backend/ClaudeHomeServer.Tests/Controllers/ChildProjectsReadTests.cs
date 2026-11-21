using System.Net;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// Б21 («Проекты» ребёнку, ADR-041 ред. 8, решение 84): «дом» ребёнка — штатный док проектов
/// с фильтром «свои проекты-предметы», поэтому ребёнку открыты ровно список (GET /api/projects)
/// и карточка (GET /api/projects/{id}) — и ровно его проекты-предметы включённых предметов
/// профиля. Выключенный и удалённый из профиля предмет в док не отдаётся (решение 84:
/// «выключенный предмет в доке не показывается»; плитки о нём нет, а док отражает плитки);
/// обычный и чужой проект — тот же 404, что «не найдено» (существование остального ребёнку
/// не раскрываем). Видимость чужого (участник Reader + SharedProjects) не помогает:
/// ProjectScope её пропускает, отказывает детская проверка в контроллере.
///
/// Правка и удаление своего проекта — 403 child_mode: эти ручки [ChildAllowed] не имеют
/// намеренно, проекты ребёнка заводит и правит только взрослый (провизер предметов,
/// куратор профиля) — их отсекает гейт И1 до тела.
///
/// Ответ — тот же взрослый DTO (фронт использует штатный тип Project, новых полей нет):
/// в тесте это поля id/name/ownerId/childSubjectId карточки и capability-профиль
/// проекта-предмета. childSubjectId — поле модели (Б8a), а не детская надстройка DTO:
/// оно отдаётся и взрослому, у обычного проекта — null (дефект fab7e418, X-W4-76:
/// без него фронт не собирал activeSubject — не было кнопки «Новый урок»).
/// Взрослый путь не тронут: child-ветка включается только когда владелец — ребёнок.
///
/// Мутации (каждая краснит ровно названное):
/// • снять детский фильтр списка (вернуть GetAccessible) — СписокРебёнка краснеет
///   (выключенный предмет и обычный проект попадают в выдачу);
/// • снять детскую проверку карточки — ЧужойПроектПредмета краснеет (200 вместо 404:
///   видимость участника пропускает ProjectScope);
/// • снять ChildSubjectId из WithCount — СписокИКарточка_ОтдаютChildSubjectId краснеет
///   (GetProperty падает на отсутствующем поле).
/// </summary>
[Trait("Category", "Host")]
public class ChildProjectsReadTests : IClassFixture<ChildProjectsReadTests.ProjectsReadHost>
{
    private const string KidPassword = "b21-kid-pw-123456";
    private const string OnSubject = "matematika";
    private const string OffSubject = "geografiya";

    public sealed class ProjectsReadHost : TestWebApplicationFactory
    {
        public ProjectsReadHost()
        {
            // Чужой проект видим ребёнку только при рубильнике и флаге (SharedProjectsGate,
            // тот же порог, что в ChildKnowledgeReadTests): «не виден в списке» и 404 по id
            // должны быть отказом детской проверки, а не отсутствием видимости.
            ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
        }
    }

    private readonly ProjectsReadHost _host;

    public ChildProjectsReadTests(ProjectsReadHost host) => _host = host;

    private ProjectManager Projects => _host.Services.GetRequiredService<ProjectManager>();
    private UserStore Users => _host.Services.GetRequiredService<UserStore>();

    // ── фикстуры ────────────────────────────────────────────────────────────────────

    // Ребёнок с валидным профилем (тот же снимок, что SetChild в ChildModeEndpointGuardTests;
    // предметы — валидные slug'ы, иначе нечитаемый профиль = DenyAll и 403 ещё на гейте).
    // Живая IChildModePolicy читает его по стору: токен проходит гейт И1, и детская сверка
    // в контроллере видит ребёнка.
    private (string Id, string Username) Kid(string tag, bool withOffSubject = false)
    {
        var user = Users.Add($"b21-kid-{tag}", KidPassword, "user");
        var subjects = new List<ChildSubject> { Subject(OnSubject, "Математика") };
        if (withOffSubject) subjects.Add(Subject(OffSubject, "География", enabled: false));
        user.ChildMode = new ChildProfile
        {
            Enabled = true,
            TutorPersonaId = "tutor",
            ProviderKey = "key",
            Model = "sonnet",
            Subjects = subjects,
        };
        return (user.Id, user.Username);
    }

    private static ChildSubject Subject(string id, string label, bool enabled = true) =>
        new() { Id = id, Label = label, Glyph = "М", Color = "info", Enabled = enabled };

    private HttpClient Client(string username, string password = KidPassword) =>
        _host.CreateAuthenticatedClient(username, password);

    // Проект-предмета (маркер ChildSubjectId, та же форма, что в ChildKnowledgeReadTests)
    // и обычный проект — оба под указанным владельцем.
    private Project SubjectProject(string ownerId, string username, string tag, string subjectId = OnSubject) =>
        Projects.Create($"Предмет-{tag}", Path.Combine(_host.TempDir, "b21_" + tag), ownerId, username,
            createDirectory: true, childSubjectId: subjectId);

    private Project PlainProject(string ownerId, string username, string tag) =>
        Projects.Create($"Обычный-{tag}", Path.Combine(_host.TempDir, "b21_plain_" + tag), ownerId, username,
            createDirectory: true);

    // Чужой проект-предмета видим ребёнку (Reader-участник + флаг): дальше отказывает
    // только детская проверка контроллера.
    private Project VisibleForeignSubject(string kidId, string otherId, string otherName, string tag)
    {
        var foreign = SubjectProject(otherId, otherName, tag);
        Projects.SetMembers(foreign.Id,
            [new ProjectMember { UserId = kidId, Role = ProjectRole.Reader, AddedBy = otherId }]);
        Users.SetFeatureFlag(kidId, FeatureFlagKeys.SharedProjects, true);
        return foreign;
    }

    private static async Task<JsonDocument> BodyAsync(HttpResponseMessage resp) =>
        JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

    // ── список: ровно свои включённые проекты-предметы ─────────────────────────────

    [Fact]
    public async Task СписокРебёнка_ТолькоЕгоВключённыеПроектыПредметы()
    {
        var (kidId, kidName) = Kid("list", withOffSubject: true);
        var own = SubjectProject(kidId, kidName, "list-on");
        var off = SubjectProject(kidId, kidName, "list-off", OffSubject);
        var plain = PlainProject(kidId, kidName, "list-plain");
        var (otherId, otherName) = Kid("list-other");
        var foreign = VisibleForeignSubject(kidId, otherId, otherName, "list-foreign");

        using var kid = Client(kidName);
        var resp = await kid.GetAsync("/api/projects");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "список проектов открыт ребёнку (решение 84)");
        var ids = (await BodyAsync(resp)).RootElement.EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).ToList();
        ids.Should().BeEquivalentTo(new[] { own.Id },
            "ровно свой включённый проект-предмета: выключенный предмет {0}, обычный проект {1} и чужой проект-предмета {2} не видны",
            off.Id, plain.Id, foreign.Id);
    }

    // ── карточка своего проекта: 200, тот же взрослый DTO ───────────────────────────

    [Fact]
    public async Task КарточкаСвоегоПроекта_200_ТотЖеВзрослыйDTO()
    {
        var (kidId, kidName) = Kid("card");
        var own = SubjectProject(kidId, kidName, "card");
        using var kid = Client(kidName);

        var resp = await kid.GetAsync($"/api/projects/{own.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "карточка своего проекта-предмета открыта ребёнку");
        var p = (await BodyAsync(resp)).RootElement;
        p.GetProperty("id").GetString().Should().Be(own.Id);
        p.GetProperty("name").GetString().Should().Be("Предмет-card");
        p.GetProperty("ownerId").GetString().Should().Be(kidId, "OwnerId — часть контракта (ревью F2), ребёнку тоже");
        // Тот же capability-профиль, что у взрослого: док и WorkspacePage фронта читают штатный тип.
        // У проекта-предмета плагины выключены самой моделью (ProjectCapabilities.For → ForChildSubject) —
        // ребёнку отдаётся ровно то, что видит взрослый, без новых полей.
        p.GetProperty("capabilities").TryGetProperty("plugins", out var plugins)
            .Should().BeTrue(" capabilities проекта-предмета — тот же ответ, что у взрослого");
        plugins.GetProperty("available").GetBoolean().Should().BeFalse(
            "профиль проекта-предмета: плагины выключены моделью (ProjectCapabilities.PluginsFor), не новой детской веткой");
    }

    // ── маркер предмета в DTO (дефект fab7e418, X-W4-76) ───────────────────────────

    // openSubjectProject/activeSubject фронта ищут проект-предмета по childSubjectId;
    // без поля док ребёнка не собирал «Новый урок» и шапку урока.
    [Fact]
    public async Task СписокИКарточка_ОтдаютChildSubjectId_СвоегоПроектаПредмета()
    {
        var (kidId, kidName) = Kid("subject-id");
        var own = SubjectProject(kidId, kidName, "subject-id");

        using var kid = Client(kidName);

        var list = await kid.GetAsync("/api/projects");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = (await BodyAsync(list)).RootElement.EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == own.Id);
        item.GetProperty("childSubjectId").GetString().Should().Be(OnSubject,
            "activeSubject фронта находит проект-предмета по этому полю");

        var card = await kid.GetAsync($"/api/projects/{own.Id}");
        card.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(card)).RootElement.GetProperty("childSubjectId").GetString()
            .Should().Be(OnSubject, "карточка — тот же WithCount-DTO, поле и там");
    }

    // ── чужой, обычный и выключенный предмет: 404 ──────────────────────────────────

    [Fact]
    public async Task ЧужойПроектПредмета_Ребёнку_404()
    {
        var (kidId, kidName) = Kid("foreign");
        var (otherId, otherName) = Kid("foreign-other");
        var foreign = VisibleForeignSubject(kidId, otherId, otherName, "foreign");
        using var kid = Client(kidName);

        // Видимость есть (Reader + рубильник и флаг): ProjectScope её пропускает,
        // отказывает детская проверка — тот же 404, что «не найдено».
        var resp = await kid.GetAsync($"/api/projects/{foreign.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "чужой проект-предмета — не projects ребёнка");
    }

    [Fact]
    public async Task ОбычныйПроектРебёнка_Ребёнку_404()
    {
        var (kidId, kidName) = Kid("plain");
        var plain = PlainProject(kidId, kidName, "plain");
        using var kid = Client(kidName);

        var resp = await kid.GetAsync($"/api/projects/{plain.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "обычный проект — не проект-предмета, в док не входит");
    }

    [Fact]
    public async Task ВыключенныйПредмет_ВСпискеИКарточке_НеОтдаётся()
    {
        var (kidId, kidName) = Kid("off", withOffSubject: true);
        var off = SubjectProject(kidId, kidName, "off", OffSubject);
        using var kid = Client(kidName);

        var list = await kid.GetAsync("/api/projects");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(list)).RootElement.EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).Should().NotContain(off.Id,
                "выключенный предмет в доке не показывается (решение 84)");

        var card = await kid.GetAsync($"/api/projects/{off.Id}");
        card.StatusCode.Should().Be(HttpStatusCode.NotFound, "и по id выключенный предмет не отдаётся: плитки о нём нет");
    }

    // ── правка и удаление своего проекта: закрыты (гейт И1 до тела) ────────────────

    [Fact]
    public async Task ПравкаИУдалениеСвоегоПроекта_Ребёнку_403_ChildMode()
    {
        var (kidId, kidName) = Kid("write");
        var own = SubjectProject(kidId, kidName, "write");
        using var kid = Client(kidName);

        // Projects ребёнка заводит и правит только взрослый (провизер, куратор):
        // PUT/DELETE класса [ChildAllowed] не имеют намеренно.
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/projects/{own.Id}");
        put.Content = new StringContent("{\"name\":\"переименовать\"}", Encoding.UTF8, "application/json");
        var putResp = await kid.SendAsync(put);
        putResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await putResp.Content.ReadAsStringAsync()).Should().Contain("child_mode");

        var delResp = await kid.DeleteAsync($"/api/projects/{own.Id}");
        delResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await delResp.Content.ReadAsStringAsync()).Should().Contain("child_mode");
    }

    // ── взрослый путь не тронут ─────────────────────────────────────────────────────

    [Fact]
    public async Task ВзрослыйСписокИКарточка_КакРаньше_РядомСДетскимиПроектами()
    {
        var (kidId, kidName) = Kid("adult");
        var kidProject = SubjectProject(kidId, kidName, "adult");

        var adultName = "b21-adult";
        var adultPassword = "b21-adult-pw-123456";
        var adult = Users.Add(adultName, adultPassword, "user");
        var own = PlainProject(adult.Id, adultName, "adult-own");

        using var client = Client(adultName, adultPassword);
        var list = await client.GetAsync("/api/projects");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = (await BodyAsync(list)).RootElement.EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).ToList();
        ids.Should().Contain(own.Id, "свой обычный проект — виден, как раньше");
        ids.Should().NotContain(kidProject.Id, "проект чужого владельца без членства не виден, как раньше");

        var card = await client.GetAsync($"/api/projects/{own.Id}");
        card.StatusCode.Should().Be(HttpStatusCode.OK, "карточка своего проекта — без изменений");
    }

    // Обычный проект взрослого: childSubjectId есть в ответе и он null — как в модели
    // (сериализация null не вырезает, Program.cs:225 без DefaultIgnoreCondition);
    // фронт трактует null как обычный проект (types.ts: childSubjectId?: string | null).
    [Fact]
    public async Task ВзрослыйОбычныйПроект_ChildSubjectId_Null_КакВМодели()
    {
        var adultName = "b21-adult-null";
        var adultPassword = "b21-adult-null-pw-123456";
        var adult = Users.Add(adultName, adultPassword, "user");
        var own = PlainProject(adult.Id, adultName, "adult-null");

        using var client = Client(adultName, adultPassword);

        var list = await client.GetAsync("/api/projects");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = (await BodyAsync(list)).RootElement.EnumerateArray()
            .Single(p => p.GetProperty("id").GetString() == own.Id);
        item.GetProperty("childSubjectId").ValueKind.Should().Be(JsonValueKind.Null,
            "обычный проект: поле null, как в модели — лишнего взрослая выдача не несёт");

        var card = await client.GetAsync($"/api/projects/{own.Id}");
        card.StatusCode.Should().Be(HttpStatusCode.OK);
        (await BodyAsync(card)).RootElement.GetProperty("childSubjectId").ValueKind
            .Should().Be(JsonValueKind.Null);
    }
}
