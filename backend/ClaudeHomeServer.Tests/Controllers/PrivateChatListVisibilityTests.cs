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
        new(_sessions, _projects, null!, null!, null!, null!) { ControllerContext = AsUser(userId) };

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, _users, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

    private PersonasController PersonasAs(string userId) =>
        new(_personas, _projects, _sessions, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, _config, NullLogger<PersonasController>.Instance, null!)
        { ControllerContext = AsUser(userId) };

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
        var ids = IdsOf(PersonasAs(_ownerId).Chats(_personaId));

        ids.Should().Contain(PersonaOwnId, "свой чат персоны — прежнее поведение");
        ids.Should().NotContain(PersonaForeignId,
            "в списке чатов персоны чужой приватный — тот же мёртвый id, что в остальных списках");
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
}
