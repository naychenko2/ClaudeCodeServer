using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.ImageEditor;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// К2c-кейсы каталога материалов ребёнка сняты Х-W4-19 (хвост решения 68); сам класс стора
/// удалён Х-W4-28 — имя этого файла больше про материалы ничего не говорит: здесь только
/// каскад проектов-предметов.
///
/// Б8c (ADR-041 ред. 5, разрез §11.1): ручки каскада удаления ребёнка — проекты-предметы.
/// Удаление ребёнка сносит их штатным ProjectManager.Delete; удаление/выключение предмета проект
/// НЕ трогает. Кейсы: снос каскадом (чужой ребёнок цел), «старый» проект удалённого предмета
/// (страховка по маркеру), гонка удаления с провижном (20 повторов), сбой записи проекта
/// (отдельный класс ниже — best-effort, ответ 204). Мутация (критерий c2): убрать каскад
/// проектов из UsersController.Delete — краснеет
/// УдалениеРебёнка_СнитаетПроектыПредметов_ЧужогоНеТрогает (проекты остаются).
/// </summary>
[Trait("Category", "Host")]
public class UsersDeleteChildSubjectProjectsTests : IClassFixture<UsersDeleteChildSubjectProjectsTests.Hosts>
{
    public sealed class Hosts : TestWebApplicationFactory
    {
        // Корень песочницы — на фикстуре, а не на тест-классе (тот же порядок, что у
        // UsersChildProfileTests:42): хост один на класс, а проект изолированного владельца
        // обязан лежать внутри того корня, который получил хост (ProjectManager.EnsureRootAllowed).
        internal readonly string SandboxRoot = Path.Combine(Path.GetTempPath(), "ccs-kidproj-" + Guid.NewGuid().ToString("N"));

        // Запись пула с ApiKey для PUT child-profile (тот же приём, что у UsersChildProfileTests):
        // без неё валидный отказ provider_not_api_key срабатывал бы на валидном теле.
        // Sandbox:ProjectsRoot — дом container-ребёнка (UserHomeResolver.SubjectHome): без него
        // провижер проекта-предмета отказывает «база не настроена» и кейсы Б8c не построются.
        public Hosts()
        {
            ExtraConfig["ClaudeSubscriptions:kids-api:ApiKey"] = "sk-ant-test-kids";
            ExtraConfig["Sandbox:ProjectsRoot"] = SandboxRoot;
        }
    }

    private readonly Hosts _hosts;
    private readonly UserStore _users;
    private readonly HttpClient _admin;

    public UsersDeleteChildSubjectProjectsTests(Hosts hosts)
    {
        _hosts = hosts;
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _admin = _hosts.CreateAuthenticatedClient();
    }

    // Ребёнок — новый пользователь с записанным в живой профиль ChildMode. Имена уникальны
    // на тест: стор пользователей общий на фикстуру.
    private string SetupChild(string username, params string[] subjectIds)
    {
        // Среда container: детский режим включается только container-пользователям
        // (ChildProfileValidator.cs:34), как в UsersChildProfileTests.NewContainerUser.
        var resp = _admin.PostAsJsonAsync("/api/users", new
        {
            username,
            password = "password12345",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        }).GetAwaiter().GetResult();
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = JsonSerializer.Deserialize<JsonElement>(resp.Content.ReadAsStringAsync()
            .GetAwaiter().GetResult()).GetProperty("id").GetString()!;

        _users.GetById(id)!.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
            Subjects = [.. subjectIds.Select(s => new ChildSubject
            {
                Id = s, Label = "Предмет " + s, Glyph = "Аа", Color = "info",
            })],
            RouteRiskAcknowledgedAt = ChildTestSubjects.ConsentAt, RouteRiskAcknowledgedBy = ChildTestSubjects.ConsentBy,
        };
        return id;
    }

    private static object SubjectBody(params string[] subjectIds) => new
    {
        enabled = true,
        acknowledgeRouteRisk = true,
        guardianUserIds = Array.Empty<string>(),
        providerKey = "kids-api",
        model = "sonnet",
        spaces = new Dictionary<string, object>(),
        subjects = subjectIds.Select(s => (object)new
        {
            id = s,
            label = "Предмет " + s,
            glyph = "Аа",
            color = "info",
            providerKey = "kids-api",
            model = "sonnet",
        }).ToArray(),
    };

    // ── Б8c: проекты-предметы каскада (ADR-041 ред. 5, разрез §11.1) ─────────────────────

    private ChildSubjectProjectProvisioner Provisioner =>
        _hosts.Services.GetRequiredService<ChildSubjectProjectProvisioner>();
    private ProjectManager Projects => _hosts.Services.GetRequiredService<ProjectManager>();

    // Предмет из ЖИВОГО профиля ребёнка — та же запись, которую боевая точка провижна
    // (PUT child-profile, Б17a) передаёт провижеру.
    private ChildSubject SubjectOf(string childId, string subjectId) =>
        _users.GetById(childId)!.ChildMode!.Subjects.First(s => s.Id == subjectId);

    [Fact]
    public async Task УдалениеРебёнка_СнитаетПроектыПредметов_ЧужогоНеТрогает()
    {
        var childA = SetupChild("kidproj-a", "math", "geo");
        var childB = SetupChild("kidproj-b", "math");
        // Проекты-предметы — через провижер (Б8b), боевую точку создания, а не рукописную запись.
        (await Provisioner.EnsureSubjectProjectAsync(childA, SubjectOf(childA, "math"))).Should().NotBeNull();
        (await Provisioner.EnsureSubjectProjectAsync(childA, SubjectOf(childA, "geo"))).Should().NotBeNull();
        (await Provisioner.EnsureSubjectProjectAsync(childB, SubjectOf(childB, "math"))).Should().NotBeNull();
        Projects.GetByOwner(childA).Count(p => p.ChildSubjectId is not null).Should().Be(2);

        var delete = await _admin.DeleteAsync($"/api/users/{childA}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        Projects.GetByOwner(childA).Should().BeEmpty("проекты-предметы ребёнка не должны остаться");
        Projects.GetByOwner(childB).Should().Contain(p => p.ChildSubjectId == "math",
            "проект другого ребёнка не тронут");
    }

    [Fact]
    public async Task УдалениеРебёнка_ЗадачаВПроектеПредмета_Снимается()
    {
        // Б8e: каскад идёт через единую уборку проекта, а не голый ProjectManager.Delete —
        // задача предмета (с напоминанием) не остаётся сиротой.
        var childId = SetupChild("kidproj-task", "math");
        var project = (await Provisioner.EnsureSubjectProjectAsync(childId, SubjectOf(childId, "math")))!;
        var tasks = _hosts.Services.GetRequiredService<TaskManager>();
        tasks.Create(project.Id, childId, new CreateTaskRequest("Решить задачи"));
        tasks.GetByProject(project.Id).Should().HaveCount(1);

        var delete = await _admin.DeleteAsync($"/api/users/{childId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        tasks.GetByProject(project.Id).Should().BeEmpty("задачи проекта-предмета снесены вместе с ним");
    }

    [Fact]
    public async Task УдалениеПредмета_ПроектЦел_УдалениеРебёнкаСнитаетЕго()
    {
        var childId = SetupChild("kidproj-subj");
        var put1 = await _admin.PutAsJsonAsync($"/api/users/{childId}/child-profile", SubjectBody("math", "geo"));
        put1.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Provisioner.EnsureSubjectProjectAsync(childId, SubjectOf(childId, "math"))).Should().NotBeNull();
        (await Provisioner.EnsureSubjectProjectAsync(childId, SubjectOf(childId, "geo"))).Should().NotBeNull();

        // PUT без math: удаление предмета проект и папку не трогает (ADR-041:595) —
        // они вернутся вместе с предметом, если его вернуть.
        var put2 = await _admin.PutAsJsonAsync($"/api/users/{childId}/child-profile", SubjectBody("geo"));
        put2.StatusCode.Should().Be(HttpStatusCode.OK);
        Projects.GetByOwner(childId).Count(p => p.ChildSubjectId == "math").Should().Be(1,
            "удаление предмета из профиля не трогает его проект");

        // Удаление ребёнка сносит и «старый» проект: math уже не в снимке профиля —
        // его берёт страховка каскада по маркеру.
        var delete = await _admin.DeleteAsync($"/api/users/{childId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Projects.GetByOwner(childId).Should().BeEmpty();
    }

    [Fact]
    public async Task УдалениеРебёнкаГонкаСПровижномПредмета_ПроектовНеОстаётся()
    {
        // Б8c, конкурентность: удаление ребёнка во время провижна предмета. Каскад входит
        // в per-ключевой замок провижнера (главный цикл контроллера), поэтому исход гонки
        // определён: либо проект создан до перечисления (каскад его снесёт), либо провижер
        // перечитал живой профиль после удаления (ребёнка нет → проекта нет). 20 повторов:
        // пересечение двух операций на одном не гарантировано.
        for (var iter = 0; iter < 20; iter++)
        {
            var childId = SetupChild($"kidproj-race{iter}", "math");
            var subject = SubjectOf(childId, "math");
            var provision = Task.Run(() => Provisioner.EnsureSubjectProjectAsync(childId, subject));
            var delete = _admin.DeleteAsync($"/api/users/{childId}");
            var project = await provision;
            var response = await delete;

            response.StatusCode.Should().Be(HttpStatusCode.NoContent, $"итерация {iter}");
            Projects.GetByOwner(childId).Should().BeEmpty(
                $"итерация {iter}: после гонки остались проекты-предметы " +
                $"(провижн вернул: {(project is null ? "ничего" : project.Id)})");
        }
    }
}

/// <summary>
/// Б8e, инвариант «владелец из p.OwnerId»: админ удаляет ребёнка, а шов уборки обучающих фото
/// (биометрия) получает id РЕБЁНКА и проекта-предмета, но не id вызывающего админа.
/// </summary>
[Trait("Category", "Host")]
public class UsersDeleteChildPhotosOwnerTests : IClassFixture<UsersDeleteChildPhotosOwnerTests.PhotoHosts>
{
    internal sealed class RecordingCleaner : IProjectTrainingPhotoCleaner
    {
        public List<(string Owner, string Project)> Calls { get; } = [];
        public void PurgeProject(string ownerId, string projectId) { lock (Calls) Calls.Add((ownerId, projectId)); }
    }

    public sealed class PhotoHosts : TestWebApplicationFactory
    {
        internal readonly RecordingCleaner Cleaner = new();
        internal readonly string SandboxRoot = Path.Combine(Path.GetTempPath(), "ccs-kidproj-photo-" + Guid.NewGuid().ToString("N"));

        public PhotoHosts()
        {
            ExtraConfig["ClaudeSubscriptions:kids-api:ApiKey"] = "sk-ant-test-kids";
            ExtraConfig["Sandbox:ProjectsRoot"] = SandboxRoot;
            ExtraServices = s => s.AddSingleton<IProjectTrainingPhotoCleaner>(Cleaner);
        }
    }

    private readonly PhotoHosts _hosts;

    public UsersDeleteChildPhotosOwnerTests(PhotoHosts hosts) => _hosts = hosts;

    [Fact]
    public async Task УдалениеРебёнка_ФотоЧистятсяПоВладельцуПроекта_НеПоАдмину()
    {
        var users = _hosts.Services.GetRequiredService<UserStore>();
        var admin = _hosts.CreateAuthenticatedClient();
        var adminId = users.FindByUsername(TestWebApplicationFactory.TestUsername)!.Id;
        var resp = await admin.PostAsJsonAsync("/api/users", new
        {
            username = "kidphoto",
            password = "password12345",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var childId = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        users.GetById(childId)!.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
            Subjects = [new ChildSubject { Id = "math", Label = "Предмет math", Glyph = "Аа", Color = "info"}],
            RouteRiskAcknowledgedAt = ChildTestSubjects.ConsentAt, RouteRiskAcknowledgedBy = ChildTestSubjects.ConsentBy,
        };
        var project = (await _hosts.Services.GetRequiredService<ChildSubjectProjectProvisioner>()
            .EnsureSubjectProjectAsync(childId, users.GetById(childId)!.ChildMode!.Subjects[0]))!;

        var delete = await admin.DeleteAsync($"/api/users/{childId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _hosts.Cleaner.Calls.Should().ContainSingle().Which.Should().Be((childId, project.Id));
        _hosts.Cleaner.Calls.Should().NotContain(c => c.Owner == adminId);
    }
}

/// <summary>
/// Б8c, сбой проекта: модель отказа — запись стора проектов. После замены projects.json
/// каталогом ProjectManager.Save бросает на projects.Delete (ProjectManager.cs:958 →
/// JsonFileStore.Save на пути-каталоге). Выбор каскада — best-effort, не 500 (тот же
/// приём, что у соседних звеньев Delete): catch звена — лог, продолжение; оба проекта
/// сняты из памяти стора, пользователь удалён, ответ тот же 204, необработанного
/// исключения нет. Свой хост: бой стора этого фикстура ломается только здесь.
/// </summary>
[Trait("Category", "Host")]
public class UsersDeleteChildSubjectProjectsBrokenTests : IClassFixture<UsersDeleteChildSubjectProjectsBrokenTests.BrokenProjectHosts>
{
    public sealed class BrokenProjectHosts : TestWebApplicationFactory
    {
        internal readonly string SandboxRoot = Path.Combine(Path.GetTempPath(), "ccs-kidproj-broken-" + Guid.NewGuid().ToString("N"));

        public BrokenProjectHosts()
        {
            ExtraConfig["ClaudeSubscriptions:kids-api:ApiKey"] = "sk-ant-test-kids";
            ExtraConfig["Sandbox:ProjectsRoot"] = SandboxRoot;
        }
    }

    private readonly BrokenProjectHosts _hosts;
    private readonly UserStore _users;
    private readonly HttpClient _admin;

    public UsersDeleteChildSubjectProjectsBrokenTests(BrokenProjectHosts hosts)
    {
        _hosts = hosts;
        _users = _hosts.Services.GetRequiredService<UserStore>();
        _admin = _hosts.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task СбойЗаписиПроекта_ОстальныеУдаляются_ПользовательУдалён_Ответ204()
    {
        var resp = await _admin.PostAsJsonAsync("/api/users", new
        {
            username = "kidproj-broken",
            password = "password12345",
            role = "user",
            executionEnvironment = ExecutionEnvironments.Container,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var childId = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync())
            .GetProperty("id").GetString()!;
        _users.GetById(childId)!.ChildMode = new ChildProfile
        {
            Enabled = true, TutorPersonaId = "tutor", ProviderKey = "key", Model = "sonnet",
            Subjects =
            [
                new ChildSubject { Id = "math", Label = "Предмет math", Glyph = "Аа", Color = "info"},
                new ChildSubject { Id = "geo", Label = "Предмет geo", Glyph = "Аа", Color = "info"},
            ],
            RouteRiskAcknowledgedAt = ChildTestSubjects.ConsentAt, RouteRiskAcknowledgedBy = ChildTestSubjects.ConsentBy,
        };
        var provisioner = _hosts.Services.GetRequiredService<ChildSubjectProjectProvisioner>();
        var projects = _hosts.Services.GetRequiredService<ProjectManager>();
        foreach (var subject in _users.GetById(childId)!.ChildMode!.Subjects)
            (await provisioner.EnsureSubjectProjectAsync(childId, subject)).Should().NotBeNull();

        // Отказ записи стора: файл проектов заменён каталогом — Save бросает на каждом
        // projects.Delete звена. Звено обязано это ловить и продолжать: второй проект
        // тоже снимается из памяти стора, учётка удаляется, ответ — 204.
        var storePath = Path.Combine(_hosts.TempDir, "projects.json");
        File.Delete(storePath);
        Directory.CreateDirectory(storePath);

        var delete = await _admin.DeleteAsync($"/api/users/{childId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "сбой проекта — лог и продолжение, не 500");
        _users.GetById(childId).Should().BeNull("пользователь удалён несмотря на сбой звена");
        projects.GetByOwner(childId).Should().BeEmpty("сбой первого проекта не остановил каскад");
    }
}
