using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

// Членство в общем проекте (shared-projects, S1-2): шесть отказов с кодами и нетронутым
// projects.json, права владельца/участника/постороннего, git init при первом приглашении.
[Trait("Category", "Host")]
public class ProjectMembersControllerTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public ProjectMembersControllerTests(TestWebApplicationFactory factory)
    {
        _factory = factory;
        // Рубильник инстанса включаем до первого запроса (хост собирается лениво)
        _factory.ExtraConfig[SharedProjectsGate.SwitchKey] = "true";
    }

    private HttpClient Owner() => _factory.CreateAuthenticatedClient();

    private HttpClient Second() => _factory.CreateAuthenticatedClient(
        TestWebApplicationFactory.SecondUsername, TestWebApplicationFactory.SecondPassword);

    private string IdOf(string username) =>
        _factory.Services.GetRequiredService<UserStore>().FindByUsername(username)!.Id;

    private static async Task SetFlagAsync(HttpClient client, bool on) =>
        (await client.PutAsJsonAsync("/api/feature-flags/shared-projects", new { enabled = on }))
            .EnsureSuccessStatusCode();

    private async Task<string> CreateProjectAsync(HttpClient client, string? dir = null)
    {
        dir ??= Path.Combine(_factory.TempDir, "members_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var response = await client.PostAsJsonAsync("/api/projects", new { name = "Общий", rootPath = dir });
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
    }

    // ── Б14b: фикстура проекта-предмета ───────────────────────────────────────
    //
    // Ребёнок заводится живым UserStore.Add (тот же путь, что у ChildCuratorSyncTests),
    // проект-предмет — ProjectManager.Create с маркером childSubjectId (тот же приём, что у
    // ChildTutorPersonaDoorsTests). REST-создание такого проекта нет: маркер ставит провижер
    // предмета, не POST /api/projects. Куратор вписывается SetMembers напрямую — ручное
    // приглашение для проекта-предмета именно 409 (Б14b), а синк здесь не при чём: тесты
    // проверяют ручки членства, а не состав.

    private UserStore Users => _factory.Services.GetRequiredService<UserStore>();
    private ProjectManager Projects => _factory.Services.GetRequiredService<ProjectManager>();

    private const string KidPassword = "kid-pw-123456";

    private async Task<(string ProjectId, string KidId, string CuratorId, HttpClient Kid)>
        ArrangeSubjectProjectAsync()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        await SetFlagAsync(Owner(), true);
        await SetFlagAsync(Second(), true);

        var kid = Users.Add("kid.members." + run, KidPassword, "user");
        var dir = Path.Combine(_factory.TempDir, "subject_" + run);
        var project = Projects.Create("Предмет " + run, dir, kid.Id, kid.Username,
            createDirectory: true, childSubjectId: "math");
        ProjectCapabilities.IsChildSubject(project).Should().BeTrue("фитчюра — проект-предмет по маркеру");

        // Куратор-опекун — участник Editor (состав, какой оставила бы синхронизация Б14a)
        Projects.SetMembers(project.Id, [
            new ProjectMember { UserId = IdOf(TestWebApplicationFactory.SecondUsername), Role = ProjectRole.Editor, AddedAt = DateTime.UtcNow },
        ]);

        // Владелец-ребёнок входит по своему паролю
        var kidClient = _factory.CreateAuthenticatedClient(kid.Username, KidPassword);
        return (project.Id, kid.Id, IdOf(TestWebApplicationFactory.SecondUsername), kidClient);
    }

    // 409 с машинным кодом + состав участников не тронут (одна проверка на каждый кейс)
    private async Task AssertSubjectRefusalAsync(Func<Task<HttpResponseMessage>> call,
        string projectId, Func<List<ProjectMember>> expected)
    {
        var before = expected();
        var r = await call();
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await r.Content.ReadAsStringAsync();
        body.Should().Contain("managed_by_child_profile");
        (await CodeOf(r)).Should().Be("managed_by_child_profile");
        expected().Should().BeEquivalentTo(before, "отказанная ручка не оставила следа в членстве");
    }

    private string StoreHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_factory.TempDir, "projects.json"))));

    private static async Task<string?> CodeOf(HttpResponseMessage r) =>
        JsonSerializer.Deserialize<JsonElement>(await r.Content.ReadAsStringAsync()) is { ValueKind: JsonValueKind.Object } j
        && j.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static Task<HttpResponseMessage> AddAsync(HttpClient c, string projectId, string username, string role = "editor") =>
        c.PostAsJsonAsync($"/api/projects/{projectId}/members", new { username, role });

    // Отказ: свой статус и код, projects.json не изменился
    private async Task AssertRefusalAsync(Func<Task<HttpResponseMessage>> call, HttpStatusCode status, string code)
    {
        var before = StoreHash();
        var r = await call();
        r.StatusCode.Should().Be(status);
        (await CodeOf(r)).Should().Be(code);
        StoreHash().Should().Be(before);
    }

    [Fact]
    public async Task Добавление_РедакторПоявляетсяВСписке()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);

        var r = await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername);

        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!;
        project.Members.Should().ContainSingle(m => m.UserId == IdOf(TestWebApplicationFactory.SecondUsername));
    }

    [Fact]
    public async Task СбойEnsureПриПриглашении_ПриглашениеПродолжаетсяБез500()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var dir = Path.Combine(_factory.TempDir, "members_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // Репозиторий с шаблоном заранее: .gitignore содержит наш блок, Ensure доходит до
        // записи. Сам InitAsync при этом — no-op (файл есть), чтобы тест ломал ровно Ensure.
        await _factory.Services.GetRequiredService<GitService>().InitAsync(null, dir);
        var id = await CreateProjectAsync(owner, dir);

        // Ломается ровно снятие игнора заметок: .gitignore — только для чтения. Чтение
        // удаётся, запись даёт UnauthorizedAccess (и на Linux, и на Windows: атрибут
        // ReadOnly .NET снимает бит записи). До правки этот сбой уносил всё приглашение
        // 500 git_init_failed (п.2 возврата ревью bc33193f): журнал заметок вторичен
        // членству — теперь Warning, и участник входит.
        File.SetAttributes(Path.Combine(dir, ".gitignore"), FileAttributes.ReadOnly);

        var r = await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername);

        r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!;
        project.Members.Should().ContainSingle(m => m.UserId == IdOf(TestWebApplicationFactory.SecondUsername));
    }

    [Fact]
    public async Task ОтзывУчастника_ПападаетВFormerMembers_ВДанныхПроекта()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

        (await owner.DeleteAsync($"/api/projects/{id}/members/{secondId}")).EnsureSuccessStatusCode();

        var dto = JsonSerializer.Deserialize<JsonElement>(await (await owner.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        dto.GetProperty("formerMembers").EnumerateArray().Should().ContainSingle(f => f.GetProperty("userId").GetString() == secondId);
    }

    [Fact]
    public async Task ПовторноеПриглашение_СнимаетЧеловекаИзFormerMembers()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/projects/{id}/members/{secondId}")).EnsureSuccessStatusCode();
        _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!.FormerMembers.Should().NotBeNull();

        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!;
        project.FormerMembers.Should().BeNull("вернувшийся участник больше не «бывший»");
        project.Members.Should().ContainSingle(m => m.UserId == secondId);
    }

    [Fact]
    public async Task УходУчастника_ПападаетВFormerMembers()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

        (await Second().DeleteAsync($"/api/projects/{id}/members/me")).EnsureSuccessStatusCode();

        var project = _factory.Services.GetRequiredService<ProjectManager>().GetById(id)!;
        project.Members.Should().BeNullOrEmpty();
        project.FormerMembers.Should().ContainSingle(f => f.UserId == secondId);
    }

    [Fact]
    public async Task FormerMembers_ПосторонникуНет404_ЛичныйПроектБезПоля()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        var personalId = await CreateProjectAsync(owner);

        (await Second().GetAsync($"/api/projects/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "посторонний (ни владелец, ни участник) проект не видит — formerMembers не раскрывается");
        var personal = JsonSerializer.Deserialize<JsonElement>(await (await owner.GetAsync($"/api/projects/{personalId}")).Content.ReadAsStringAsync());
        personal.TryGetProperty("formerMembers", out var fm).Should().BeTrue();
        fm.ValueKind.Should().Be(JsonValueKind.Null, "у личного проекта бывших участников нет");
        secondId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Отказ_UserNotFound()
    {
        var owner = Owner(); await SetFlagAsync(owner, true);
        var id = await CreateProjectAsync(owner);
        await AssertRefusalAsync(() => AddAsync(owner, id, "nobody_such"), HttpStatusCode.NotFound, "user_not_found");
    }

    [Fact]
    public async Task Отказ_AlreadyMember()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "already_member");
    }

    [Fact]
    public async Task Отказ_Self()
    {
        var owner = Owner(); await SetFlagAsync(owner, true);
        var id = await CreateProjectAsync(owner);
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.TestUsername),
            HttpStatusCode.BadRequest, "self");
    }

    [Fact]
    public async Task Отказ_FlagDisabled_УПолучателя()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), false);
        var id = await CreateProjectAsync(owner);
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "flag_disabled");
    }

    [Fact]
    public async Task Отказ_LocalProject()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var dir = Path.Combine(_factory.TempDir, "local_" + Guid.NewGuid().ToString("N")[..8]);
        var local = _factory.Services.GetRequiredService<ProjectManager>()
            .CreateLocal("Локальный", dir, IdOf(TestWebApplicationFactory.TestUsername), "device-1");
        await AssertRefusalAsync(() => AddAsync(owner, local.Id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "local_project");
    }

    [Fact]
    public async Task Отказ_РубильникВыключен_503()
    {
        using var off = new TestWebApplicationFactory();
        off.ExtraConfig[SharedProjectsGate.SwitchKey] = "false";
        var owner = off.CreateAuthenticatedClient();
        await SetFlagAsync(owner, true);
        var dir = Path.Combine(off.TempDir, "off_proj"); Directory.CreateDirectory(dir);
        var created = await owner.PostAsJsonAsync("/api/projects", new { name = "Закрытый", rootPath = dir });
        var id = JsonSerializer.Deserialize<JsonElement>(await created.Content.ReadAsStringAsync()).GetProperty("id").GetString()!;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(off.TempDir, "projects.json"))));

        var r = await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername);

        r.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await CodeOf(r)).Should().Be("shared_projects_disabled");
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(off.TempDir, "projects.json")))).Should().Be(hash);
    }

    [Fact]
    public async Task УчастникНаУправлении_403_Посторонний_404()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        var strangerProject = await CreateProjectAsync(owner); // второй в нём не участник
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();
        var member = Second();

        // участник: добавить, сменить роль, убрать — 403
        (await AddAsync(member, id, TestWebApplicationFactory.TestUsername)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PutAsJsonAsync($"/api/projects/{id}/members/{secondId}", new { role = "reader" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.DeleteAsync($"/api/projects/{id}/members/{secondId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // посторонний — 404 на всё, включая «покинуть»
        (await AddAsync(member, strangerProject, "x")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.PutAsJsonAsync($"/api/projects/{strangerProject}/members/{secondId}", new { role = "reader" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await member.DeleteAsync($"/api/projects/{strangerProject}/members/me")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task СменаРоли_Убрать_Покинуть()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);
        var projects = _factory.Services.GetRequiredService<ProjectManager>();
        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

        (await owner.PutAsJsonAsync($"/api/projects/{id}/members/{secondId}", new { role = "reader" })).EnsureSuccessStatusCode();
        projects.GetById(id)!.Members!.Single().Role.Should().Be(ClaudeHomeServer.Services.Access.ProjectRole.Reader);

        (await Second().DeleteAsync($"/api/projects/{id}/members/me")).EnsureSuccessStatusCode();
        projects.GetById(id)!.Members.Should().BeNull();

        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername, "reader")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/projects/{id}/members/{secondId}")).EnsureSuccessStatusCode();
        projects.GetById(id)!.Members.Should().BeNull();
    }

    [Fact]
    public async Task ПервоеДобавление_СоздаётГитРепозиторий()
    {
        var owner = Owner(); await SetFlagAsync(owner, true); await SetFlagAsync(Second(), true);
        var dir = Path.Combine(Path.GetTempPath(), "members_git_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var id = await CreateProjectAsync(owner, dir);
            Directory.Exists(Path.Combine(dir, ".git")).Should().BeFalse();

            (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

            Directory.Exists(Path.Combine(dir, ".git")).Should().BeTrue();
        }
        finally { try { Directory.Delete(dir, true); } catch { /* не критично */ } }
    }

    [Fact]
    public async Task УдалениеВладельца_ПриЖивыхУчастниках_Отказ()
    {
        var second = Second(); var admin = Owner();
        await SetFlagAsync(second, true); await SetFlagAsync(admin, true);
        var id = await CreateProjectAsync(second);
        (await AddAsync(second, id, TestWebApplicationFactory.TestUsername)).EnsureSuccessStatusCode();

        var r = await admin.DeleteAsync($"/api/users/{IdOf(TestWebApplicationFactory.SecondUsername)}");

        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(r)).Should().Be("owner_has_members");
    }

    [Fact]
    public async Task НетПоискаУчётокПоЧастиИмени()
    {
        var owner = Owner(); await SetFlagAsync(owner, true);
        var id = await CreateProjectAsync(owner);
        (await owner.GetAsync($"/api/projects/{id}/members/sec")).IsSuccessStatusCode.Should().BeFalse();
    }

    [Fact]
    public async Task СписокУчастников_ВладелецИУчастникВидятВладельцаИМоюРоль_ПосторонийДаёт404()
    {
        var owner = Owner(); var second = Second();
        await SetFlagAsync(owner, true); await SetFlagAsync(second, true);
        var id = await CreateProjectAsync(owner);

        var asOwner = JsonSerializer.Deserialize<JsonElement>(await (await owner.GetAsync($"/api/projects/{id}/members")).Content.ReadAsStringAsync());
        asOwner.GetProperty("myRole").GetString().Should().Be("owner");
        asOwner.GetProperty("members").GetArrayLength().Should().Be(0);
        asOwner.GetProperty("owner").GetProperty("userId").GetString().Should().Be(IdOf(TestWebApplicationFactory.TestUsername));

        // Посторонний до приглашения — 404
        (await second.GetAsync($"/api/projects/{id}/members")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var add = await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername, "reader");
        var addJson = JsonSerializer.Deserialize<JsonElement>(await add.Content.ReadAsStringAsync());
        addJson.GetProperty("members")[0].GetProperty("username").GetString().Should().Be(TestWebApplicationFactory.SecondUsername);

        var asMember = JsonSerializer.Deserialize<JsonElement>(await (await second.GetAsync($"/api/projects/{id}/members")).Content.ReadAsStringAsync());
        asMember.GetProperty("myRole").GetString().Should().Be("reader");
        asMember.GetProperty("members")[0].GetProperty("role").GetString().Should().Be("reader");
        asMember.GetProperty("members")[0].TryGetProperty("addedAt", out _).Should().BeTrue();
    }

    // ── Б14b (ADR-041 ред. 6, решение 62, разрез §12): ручки членства проекта-предмета ──
    //
    // Членство проекта-предмета пишет только синхронизация профиля (Б14a): Add/ChangeRole/
    // Remove/Leave отвечают 409 managed_by_child_profile для любого вызывающего — владельца-
    // ребёнка и куратора-участника. Мутации, на которых эти три теста краснеют: убрать
    // проверку в ChangeRoleAsync → 403 not_owner вместо 409 (кейс куратора); убрать её в
    // DropAsync → 403 (кейс Remove) и 400 owner_cannot_leave вместо 409 (кейс Leave).
    // Кейс Add краснеет и без проверки в AddAsync: ребёнок-владелец получил бы 409
    // flag_disabled (флаг «Общих проектов» его учётки выключен) вместо кода границы.

    [Fact]
    public async Task ПроектПредмета_Пригласить_409_ManagedByChildProfile()
    {
        var (id, _, _, kid) = await ArrangeSubjectProjectAsync();
        // Вызывающий — владелец-ребёнок БЕЗ флага «Общих проектов»: без проверки в AddAsync
        // он получил бы 409 flag_disabled — код границы должен быть раньше флага.
        await AssertSubjectRefusalAsync(
            () => AddAsync(kid, id, TestWebApplicationFactory.SecondUsername),
            id, () => Projects.GetById(id)!.Members!);
    }

    [Fact]
    public async Task ПроектПредмета_СменитьРоль_409_ВладельцемИКуратором()
    {
        var (id, _, curatorId, kid) = await ArrangeSubjectProjectAsync();
        await AssertSubjectRefusalAsync(
            () => kid.PutAsJsonAsync($"/api/projects/{id}/members/{curatorId}", new { role = "reader" }),
            id, () => Projects.GetById(id)!.Members!);
        // Куратор-участник (не владелец): без проверки в ChangeRoleAsync — 403 not_owner
        await AssertSubjectRefusalAsync(
            () => Second().PutAsJsonAsync($"/api/projects/{id}/members/{curatorId}", new { role = "reader" }),
            id, () => Projects.GetById(id)!.Members!);
        Projects.GetById(id)!.Members!.Single().Role.Should().Be(ProjectRole.Editor,
            "роль куратора остаётся той, что поставила синхронизация (решение 60)");
    }

    [Fact]
    public async Task ПроектПредмета_УбратьИПокинуть_409_ВладельцемИКуратором()
    {
        var (id, _, curatorId, kid) = await ArrangeSubjectProjectAsync();
        // Убрать (вызывает владелец-ребёнок): без проверки в DropAsync — 404 not_member
        await AssertSubjectRefusalAsync(
            () => kid.DeleteAsync($"/api/projects/{id}/members/{curatorId}"),
            id, () => Projects.GetById(id)!.Members!);
        // Покинуть (вызывает куратор): без проверки в DropAsync — 200, куратор выпал бы из состава
        await AssertSubjectRefusalAsync(
            () => Second().DeleteAsync($"/api/projects/{id}/members/me"),
            id, () => Projects.GetById(id)!.Members!);
        // Покинуть для владельца-ребёнка: без DropAsync — 400 owner_cannot_leave вместо кода границы
        var r = await kid.DeleteAsync($"/api/projects/{id}/members/me");
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CodeOf(r)).Should().Be("managed_by_child_profile");
    }

    [Fact]
    public async Task ОбычныйПроект_ТеЖеРучки_ПрежниеКоды()
    {
        // c2: граница не течёт за пределы проекта-предмета — те же четыре операции
        // в обычном общем проекте работают как раньше (существующие кейсы этих кодов
        // зелёные без правок: flag_disabled — Отказ_FlagDisabled_УПолучателя, 403/404 —
        // УчастникНаУправлении_403_Посторонний_404, owner_cannot_leave — здесь)
        var owner = Owner(); var second = Second();
        await SetFlagAsync(owner, true); await SetFlagAsync(second, true);
        var id = await CreateProjectAsync(owner);
        var secondId = IdOf(TestWebApplicationFactory.SecondUsername);

        // владелец без включённого флага — прежний flag_disabled
        await SetFlagAsync(owner, false);
        await AssertRefusalAsync(() => AddAsync(owner, id, TestWebApplicationFactory.SecondUsername),
            HttpStatusCode.Conflict, "flag_disabled");
        await SetFlagAsync(owner, true);

        // владелец-ребёнка здесь нет — «покинуть» для владельца остаёт прежним 400
        (await owner.DeleteAsync($"/api/projects/{id}/members/me"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync($"/api/projects/{id}/members/{secondId}", new { role = "reader" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "участника ещё нет — прежний not_member");

        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync($"/api/projects/{id}/members/{secondId}", new { role = "reader" }))
            .EnsureSuccessStatusCode();
        Projects.GetById(id)!.Members!.Single().Role.Should().Be(ProjectRole.Reader);
        (await owner.DeleteAsync($"/api/projects/{id}/members/{secondId}")).EnsureSuccessStatusCode();
        Projects.GetById(id)!.Members.Should().BeNull();
    }

    [Fact]
    public async Task DtoПроекта_ОбычныйБезУчастников_ПолейНет_ОбщийСОбеихСторон_Есть()
    {
        var owner = Owner(); var second = Second();
        await SetFlagAsync(owner, true); await SetFlagAsync(second, true);
        var id = await CreateProjectAsync(owner);

        var plain = JsonSerializer.Deserialize<JsonElement>(await (await owner.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        plain.GetProperty("members").ValueKind.Should().Be(JsonValueKind.Null);
        plain.GetProperty("myRole").ValueKind.Should().Be(JsonValueKind.Null);

        (await AddAsync(owner, id, TestWebApplicationFactory.SecondUsername)).EnsureSuccessStatusCode();

        var asMember = JsonSerializer.Deserialize<JsonElement>(await (await second.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        asMember.GetProperty("myRole").GetString().Should().Be("editor");
        asMember.GetProperty("ownerUsername").GetString().Should().Be(TestWebApplicationFactory.TestUsername);
        asMember.GetProperty("members").GetProperty("count").GetInt32().Should().Be(1);

        var asOwner = JsonSerializer.Deserialize<JsonElement>(await (await owner.GetAsync($"/api/projects/{id}")).Content.ReadAsStringAsync());
        asOwner.GetProperty("myRole").GetString().Should().Be("owner");
    }
}
