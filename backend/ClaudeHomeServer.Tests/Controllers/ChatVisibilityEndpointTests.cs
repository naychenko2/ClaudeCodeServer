using System.Security.Claims;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Git;
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
using Moq;
using Xunit;

namespace ClaudeHomeServer.Tests.Controllers;

/// <summary>
/// S2d-7a: PUT /api/chats/{id}/visibility. Контроллер: видимость чата (404 неотличим для зрителя,
/// которому чат не виден), читатель — 403 reader, не автор — 403 not_chat_author, остальное
/// решает SessionManager.SetChatVisibility (409 not_shared_project / chat_has_messages).
/// QA-D45: порядок отказов — режим чата (409 private_chat_mode / private_chat_dossiers) идёт
/// раньше авторского 403: у штаба и группы нет автора, которого касалась бы эта операция, а
/// участник своего приватного чата не является арендатором проектного чата.
/// Автор-редактор проходит: это писатель для АВТОРА чата, а не арендатора (GetOwnedVisible
/// редактора не пустил бы). Стенд — как у GetOwnedVisibleTests: контроллер напрямую, стор
/// сессий сидируется sessions.json, в корне DI — настоящая точка доступа.
/// </summary>
public sealed class ChatVisibilityEndpointTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projects;
    private readonly UserStore _users;
    private readonly string _ownerId;
    private readonly string _editorId;
    private readonly string _readerId;
    private readonly string _outsideId;
    private readonly string _projectId;
    private readonly string _personalProjectId;
    private readonly ChatHistoryService _history;
    private readonly SessionManager _sessions;
    private readonly PersonaManager _personas;

    private const string EditorChatId = "editor-chat";
    private const string EditorChat2Id = "editor-chat-2";
    private const string OwnerChatId = "owner-chat";
    private const string OwnerPrivateId = "owner-private";
    private const string EditorPrivateId = "editor-private";
    private const string WithMessagesId = "with-messages";
    private const string PersonalChatId = "personal-chat";
    private const string OutsideChatId = "outside-chat";
    private const string TeamChatId = "team-chat";
    private const string GroupChatId = "group-chat";
    private const string ReaderPrivateId = "reader-private";

    public ChatVisibilityEndpointTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_visibility_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Read-ручки S2d-3e идут через IProjectAccess.ResolveSession: участнику нужен
                // рубильник инстанса и личный флаг — как в проде
                [SharedProjectsGate.SwitchKey] = "true",
            })
            .Build();

        var userStore = _users = new UserStore(_config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = userStore.GetFirst()!.Id;
        _editorId = userStore.Add("kuzma-editor", "pwd", "user").Id;
        _readerId = userStore.Add("some-reader", "pwd", "user").Id;
        // Посторонний: не участник общего проекта и без флага shared-projects — для него чат
        // проекта неотличим от несуществующего (QA-D45, c3)
        _outsideId = userStore.Add("outside-user", "pwd", "user").Id;
        foreach (var id in new[] { _editorId, _readerId })
            userStore.SetFeatureFlag(id, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        // Реальный файл корня: за гейтом Files локатор обязан его найти (200) — это и есть
        // доказательство пройденного гейта (образец — FilesControllerTests W4-LINK-B)
        File.WriteAllText(Path.Combine(projectRoot, "a.txt"), "один\n");
        _projects = new ProjectManager(_config, userStore, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, userStore.GetFirst()!.Username).Id;
        // Оба — участники общего проекта: на read-ручках автор своего приватного проходит,
        // другой участник на чужом приватном — нет
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
            new ProjectMember { UserId = _readerId, Role = ProjectRole.Reader, AddedBy = _ownerId },
        ]);

        // Личный проект владельца (без участников) и чат в нём — для 409 not_shared_project
        var personalRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "personal")).FullName;
        _personalProjectId = _projects.Create("Личный", personalRoot, _ownerId, userStore.GetFirst()!.Username).Id;

        WriteSessions(sessionsJsonPath, [
            new Session { Id = EditorChatId, ProjectId = _projectId, CreatedByUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = EditorChat2Id, ProjectId = _projectId, CreatedByUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = OwnerChatId, ProjectId = _projectId, CreatedByUserId = _ownerId, Status = SessionStatus.Finished },
            // Приватные сиды — с флагом opt-out true: его ставит тумблер приватности (S2d-7a),
            // инвариант PrivateToUserId != null ⇒ ExcludeFromDossiers == true
            new Session { Id = OwnerPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId, PrivateToUserId = _ownerId, ExcludeFromDossiers = true, Status = SessionStatus.Finished },
            new Session { Id = EditorPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId, PrivateToUserId = _editorId, ExcludeFromDossiers = true, Status = SessionStatus.Finished },
            new Session { Id = WithMessagesId, ProjectId = _projectId, CreatedByUserId = _editorId, MessageCount = 2, Status = SessionStatus.Finished },
            new Session { Id = PersonalChatId, ProjectId = _personalProjectId, CreatedByUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OutsideChatId, OwnerId = _ownerId, CreatedByUserId = _ownerId, Status = SessionStatus.Finished },
            // S2d-7b-3: штаб «Командной реализации» и групповой чат (общие, автор — владелец); приватный
            // чат читателя (автор понижен до Reader: приватность ставилась, пока он был редактором)
            new Session { Id = TeamChatId, ProjectId = _projectId, CreatedByUserId = _ownerId, TeamImplement = new SessionTeamImplement(), Status = SessionStatus.Finished },
            new Session { Id = GroupChatId, ProjectId = _projectId, CreatedByUserId = _ownerId, Participants = ["p1", "p2"], Status = SessionStatus.Finished },
            new Session { Id = ReaderPrivateId, ProjectId = _projectId, CreatedByUserId = _readerId, PrivateToUserId = _readerId, ExcludeFromDossiers = true, Status = SessionStatus.Finished },
        ]);

        _history = new ChatHistoryService(_config);
        _personas = new PersonaManager(_config);
        _sessions = CreateSessionManager(userStore, appSettings);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sessions.ToList()));

    // Состав — как в SessionStatusTests: живой SessionManager без процессов и LLM-вызовов
    private SessionManager CreateSessionManager(UserStore userStore, AppSettingsService appSettings)
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projects, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = _personas;
        var bindings = new PersonaBindingsService(personas, _projects, wkStore,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projects, _history, _config, adapters, falCost, usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas, bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster(),
            services: AccessProvider());
    }

    // Корень DI с настоящей точкой доступа (тот же ленивый шов, что в проде: прямая зависимость
    // замкнула бы цикл ProjectAccessService → ISessionDirectory → SessionManager). Лямбда
    // читает _sessions позже — к первому GetService он уже собран.
    private ServiceProvider AccessProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectAccess>(_ => new ProjectAccessService(
            _projects, _sessions, new SharedProjectsGate(_config, new FeatureFlagService(_users))));
        services.AddSingleton<IFeatureChatLinks, NoFeatureChatLinks>();
        return services.BuildServiceProvider();
    }

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

    // PUT /api/projects/{id}/sessions/{sid} (S2d-6c): Update не трогает provisioner/контекст/кэш —
    // несуществующие для ручки зависимости, как null! в ChatsAs выше.
    private SessionsController SessionsAs(string userId) =>
        new(_sessions, _projects, null!, _personas, null!, null!) { ControllerContext = AsUser(userId) };

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])) },
    };

    private static (int? Status, string? Code) Read(IActionResult r)
    {
        switch (r)
        {
            case NotFoundResult: return (404, null);
            case OkObjectResult: return (200, null);
            case ObjectResult o:
                var code = o.Value?.GetType().GetProperty("code")?.GetValue(o.Value) as string;
                return (o.StatusCode, code);
            default: return (null, null);
        }
    }

    [Fact]
    public void Редактор_Автор_ПриватныйИОбратно_200_ExcludeFromDossiersОстаётся()
    {
        var c = ChatsAs(_editorId);

        Read(c.SetVisibilitySync(EditorChatId, new SetVisibilityRequest(true))).Status.Should().Be(200);
        var s = _sessions.GetById(EditorChatId)!;
        s.PrivateToUserId.Should().Be(_editorId);
        s.ExcludeFromDossiers.Should().BeTrue();

        Read(c.SetVisibilitySync(EditorChatId, new SetVisibilityRequest(false))).Status.Should().Be(200);
        s.PrivateToUserId.Should().BeNull();
        s.ExcludeFromDossiers.Should().BeTrue("после «сделать общим» прошлое не раскрываем");

        Read(c.SetVisibilitySync(EditorChatId, new SetVisibilityRequest(true))).Should().Be((409, "chat_has_messages"));
    }

    [Fact]
    public void Читатель_403reader_ПриватностьНеСтавится()
    {
        var c = ChatsAs(_readerId);

        Read(c.SetVisibilitySync(EditorChat2Id, new SetVisibilityRequest(true))).Should().Be((403, "reader"));
        _sessions.GetById(EditorChat2Id)!.PrivateToUserId.Should().BeNull();
    }

    [Fact]
    public void НеАвтор_403notChatAuthor_ДажеВладелецПроекта()
    {
        Read(ChatsAs(_ownerId).SetVisibilitySync(EditorChat2Id, new SetVisibilityRequest(true)))
            .Should().Be((403, "not_chat_author"));
        Read(ChatsAs(_editorId).SetVisibilitySync(OwnerChatId, new SetVisibilityRequest(true)))
            .Should().Be((403, "not_chat_author"));
    }

    [Fact]
    public void ЧужойПриватный_404_КакНесуществующий_ДажеАвторуНеЕгоЧата()
    {
        var c = ChatsAs(_editorId);

        Read(c.SetVisibilitySync(OwnerPrivateId, new SetVisibilityRequest(false))).Status.Should().Be(404);
        Read(c.SetVisibilitySync("no-such-chat", new SetVisibilityRequest(false))).Status.Should().Be(404);
        _sessions.GetById(OwnerPrivateId)!.PrivateToUserId.Should().Be(_ownerId, "чужую приватность не снять");
    }

    [Fact]
    public void ВладелецПроектаНаПриватномЧатеРедактора_404_ПриватностьНеМеняется()
    {
        var c = ChatsAs(_ownerId);

        Read(c.SetVisibilitySync(EditorPrivateId, new SetVisibilityRequest(false))).Status.Should().Be(404);
        Read(c.SetVisibilitySync(EditorPrivateId, new SetVisibilityRequest(true))).Status.Should().Be(404);
        _sessions.GetById(EditorPrivateId)!.PrivateToUserId.Should().Be(_editorId);
    }

    [Fact]
    public void ЛичныйПроектИЧатВнеПроекта_409notSharedProject()
    {
        var c = ChatsAs(_ownerId);

        Read(c.SetVisibilitySync(PersonalChatId, new SetVisibilityRequest(true))).Should().Be((409, "not_shared_project"));
        Read(c.SetVisibilitySync(OutsideChatId, new SetVisibilityRequest(true))).Should().Be((409, "not_shared_project"));
    }

    [Fact]
    public void ЧатСоСообщениями_409chatHasMessages()
    {
        Read(ChatsAs(_editorId).SetVisibilitySync(WithMessagesId, new SetVisibilityRequest(true)))
            .Should().Be((409, "chat_has_messages"));
        _sessions.GetById(WithMessagesId)!.PrivateToUserId.Should().BeNull();
    }

    // S2d-7b-3: приватный чат не становится групповым — 409 private_chat_mode, состав не меняется.
    // Общий чат идёт прежним путём: до валидации состава (400 «от 2 до 8 персон»), не 409.
    [Fact]
    public void ПриватныйЧат_Участники_409privateChatMode_ОбщийЧат_ПрежнийПуть()
    {
        var c = ChatsAs(_ownerId);

        Read(c.SetParticipants(OwnerPrivateId, new SetParticipantsRequest(["p1", "p2"]))).Should().Be((409, "private_chat_mode"));
        _sessions.GetById(OwnerPrivateId)!.Participants.Should().BeNull("отказ не создаёт группу");

        Read(c.SetParticipants(OwnerChatId, new SetParticipantsRequest([]))).Status.Should().Be(400);
    }

    // S2d-7b-3: приватный чат не становится штабом — 409 private_chat_mode; выключение режима
    // дверь не режет. Общий чат дверь пропускает.
    [Fact]
    public async Task ПриватныйЧат_ВключитьШтаб_409privateChatMode_ОбщийЧат_ДверьПропускает()
    {
        var c = ChatsAs(_ownerId);

        Read(await c.SetTeamImplement(OwnerPrivateId, new SetTeamImplementRequest(true)))
            .Should().Be((409, "private_chat_mode"));
        _sessions.GetById(OwnerPrivateId)!.TeamImplement.Should().BeNull();

        ClaudeHomeServer.Controllers.FeatureChatDoor.DenyPrivateMode(_sessions.GetById(OwnerPrivateId)).Should().NotBeNull();
        ClaudeHomeServer.Controllers.FeatureChatDoor.DenyPrivateMode(_sessions.GetById(OwnerChatId)).Should().BeNull();
    }

    // S2d-7b-3, обратное направление: штаб или группа приватными не делаются (409 private_chat_mode),
    // флаг не ставится; снятие приватности и «чат без режима» — прежним путём.
    [Fact]
    public void ШтабИГруппа_СделатьПриватным_409privateChatMode_ФлагНеСтавится()
    {
        var c = ChatsAs(_ownerId);

        Read(c.SetVisibilitySync(TeamChatId, new SetVisibilityRequest(true))).Should().Be((409, "private_chat_mode"));
        Read(c.SetVisibilitySync(GroupChatId, new SetVisibilityRequest(true))).Should().Be((409, "private_chat_mode"));
        _sessions.GetById(TeamChatId)!.PrivateToUserId.Should().BeNull();
        _sessions.GetById(GroupChatId)!.PrivateToUserId.Should().BeNull();
        _sessions.GetById(GroupChatId)!.ExcludeFromDossiers.Should().BeFalse("отказ не трогает opt-out");

        Read(c.SetVisibilitySync(OwnerChatId, new SetVisibilityRequest(true))).Status.Should().Be(200);
    }

    // S2d-7b-3: автор понижен до читателя — его приватный чат виден только ему и только на чтение:
    // чтение доступно (GetAccessible), право вести ход закрыто 403 project_scope, настройки — 403
    // owner_settings, смена видимости — 403 reader; другому участнику и владельцу проекта чат не виден (404).
    [Fact]
    public void АвторСталЧитателем_ПриватныйЧат_ТолькоЕмуИТолькоНаЧтение()
    {
        _sessions.GetAccessible(ReaderPrivateId, _readerId).Should().NotBeNull("читателю-автору чтение остаётся");
        _sessions.GetAccessible(ReaderPrivateId, _editorId).Should().BeNull();
        _sessions.GetAccessible(ReaderPrivateId, _ownerId).Should().BeNull("владелец проекта чужой приватный не видит");

        Read(_sessions.DenyTurn(ReaderPrivateId, _readerId)!).Should().Be((403, "project_scope"));
        Read(_sessions.DenyOwnerTurn(ReaderPrivateId, _readerId)!).Status.Should().Be(403);
        Read(ChatsAs(_readerId).SetVisibilitySync(ReaderPrivateId, new SetVisibilityRequest(false))).Should().Be((403, "reader"));
        _sessions.GetById(ReaderPrivateId)!.PrivateToUserId.Should().Be(_readerId);

        Read(_sessions.DenyTurn(ReaderPrivateId, _editorId)!).Status.Should().Be(404);
        Read(_sessions.DenyTurn(ReaderPrivateId, _ownerId)!).Status.Should().Be(404);
    }

    // S2d-7b-3, доработка 1: позитив — общий чат принимает состав и становится штабом через ручки;
    // те же ручки на приватном чате — 409 private_chat_mode и состав/штаб не записываются.
    [Fact]
    public async Task ОбщийЧат_ПринимаетСоставИСтановитсяШтабом_ПриватныйНет()
    {
        Persona P(string n) => _personas.Create(_ownerId, n, role: null, description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Project, projectId: _projectId,
            color: null, greeting: null, memoryEnabled: false);
        var p1 = P("Участник-1");
        var p2 = P("Участник-2");
        var c = ChatsAs(_ownerId);

        Read(c.SetParticipants(OwnerChatId, new SetParticipantsRequest([p1.Id, p2.Id]))).Status.Should().Be(200);
        _sessions.GetById(OwnerChatId)!.Participants.Should().BeEquivalentTo([p1.Id, p2.Id]);

        Read(await c.SetTeamImplement(OwnerChatId, new SetTeamImplementRequest(true, CoordinatorPersonaId: p1.Id, ExecutorPersonaIds: [p2.Id])))
            .Status.Should().Be(200);
        _sessions.GetById(OwnerChatId)!.TeamImplement.Should().NotBeNull("общий чат становится штабом");

        // Приватный чат владельца — оба режима 409, ничего не записано
        Read(c.SetParticipants(OwnerPrivateId, new SetParticipantsRequest([p1.Id, p2.Id]))).Should().Be((409, "private_chat_mode"));
        Read(await c.SetTeamImplement(OwnerPrivateId, new SetTeamImplementRequest(true, CoordinatorPersonaId: p1.Id)))
            .Should().Be((409, "private_chat_mode"));
        _sessions.GetById(OwnerPrivateId)!.Participants.Should().BeNullOrEmpty();
        _sessions.GetById(OwnerPrivateId)!.TeamImplement.Should().BeNull();
    }

    // Автор-читатель: GET /api/chats/{id} по настоящей ручке — 200 ему, 404 остальным.
    [Fact]
    public void АвторЧитатель_GetChats_200_ДругимУчастникамИВладельцу404()
    {
        Read(ChatsAs(_readerId).Get(ReaderPrivateId)).Status.Should().Be(200);
        Read(ChatsAs(_editorId).Get(ReaderPrivateId)).Status.Should().Be(404);
        Read(ChatsAs(_ownerId).Get(ReaderPrivateId)).Status.Should().Be(404);
    }

    // S2d-6c: PUT sessions/{sid} с excludeFromDossiers=false на приватном чате — 409
    // private_chat_dossiers, и ни одно поле запроса не применяется (имя остаётся).
    // После снятия приватности автором флаг остаётся true (прошлое не раскрываем), но теперь
    // автор может выключить его сам: тот же PUT проходит и флаг гаснет.
    [Fact]
    public void ПриватныйЧат_ВыключитьOptOut_409_ИмяНеМеняется_ПослеСнятияПриватности_200()
    {
        var c = SessionsAs(_ownerId);
        var s = _sessions.GetById(OwnerPrivateId)!;
        s.ExcludeFromDossiers.Should().BeTrue("приватный чат по тумблеру уже вне «Истории решений»");

        Read(c.UpdateSync(_projectId, OwnerPrivateId, new UpdateSessionRequest(Name: "новое имя", ExcludeFromDossiers: false)))
            .Should().Be((409, "private_chat_dossiers"));
        s.Name.Should().BeNull("отказ 409: прочие поля запроса не применяются");
        s.ExcludeFromDossiers.Should().BeTrue("отказ не меняет флаг");
        s.PrivateToUserId.Should().Be(_ownerId, "отказ не трогает приватность");

        // Снять приватность может только автор; флаг после этого остаётся true (S2d-7a)
        Read(ChatsAs(_ownerId).SetVisibilitySync(OwnerPrivateId, new SetVisibilityRequest(false))).Status.Should().Be(200);
        s.PrivateToUserId.Should().BeNull();
        s.ExcludeFromDossiers.Should().BeTrue("после «сделать общим» прошлое приватного периода не раскрываем");

        // Явное согласие автора после снятия приватности — принимается
        Read(c.UpdateSync(_projectId, OwnerPrivateId, new UpdateSessionRequest(ExcludeFromDossiers: false))).Status.Should().Be(200);
        s.ExcludeFromDossiers.Should().BeFalse();
    }

    // Тот же PUT на чужом приватном чате — прежний 404 (существование не выдаётся).
    // На общем чате opt-out включается и выключается без ограничений — но у проектного чата
    // настройки принадлежат владельцу-арендатору (ResolveOwnerId, SessionManager.cs:4454).
    // QA-P1: правка чужого общего чата — право записи (редактор и выше), а не авторство:
    // проверяется в ChatWriteRightsEndpointTests.
    [Fact]
    public void ЧужойПриватный_404_АОбщийЧатВладельца_МеняетOptOut_Свободно()
    {
        Read(SessionsAs(_editorId).UpdateSync(_projectId, OwnerPrivateId, new UpdateSessionRequest(ExcludeFromDossiers: false)))
            .Status.Should().Be(404, "чужой приватный неотличим от несуществующего");
        _sessions.GetById(OwnerPrivateId)!.ExcludeFromDossiers.Should().BeTrue("чужой флаг не трогается");

        var c = SessionsAs(_ownerId);
        var s = _sessions.GetById(OwnerChatId)!;
        Read(c.UpdateSync(_projectId, OwnerChatId, new UpdateSessionRequest(ExcludeFromDossiers: true))).Status.Should().Be(200);
        s.ExcludeFromDossiers.Should().BeTrue();
        Read(c.UpdateSync(_projectId, OwnerChatId, new UpdateSessionRequest(ExcludeFromDossiers: false))).Status.Should().Be(200);
        s.ExcludeFromDossiers.Should().BeFalse("общий чат: owner сам решает");
        s.PrivateToUserId.Should().BeNull("opt-out не трогает приватность");
    }

    // QA-D45 (D4): режим общего контура отвечает раньше авторского отказа — групповой чат и штаб
    // приватными не делает НИКТО, не только автор. Участник (не автор этих чатов) получает тот же
    // 409 private_chat_mode, что и владелец; флаг и opt-out не трогаются.
    [Fact]
    public void ГрупповойЧатИШтабНеАвтор_409privateChatMode_ФлагНеСтавится()
    {
        var c = ChatsAs(_editorId);

        Read(c.SetVisibilitySync(GroupChatId, new SetVisibilityRequest(true))).Should().Be((409, "private_chat_mode"));
        Read(c.SetVisibilitySync(TeamChatId, new SetVisibilityRequest(true))).Should().Be((409, "private_chat_mode"));

        var group = _sessions.GetById(GroupChatId)!;
        var team = _sessions.GetById(TeamChatId)!;
        group.PrivateToUserId.Should().BeNull("отказ не делает чат приватным");
        team.PrivateToUserId.Should().BeNull();
        group.ExcludeFromDossiers.Should().BeFalse("отказ не трогает opt-out");
        group.Participants.Should().BeEquivalentTo(["p1", "p2"], "отказ не меняет состав");
    }

    // QA-D45 (D5): «Не сохранять решения» на СВОЁМ приватном чате участника: для него чат виден
    // (CanSee), но он не арендатор (владелец проектного чата — владелец проекта) — был 403
    // not_chat_author, теперь 409 private_chat_dossiers. Отказ ничего не применяет: флаг,
    // приватность и имя целы.
    [Fact]
    public void СвойПриватныйЧатУчастника_ВыключитьOptOut_409privateChatDossiers()
    {
        var s = _sessions.GetById(EditorPrivateId)!;
        s.ExcludeFromDossiers.Should().BeTrue("приватный чат по тумблеру уже вне «Истории решений»");

        Read(SessionsAs(_editorId).UpdateSync(_projectId, EditorPrivateId,
            new UpdateSessionRequest(Name: "новое имя", ExcludeFromDossiers: false)))
            .Should().Be((409, "private_chat_dossiers"));

        s.ExcludeFromDossiers.Should().BeTrue("отказ не меняет флаг");
        s.PrivateToUserId.Should().Be(_editorId, "отказ не трогает приватность");
        s.Name.Should().BeNull("отказ 409: прочие поля запроса не применяются");
    }

    // QA-D45 (c3): новый порядок отказов ничего не выдаёт лишнего. Постороннему (не участнику
    // проекта) приватный чат — прежний 404, неотличимый от несуществующего; читателю — прежний
    // 403 (reader на тумблере; на правке — project_scope: QA-P1, право записи вместо авторства),
    // 409 режима его не касается.
    [Fact]
    public void ПостороннийИЧитатель_ПрежниеОтказы_СуществованиеНеРаскрывается()
    {
        var req = new UpdateSessionRequest(ExcludeFromDossiers: false);

        Read(SessionsAs(_outsideId).UpdateSync(_projectId, EditorPrivateId, req)).Should().Be((404, null));
        Read(SessionsAs(_outsideId).UpdateSync(_projectId, "no-such-session", req)).Should().Be((404, null));
        Read(ChatsAs(_outsideId).SetVisibilitySync(EditorPrivateId, new SetVisibilityRequest(false))).Status.Should().Be(404);
        _sessions.GetById(EditorPrivateId)!.PrivateToUserId.Should().Be(_editorId, "чужая попытка ничего не изменила");

        Read(ChatsAs(_readerId).SetVisibilitySync(GroupChatId, new SetVisibilityRequest(true))).Should().Be((403, "reader"));
        Read(SessionsAs(_readerId).UpdateSync(_projectId, OwnerChatId, req)).Should().Be((403, "project_scope"));
        _sessions.GetById(OwnerChatId)!.ExcludeFromDossiers.Should().BeFalse("отказ не пишет флаг чужому чату");
    }
}

// SetVisibility стал асинхронным (вышибание из session-группы после Ok); тесты ручки читают результат синхронно
internal static class ChatsControllerVisibilityExtensions
{
    public static Microsoft.AspNetCore.Mvc.IActionResult SetVisibilitySync(
        this ClaudeHomeServer.Controllers.ChatsController c, string id, ClaudeHomeServer.Controllers.SetVisibilityRequest req) =>
        c.SetVisibility(id, req).GetAwaiter().GetResult();
}

// Update — async (UpdateAsync в менеджере); та же синхронная читка, что у SetVisibilitySync
internal static class SessionsControllerUpdateExtensions
{
    public static Microsoft.AspNetCore.Mvc.IActionResult UpdateSync(
        this ClaudeHomeServer.Controllers.SessionsController c, string projectId, string id, ClaudeHomeServer.Controllers.UpdateSessionRequest req) =>
        c.Update(projectId, id, req).GetAwaiter().GetResult();
}
