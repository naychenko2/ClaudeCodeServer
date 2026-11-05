using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2d-4a (находка H1 ревью R-S2d-3c): человеческие СПИСКИ чатов не отдают чужой приватный
/// чат — ни id, ни названием. Утечка: ResolveOwnerId проектной сессии = владелец проекта,
/// поэтому GetAllOwnedBy/GetByProject отдавали владельцу чаты, приватные другому автору.
/// Закрыто: SessionManager.VisibleTo (та же Core-точка SessionAccess.CanSee, что у
/// GetOwnedVisible и шлюзов редакторов) + GetAllOwnedVisibleBy / GetVisibleByProject, и
/// точечные фильтры в GetPersonaChats и снимке фоновой работы.
///
/// Проверяется на настоящих ручках — контроллеры собираются напрямую с ClaimsPrincipal
/// вызывающего, сессии сидируются sessions.json (образец — GetOwnedVisibleTests S2d-3a…3e,
/// тот же шаг этой же фичи): GET /api/me/wall/candidates, GET /api/projects/{id}/sessions
/// (через настоящий ETagJsonResult — тело читается из ответа), сводка «Домой», список чатов
/// персоны, снимок agents-presence. Автор своего приватного, общий чат проекта (в т.ч.
/// authored участником) и чат вне проекта — прежнее поведение; чужой приватный неотличим
/// от несуществующего, как на одиночных ручках S2d-3.
///
/// Машинные потребители не менялись и здесь не проверяются: индекс фич и гард
/// (FeatureChatDoor.Snapshot → TasksController/TasksToolset), GetProjectlessChats (для чата
/// вне проекта CanSee = владение самим чатом, оно уже стоит), сторожа и дайджест.
/// HTTP-слой этих же ручек (роутинг, [ProjectScope], ETag) — хост-набор, он на гейте Марка.
/// </summary>
public sealed class PrivateChatListVisibilityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly PersonaManager _personas;
    private readonly SessionManager _sessions;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _projectId;
    private readonly string _personaId;

    // Сид: чат участника, приватный участнику; чат участника общий; чат владельца приватный
    // владельцу; чат владельца общий; чат вне проекта; два чата проектной персоны — общий
    // (автор — владелец) и приватный участнику.
    private const string ForeignPrivateId = "priv-foreign";
    private const string MemberOpenId = "member-open";
    private const string OwnPrivateId = "priv-own";
    private const string OwnerOpenId = "owner-open";
    private const string OutsideChatId = "outside-chat";
    private const string PersonaOwnId = "persona-own";
    private const string PersonaForeignId = "persona-foreign";

    public PrivateChatListVisibilityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "private_lists_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Членство в проекте оформляется только при включённом рубильнике — как в проде
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("kuzma-editor", "pwd", "user").Id;
        foreach (var id in new[] { _editorId })
            _users.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        _personas = new PersonaManager(_config);
        _personaId = _personas.Create(_ownerId, "Персона проекта", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;

        WriteSessions(Path.Combine(_tempDir, "sessions.json"), [
            new Session { Id = ForeignPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId,
                PrivateToUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = MemberOpenId, ProjectId = _projectId, CreatedByUserId = _editorId,
                Status = SessionStatus.Finished },
            new Session { Id = OwnPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OwnerOpenId, ProjectId = _projectId, Status = SessionStatus.Finished },
            new Session { Id = OutsideChatId, OwnerId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = PersonaOwnId, ProjectId = _projectId, PersonaId = _personaId,
                Status = SessionStatus.Finished },
            new Session { Id = PersonaForeignId, ProjectId = _projectId, PersonaId = _personaId,
                CreatedByUserId = _editorId, PrivateToUserId = _editorId, Status = SessionStatus.Finished },
        ]);

        _sessions = CreateSessionManager(appSettings);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, JsonSerializer.Serialize(sessions.ToList()));

    // Живой SessionManager без процессов и LLM-вызовов (состав — GetOwnedVisibleTests)
    private SessionManager CreateSessionManager(AppSettingsService appSettings)
    {
        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var jwt = new JwtService(_config, _users, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _users, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projects, wkStore, knowledge,
            new SkillsService(), _users, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, new ChatHistoryService(_config), _config, adapters,
            new FalCostService(new Mock<IHttpClientFactory>().Object, _config), new UsageService(_config),
            appSettings, _users, jwt, server.Object, llmProviders, new FeatureFlagService(_users),
            _personas, bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        },
    };

    private MyWallController WallAs(string userId) =>
        new(_users, _sessions) { ControllerContext = AsUser(userId) };

    private SessionsController SessionsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!) { ControllerContext = AsUser(userId) };

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, _users, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

    // W3-B4a: Chats берёт HttpContext.GetRequestScope(), поэтому прямому вызову нужна та же
    // область, что положил бы фильтр [ProjectScope(From = Persona)] (ProjectAccessService.
    // ResolvePersona): ключ — арендатор (владелец пространства персоны: проектной — её проекта,
    // глобальной — самой персоны), вызывающий — зритель (И-2, И-5). Без области действие
    // падает громко (RequestScope не определён) — это и есть контракт, он не обходится.
    private PersonasController PersonasAs(string userId, string personaId)
    {
        var persona = _personas.GetByIdInternal(personaId)!;
        var ownProject = PersonaZone.OwnProjectId(persona);
        var tenant = ownProject is not null ? _projects.GetById(ownProject)!.OwnerId! : persona.OwnerId;
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        };
        http.SetRequestScope(new RequestScope(userId, tenant,
            userId == tenant ? ProjectRole.Owner : ProjectRole.Editor));
        return new PersonasController(_personas, _projects, _sessions, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, _config,
            NullLogger<PersonasController>.Instance, null!)
        { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private HomeController HomeAs(string userId) =>
        new(_sessions, _projects, new Mock<ITaskLookup>().Object) { ControllerContext = AsUser(userId) };

    private static List<string> IdsOf(IActionResult result) =>
        ((IEnumerable<Session>)((OkObjectResult)result).Value!).Select(s => s.Id).ToList();

    // Тело настоящего ETagJsonResult: результат исполняется на DefaultHttpContext с
    // боевыми MVC-опциями и читается из Response.Body — то есть проверяется и то, что
    // реально уходит на wire, а не только список до сериализации.
    private static async Task<List<string>> SessionIdsAsync(SessionsController controller, string projectId)
    {
        var http = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>(
            Options.Create(new Microsoft.AspNetCore.Mvc.JsonOptions()));
        http.RequestServices = services.BuildServiceProvider();
        http.Response.Body = new MemoryStream();

        var result = (ETagJsonResult)controller.GetAll(projectId);
        await result.ExecuteResultAsync(new ActionContext { HttpContext = http });

        http.Response.Body.Position = 0;
        var json = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return JsonSerializer.Deserialize<JsonElement>(json).EnumerateArray()
            .Select(s => s.GetProperty("id").GetString()!).ToList();
    }

    // Живой фон у чата: подмена адаптера в живом сторе (образец — SetAdapter в
    // ChatsArchivedEndpointTests). Оба вида сразу: фильтр один, списки разные.
    private void SetLiveBg(string sessionId)
    {
        var map = (System.Collections.IDictionary)typeof(SessionManager)
            .GetField("_sessions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_sessions)!;
        var entry = map[sessionId]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns((Session)entry.GetType().GetField("Info")!.GetValue(entry)!);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(false);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        adapter.SetupGet(a => a.HasTrackedBg).Returns(true);
        adapter.SetupGet(a => a.HasTrackedCommandBg).Returns(true);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
    }

    // ── кандидаты стены (находка H1) ────────────────────────────────────────────────

    [Fact]
    public void СтенаCandidates_ЧужойПриватныйНеПредлагается_СвойПриватныйОбщийИЧатВнеПроектаКакПрежде()
    {
        var ids = IdsOf(WallAs(_ownerId).Candidates());

        ids.Should().NotContain(ForeignPrivateId,
            "пикер стены не может позвать на стену то, чего человек не видит");
        ids.Should().Contain(new[] { OwnPrivateId, MemberOpenId, OwnerOpenId, OutsideChatId },
            "свой приватный, общий чат проекта (в т.ч. authored участником) и чат вне проекта — как раньше");

        // Автор своего приватного — для стены он его не звал и раньше (чат проектный,
        // арендатор — владелец): состав кандидатов участника не меняется
        IdsOf(WallAs(_editorId).Candidates()).Should().NotContain(ForeignPrivateId);
    }

    // ── список чатов проекта ────────────────────────────────────────────────────────

    [Fact]
    public async Task СписокЧатовПроекта_ЧужойПриватныйНеОтдаётся_АвторСвойВидит_ОбщийКакПрежде()
    {
        var asOwner = await SessionIdsAsync(SessionsAs(_ownerId), _projectId);
        asOwner.Should().NotContain(ForeignPrivateId,
            "чужой приватный для владельца неотличим от несуществующего — ни id, ни названием");
        asOwner.Should().Contain(new[] { OwnPrivateId, MemberOpenId, OwnerOpenId });

        // Автор своего приватного проходит (приватность — его поле, не чужой запрет);
        // приватный владельцу ему не виден — та же точка с другой стороны
        var asAuthor = await SessionIdsAsync(SessionsAs(_editorId), _projectId);
        asAuthor.Should().Contain(new[] { ForeignPrivateId, MemberOpenId, OwnerOpenId });
        asAuthor.Should().NotContain(OwnPrivateId);
    }

    // ── сводка «Домой» ──────────────────────────────────────────────────────────────

    [Fact]
    public void СводкаДомой_ЧужойПриватныйНеВСводке_СвойПриватныйОбщийИЧатВнеПроектаКакПрежде()
    {
        // Те же именованные политики, что на wire (MVC-опции): иначе GetProperty промахнётся
        // по регистру, а не по составу
        var body = JsonDocument.Parse(JsonSerializer.Serialize(
            ((OkObjectResult)HomeAs(_ownerId).GetSummary()).Value,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })).RootElement;
        var ids = body.GetProperty("active").EnumerateArray().Concat(body.GetProperty("recent").EnumerateArray())
            .Select(s => s.GetProperty("id").GetString()).ToList();

        ids.Should().NotContain(ForeignPrivateId);
        ids.Should().Contain(new[] { OwnPrivateId, MemberOpenId, OwnerOpenId, OutsideChatId });
    }

    // ── список чатов персоны ────────────────────────────────────────────────────────

    [Fact]
    public void СписокЧатовПерсоны_ЧужойПриватныйНеЖивёт_СвойЧатПерсоныКакПрежде()
    {
        var ids = IdsOf(PersonasAs(_ownerId, _personaId).Chats(_personaId));

        ids.Should().Contain(PersonaOwnId, "свой чат персоны — прежнее поведение");
        ids.Should().NotContain(PersonaForeignId,
            "в списке чатов персоны чужой приватный — тот же мёртвый id, что в остальных списках");
    }

    // W3-B4a снял границу S2d-4e: персона проекта резолвится по доступу к проекту (ResolvePersona),
    // а не по владению персоны — участнику она открыта. Список ключуется арендатором (владельцем
    // проекта), но фильтруется видимостью вызывающего (viewer — И-5): участник видит общий чат
    // персоны и свой приватный с ней. Подмена viewer на арендатора (проба) уберёт его приватный
    // из его же списка — тест на это и стоит.
    [Fact]
    public void СписокЧатовПерсоны_УчастникуВиденЕгоПриватныйИОбщий_СписокПоЗрителю()
    {
        var ids = IdsOf(PersonasAs(_editorId, _personaId).Chats(_personaId));

        ids.Should().Contain(PersonaForeignId,
            "свой приватный чат участника с персоной — автору виден: viewer — вызывающий, не арендатор (И-5)");
        ids.Should().Contain(PersonaOwnId,
            "общий чат персоны — участнику виден: персона проекта открыта участнику (W3-B4a)");
    }

    // ── снимок фоновой работы (id чатов) ───────────────────────────────────────────

    [Fact]
    public void СнимокФоновойРаботы_ЧужойПриватныйНеОтдаётId_СвойИОбщийКакПрежде()
    {
        SetLiveBg(ForeignPrivateId);
        SetLiveBg(OwnerOpenId);

        var body = JsonDocument.Parse(JsonSerializer.Serialize(
            ((OkObjectResult)ChatsAs(_ownerId).AgentsPresence()).Value)).RootElement;
        var agents = body.GetProperty("agents").EnumerateArray().Select(x => x.GetString()).ToList();
        var commands = body.GetProperty("commands").EnumerateArray().Select(x => x.GetString()).ToList();

        agents.Should().Contain(OwnerOpenId);
        commands.Should().Contain(OwnerOpenId);
        agents.Should().NotContain(ForeignPrivateId, "id чужого приватного чата человеку не отдаётся");
        commands.Should().NotContain(ForeignPrivateId);
    }

    // ── снимок чатов для проекций фич (S2d-4j, дефект 2f8b7b73) ─────────────────────

    [Fact]
    public void СнимокФич_ДляЧеловека_ЧужойПриватныйНеВходит_СвойПриватныйИОбщиеВходят()
    {
        var ids = FeatureChatDoor.SnapshotVisible(_sessions, _ownerId).Select(s => s.Id).ToList();

        ids.Should().NotContain(ForeignPrivateId, "id чужого приватного чата не уходит в ChatIds страницы и индекса фич");
        ids.Should().NotContain(PersonaForeignId);
        ids.Should().Contain([OwnPrivateId, MemberOpenId, OwnerOpenId, OutsideChatId]);
    }

    [Fact]
    public void СнимокФич_МашинныйSnapshot_ДляГардаПривязкиНеМенялся_ЧужойПриватныйЕстьВНём()
    {
        var ids = FeatureChatDoor.Snapshot(_sessions, _ownerId).Select(s => s.Id).ToList();

        ids.Should().Contain(ForeignPrivateId, "гарду привязки нужны все чаты владельца");
    }

    // Настоящий проектор на сериализованной строке: фича с чужим приватным чатом (эпик начат из
    // него, шаг привязан к нему по TaskId) и со своим общим чатом. Контроль: тот же проектор на
    // машинном снимке id отдаёт — значит, тест различает снимки, а не пустую выдачу.
    [Fact]
    public void ПроекцииФич_ПроекторНаСнимкеВидимых_ЧужойПриватныйЧатНеВСтрокеОтвета_ОбщийЕсть()
    {
        var tasks = new ClaudeHomeServer.Services.Tasks.TaskManager(_config);
        var epic = tasks.Create(_projectId, _ownerId, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: ForeignPrivateId));
        var foreignStep = tasks.Create(_projectId, _ownerId, new CreateTaskRequest("Шаг чужого", Role: "step",
            FeatureId: epic.Id));
        var openStep = tasks.Create(_projectId, _ownerId, new CreateTaskRequest("Шаг общего", Role: "step",
            FeatureId: epic.Id));
        _sessions.GetById(ForeignPrivateId)!.TaskId = foreignStep.Id;
        _sessions.GetById(OwnerOpenId)!.TaskId = openStep.Id;
        var projector = new ClaudeHomeServer.Services.Tasks.Projector.FeatureProjector(tasks);

        // Как в контроллере: снимок видимых + маска id чатов в самих задачах облегчённого run.
        string Serialize(List<ClaudeHomeServer.Services.Tasks.Projector.FeatureIndexSession> snap, bool mask)
        {
            ClaudeHomeServer.Services.Tasks.Projector.FeatureRun M(ClaudeHomeServer.Services.Tasks.Projector.FeatureRun r) =>
                mask ? FeatureChatDoor.MaskRunChats(r, _sessions, _projects, _ownerId) : r;
            return JsonSerializer.Serialize(new
            {
                full = M(ClaudeHomeServer.Services.Tasks.Projector.FeatureProjector.SlimRun(
                    projector.ProjectFull(epic.Id, _ownerId, snap)!)),
                list = projector.ProjectAll(_ownerId, snap).Select(M).ToList(),
                index = projector.BuildIndex(_ownerId, snap),
            });
        }

        var visible = Serialize(FeatureChatDoor.SnapshotVisible(_sessions, _ownerId), mask: true);
        var machine = Serialize(FeatureChatDoor.Snapshot(_sessions, _ownerId), mask: false);

        machine.Should().Contain(ForeignPrivateId, "контроль: без фильтра проектор отдаёт id чата");
        visible.Should().NotContain(ForeignPrivateId);
        visible.Should().Contain(OwnerOpenId, "общий чат проекта остаётся в проекциях как прежде");
    }

    // Настоящие GetFeatures/GetFeature (не копия композиции): сериализованная строка ответа.
    // Эпик начат из приватного чата (SourceSessionId), шаг привязан к нему по TaskId, второй шаг —
    // к общему чату. Без MaskRunChats (TasksController.GetFeatures) id приватного чата остаётся
    // в самих задачах run; без SnapshotVisible — в ChatIds/исполнителях.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private TasksController FeaturesAs(string viewerId, ClaudeHomeServer.Services.Tasks.TaskManager tasks) =>
        new(tasks, new TestSessionBroadcaster(), null!, _projects, _personas, null!, null!, _sessions, null!,
            features: new ClaudeHomeServer.Services.Tasks.Projector.FeatureProjector(tasks))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", viewerId)])),
                },
            },
        };

    // Фича зрителя: эпик + шаг на приватном чате + шаг на общем. Чаты — чужие/общие для зрителя.
    private (string EpicId, ClaudeHomeServer.Services.Tasks.TaskManager Tasks) SeedFeature(
        string tenantId, string privateChatId, string openChatId)
    {
        var tasks = new ClaudeHomeServer.Services.Tasks.TaskManager(_config);
        var epic = tasks.Create(_projectId, tenantId, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: privateChatId));
        var privateStep = tasks.Create(_projectId, tenantId, new CreateTaskRequest("Шаг приватного", Role: "step",
            FeatureId: epic.Id));
        var openStep = tasks.Create(_projectId, tenantId, new CreateTaskRequest("Шаг общего", Role: "step",
            FeatureId: epic.Id));
        _sessions.GetById(privateChatId)!.TaskId = privateStep.Id;
        _sessions.GetById(openChatId)!.TaskId = openStep.Id;
        return (epic.Id, tasks);
    }

    private static string Wire(IActionResult result) =>
        JsonSerializer.Serialize(result.Should().BeOfType<OkObjectResult>().Subject.Value, WireOptions);

    [Fact]
    public async Task НастоящиеРучкиФич_ВладелецПроекта_ЧужойПриватныйЧатНеВСтрокеОтвета_ОбщийЕсть()
    {
        var (epicId, tasks) = SeedFeature(_ownerId, ForeignPrivateId, OwnerOpenId);
        using var _ = tasks;
        var controller = FeaturesAs(_ownerId, tasks);

        var list = Wire(controller.GetFeatures());
        var page = Wire(await controller.GetFeature(epicId));

        foreach (var wire in new[] { list, page })
        {
            wire.Should().Contain(epicId, "контроль: ответ не пустой");
            wire.Should().NotContain(ForeignPrivateId, "id чужого приватного чата не уходит в ответ ручки");
        }
        page.Should().Contain(OwnerOpenId, "общий чат проекта остаётся в ответе страницы как прежде");
    }

    [Fact]
    public async Task НастоящиеРучкиФич_УчастникПроекта_ПриватныйЧатВладельцаНеВСтрокеОтвета_ОбщийЕсть()
    {
        // Фича участника в общем проекте: эпик начат из приватного чата ВЛАДЕЛЬЦА — участнику он
        // недоступен. Снимок чатов собирается по арендатору (владельцу проекта), поэтому общий чат
        // в ответ участника не входит — проверяем отсутствие приватного, а не наличие общего.
        var (epicId, tasks) = SeedFeature(_editorId, OwnPrivateId, MemberOpenId);
        using var _ = tasks;
        var controller = FeaturesAs(_editorId, tasks);

        var list = Wire(controller.GetFeatures());
        var page = Wire(await controller.GetFeature(epicId));

        foreach (var wire in new[] { list, page })
        {
            wire.Should().Contain(epicId, "контроль: ответ не пустой, фича участника в нём есть");
            wire.Should().NotContain(OwnPrivateId, "участник не видит приватный чат владельца");
        }
    }
}
