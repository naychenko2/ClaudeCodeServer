using System.Security.Claims;
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
/// FIN3-2 (доработка по FINAL-3, п. 2 и 3 ревью b13f2105): инвариант «приватный чат виден
/// только автору, даже владельцу проекта» нарушался на путях входа, которых не было в
/// таблице разреза S2d:
///
/// 1) PUT /api/projects/{id}/sessions/{sid}/subject (ChatSubjectsController.MoveSession) —
///    стояли только [ProjectScope(Write)] и GetById: редактор по id чужого приватного чата
///    получал в ответ целый Session (Name, LastMessage, Attachments, ClaudeSessionId) и писал
///    в него тему. Закрыто: IsVisibleTo (Core-точка SessionAccess.CanSee) до записи; чужой
///    приватный — 404, неотличимый от несуществующего (тот же приём, что у правки чата S2d-3f).
/// 2) POST /api/projects/{id}/sessions с resumeSessionId (SessionsController.Create →
///    SessionManager.CreateAsync): ClaudeSessionId = resumeSessionId без проверки, а ключ
///    истории — HistoryKeyOf = ClaudeSessionId (SessionManager.cs:557): новый чат с csid
///    чужого приватного получал его history.json и --resume транскрипта CLI. Закрыто:
///    IsTranscriptReservedForOthers — csid существующего невидимого чата отвергается 404
///    (тот же код, что на неизвестный id в теле). Неизвестный ключ принимается как прежде:
///    ветвление подаёт свой новый csid до регистрации чата (SessionManager.cs:3660), внешний
///    транскрипт — законный resume (сторож: ChatCreationPersonaGateTests.СессияСResume_Проходит).
/// 3) POST /api/chats (ChatsController.Create → CreateChatAsync) и POST /api/personas/{id}/chats
///    (PersonasController.CreateChat → CreatePersonaChatAsync, включая проектную ветку
///    contextProjectId, SessionManager.cs:4378) — те же двери без гейта: новый чат с csid чужого
///    приватного получил бы его транскрипт. Закрыто тем же гейтом и тем же 404 (доработка по
///    возврату ревью cea595f7 шага FIN3-2).
///
/// Стенд — как у ChatVisibilityEndpointTests/GetOwnedVisibleTests: контроллеры напрямую с
/// ClaimsPrincipal вызывающего, сессии сидируются sessions.json, в корне DI — настоящая
/// точка доступа. Автор своего приватного и общий чат проекта проходят как прежде.
/// HTTP-слой этих ручек (роутинг, [ProjectScope], [Authorize]) — хост-набор, он на гейте Марка.
/// </summary>
public sealed class PrivateChatSubjectResumeTests : IDisposable
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
    private readonly string _subjectId;
    private readonly string _editorPersonaId;
    private readonly string _ownerProjectPersonaId;
    private readonly string _ownerGlobalPersonaId;

    // Сид: приватный владельцу, приватный редактору, общий — у каждого свой ClaudeSessionId:
    // именно он ключ истории и транскрипта (HistoryKeyOf), т.е. то, что подставляет resume.
    private const string OwnerPrivateId = "owner-private";
    private const string OwnerPrivateCsid = "csid-owner-private";
    private const string EditorPrivateId = "editor-private";
    private const string EditorPrivateCsid = "csid-editor-private";
    private const string OwnerOpenId = "owner-open";
    private const string OwnerOpenCsid = "csid-owner-open";

    public PrivateChatSubjectResumeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "priv_subject_resume_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
                // Членство в проекте оформляется только при включённом рубильнике — как в проде
                [SharedProjectsGate.SwitchKey] = "true",
                // Create поднимает сессию: профиль CLI и домашние папки — в temp (образец —
                // SessionManagerTestBase)
                ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
                ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            })
            .Build();

        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _ownerId = _users.GetFirst()!.Id;
        _editorId = _users.Add("kuzma-editor", "pwd", "user").Id;
        _users.SetFeatureFlag(_editorId, FeatureFlagKeys.SharedProjects, true);

        var appSettings = new AppSettingsService(_config);
        var projectRoot = Directory.CreateDirectory(Path.Combine(_tempDir, "project")).FullName;
        _projects = new ProjectManager(_config, _users, appSettings);
        _projectId = _projects.Create("Общий проект", projectRoot, _ownerId, _users.GetFirst()!.Username).Id;
        _projects.SetMembers(_projectId,
        [
            new ProjectMember { UserId = _editorId, Role = ProjectRole.Editor, AddedBy = _ownerId },
        ]);

        // Реестр тем: одна тема человека — цель ручника переноса
        _subjectId = _projects.ReplaceSubjects(_projectId, [new ProjectSubjectEdit(null, "Тема А")])[0].Id;

        WriteSessions(Path.Combine(_tempDir, "sessions.json"), [
            new Session { Id = OwnerPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                PrivateToUserId = _ownerId, ExcludeFromDossiers = true,
                ClaudeSessionId = OwnerPrivateCsid, Status = SessionStatus.Finished },
            new Session { Id = EditorPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId,
                PrivateToUserId = _editorId, ExcludeFromDossiers = true,
                ClaudeSessionId = EditorPrivateCsid, Status = SessionStatus.Finished },
            new Session { Id = OwnerOpenId, ProjectId = _projectId, CreatedByUserId = _ownerId,
                ClaudeSessionId = OwnerOpenCsid, Status = SessionStatus.Finished },
        ]);

        _personas = new PersonaManager(_config);
        // Персоны для четвёртой группы (POST /personas/{id}/chats): глобальная персона редактора —
        // ветка чата вне проекта; глобальная и проектная персоны владельца — обе дороги в проектную
        // ветку CreatePersonaChatAsync (через contextProjectId и через persona.ProjectId, :4378)
        _editorPersonaId = _personas.Create(_editorId, "Персона редактора", null, null, null, null, null,
            PersonaScope.Global, null, null, null, false).Id;
        _ownerProjectPersonaId = _personas.Create(_ownerId, "Персона проекта", null, null, null, null, null,
            PersonaScope.Project, _projectId, null, null, false).Id;
        _ownerGlobalPersonaId = _personas.Create(_ownerId, "Глобальная владельца", null, null, null, null, null,
            PersonaScope.Global, null, null, null, false).Id;
        _sessions = CreateSessionManager(appSettings);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private static void WriteSessions(string path, IEnumerable<Session> sessions) =>
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(sessions.ToList()));

    // Живой SessionManager без процессов и LLM-вызовов (состав — ChatVisibilityEndpointTests),
    // с настоящей точкой доступа в корне DI — на ней VisibleTo резолвит проект.
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
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
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
            TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster(),
            services: AccessProvider());
    }

    private ServiceProvider AccessProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IProjectAccess>(_ => new ProjectAccessService(
            _projects, _sessions, new SharedProjectsGate(_config, new FeatureFlagService(_users))));
        services.AddSingleton<IFeatureChatLinks, NoFeatureChatLinks>();
        return services.BuildServiceProvider();
    }

    private static ControllerContext AsUser(string userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)])),
        },
    };

    // MoveSession не трогает sweeper — несуществующая для ручки зависимость, как null! в образце
    private ChatSubjectsController SubjectsAs(string userId) =>
        new(_sessions, _projects, null!) { ControllerContext = AsUser(userId) };

    private SessionsController SessionsAs(string userId) =>
        new(_sessions, _projects, null!, _personas, null!, null!) { ControllerContext = AsUser(userId) };

    // Create с непустым resume не доходит до провижнера (блока «без персоны и без resume») —
    // несуществующие для ручки зависимости как null!, образец — PrivateChatListVisibilityTests
    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, _users, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

    // CreateChat не трогает оркестрацию персон (memory/ask/automation/...) — те же null!
    private PersonasController PersonasAs(string userId) =>
        new(_personas, _projects, _sessions, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, _config, NullLogger<PersonasController>.Instance, null!)
        { ControllerContext = AsUser(userId) };

    // ── 1. subject: чужой приватный — 404, тема не пишется ────────────────────────────

    [Fact]
    public void Subject_ЧужойПриватный_404_КакНесуществующий_ТемаНеПишется()
    {
        var req = new MoveSessionSubjectRequest(_subjectId, null);

        // Редактор на приватном чате владельца: ни ответа (целый Session), ни записи темы
        SubjectsAs(_editorId).MoveSession(_projectId, OwnerPrivateId, req)
            .Should().BeOfType<NotFoundResult>("чужой приватный неотличим от несуществующего");
        _sessions.GetById(OwnerPrivateId)!.SubjectId.Should().BeNull("отказ не пишет тему чужому чату");

        // Даже владелец на приватном чате редактора — тот же 404 (приватный виден только автору)
        SubjectsAs(_ownerId).MoveSession(_projectId, EditorPrivateId, req)
            .Should().BeOfType<NotFoundResult>();
        _sessions.GetById(EditorPrivateId)!.SubjectId.Should().BeNull();

        // Тот же отказ на «Вернуть автоматике» (Release) — гейт стоит до развилки вида записи
        SubjectsAs(_editorId).MoveSession(_projectId, OwnerPrivateId, new MoveSessionSubjectRequest(null, true))
            .Should().BeOfType<NotFoundResult>();

        // Недотличимость от несуществующего: тот же ответ на id, которого нет вовсе
        SubjectsAs(_editorId).MoveSession(_projectId, "no-such-session", req)
            .Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public void Subject_СвойПриватныйИОбщий_200_ТемаПишется()
    {
        var req = new MoveSessionSubjectRequest(_subjectId, null);

        // Автор своего приватного проходит (приватность — его поле, не чужой запрет)
        SubjectsAs(_editorId).MoveSession(_projectId, EditorPrivateId, req)
            .Should().BeOfType<OkObjectResult>();
        _sessions.GetById(EditorPrivateId)!.SubjectId.Should().Be(_subjectId);

        // Общий чат проекта редактором — прежний путь (право Write остаётся)
        SubjectsAs(_editorId).MoveSession(_projectId, OwnerOpenId, req)
            .Should().BeOfType<OkObjectResult>();
        _sessions.GetById(OwnerOpenId)!.SubjectId.Should().Be(_subjectId);
    }

    // ── 2. resume: чужой приватный — 404, чат не создан ───────────────────────────────

    [Fact]
    public async Task Resume_ЧужойПриватныйCsid_404_ЧатНеСоздан()
    {
        var before = _sessions.GetVisibleByProject(_projectId, _editorId).Count;

        // Редактор создаёт чат с csid приватного чата владельца: ключ истории и транскрипта
        // (HistoryKeyOf) отдал бы ему чужой разговор — отказ, неотличимый от неизвестного id
        var r = await SessionsAs(_editorId).Create(_projectId,
            new CreateSessionRequest(ResumeSessionId: OwnerPrivateCsid));
        r.Should().BeOfType<NotFoundObjectResult>("csid чужого приватного — тот же отказ, что неизвестный id");
        _sessions.GetVisibleByProject(_projectId, _editorId).Count.Should().Be(before,
            "отказ не создаёт чат");

        // Даже владелец на csid приватного чата редактора — тот же 404
        (await SessionsAs(_ownerId).Create(_projectId,
            new CreateSessionRequest(ResumeSessionId: EditorPrivateCsid)))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task Resume_СвойПриватныйОбщийИНеизвестный_201_ВладелецНеЗатронут()
    {
        // Автор на своём приватном csid — прежний путь: новый чат продолжает свой разговор
        var own = (CreatedAtActionResult)await SessionsAs(_editorId).Create(_projectId,
            new CreateSessionRequest(ResumeSessionId: EditorPrivateCsid));
        ((Session)own.Value!).ClaudeSessionId.Should().Be(EditorPrivateCsid);

        // Общий csid проекта — и редактору, и владельцу (для владельца — свой, поведение не меняется)
        var openByEditor = (CreatedAtActionResult)await SessionsAs(_editorId).Create(_projectId,
            new CreateSessionRequest(ResumeSessionId: OwnerOpenCsid));
        ((Session)openByEditor.Value!).ClaudeSessionId.Should().Be(OwnerOpenCsid);
        var openByOwner = (CreatedAtActionResult)await SessionsAs(_ownerId).Create(_projectId,
            new CreateSessionRequest(ResumeSessionId: OwnerOpenCsid));
        ((Session)openByOwner.Value!).ClaudeSessionId.Should().Be(OwnerOpenCsid);

        // Неизвестный ключ принимается как прежде: ветвление подаёт свой новый csid до
        // регистрации чата, внешний транскрипт — законный resume (сторож СессияСResume_Проходит)
        var unknown = (CreatedAtActionResult)await SessionsAs(_editorId).Create(_projectId,
            new CreateSessionRequest(ResumeSessionId: "resume-unknown-123"));
        ((Session)unknown.Value!).ClaudeSessionId.Should().Be("resume-unknown-123");
    }

    // ── 3. POST /api/chats (ChatsController.Create → CreateChatAsync): тот же гейт ───────

    [Fact]
    public async Task ResumeChats_ЧужойПриватныйCsid_404_ЧатНеСоздан()
    {
        var before = _sessions.GetProjectlessChats(_editorId).Count;

        // Редактор создаёт чат вне проекта с csid приватного чата владельца: ClaudeSessionId
        // (SessionManager.cs:4311) — ключ истории (HistoryKeyOf) и транскрипта CLI, новый чат
        // получил бы чужой разговор — отказ, неотличимый от неизвестного id
        var r = await ChatsAs(_editorId).Create(new CreateChatRequest(ResumeSessionId: OwnerPrivateCsid));
        r.Should().BeOfType<NotFoundObjectResult>("csid чужого приватного — тот же отказ, что неизвестный id");
        _sessions.GetProjectlessChats(_editorId).Count.Should().Be(before, "отказ не создаёт чат");

        // Даже владелец на csid приватного чата редактора — тот же 404 (приватный виден только автору)
        (await ChatsAs(_ownerId).Create(new CreateChatRequest(ResumeSessionId: EditorPrivateCsid)))
            .Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task ResumeChats_СвойПриватныйОбщийИНеизвестный_201_ВладелецНеЗатронут()
    {
        // Автор на своём приватном csid — прежний путь: чат вне проекта продолжает свой разговор
        var own = (CreatedAtActionResult)await ChatsAs(_editorId).Create(
            new CreateChatRequest(ResumeSessionId: EditorPrivateCsid));
        ((Session)own.Value!).ClaudeSessionId.Should().Be(EditorPrivateCsid);

        // Общий csid проекта — и редактору, и владельцу (для владельца — свой, поведение не меняется)
        var openByEditor = (CreatedAtActionResult)await ChatsAs(_editorId).Create(
            new CreateChatRequest(ResumeSessionId: OwnerOpenCsid));
        ((Session)openByEditor.Value!).ClaudeSessionId.Should().Be(OwnerOpenCsid);
        var openByOwner = (CreatedAtActionResult)await ChatsAs(_ownerId).Create(
            new CreateChatRequest(ResumeSessionId: OwnerOpenCsid));
        ((Session)openByOwner.Value!).ClaudeSessionId.Should().Be(OwnerOpenCsid);

        // Неизвестный ключ принимается как прежде: ветвление и внешний транскрипт — законный resume
        var unknown = (CreatedAtActionResult)await ChatsAs(_editorId).Create(
            new CreateChatRequest(ResumeSessionId: "resume-unknown-456"));
        ((Session)unknown.Value!).ClaudeSessionId.Should().Be("resume-unknown-456");
    }

    // ── 4. POST /api/personas/{id}/chats (PersonasController.CreateChat → CreatePersonaChatAsync) ──
    // CreateChat отдаёт ActionResult<Session>: Ok()/NotFound() оборачиваются в него, результат
    // действия — на .Result (так же его разворачивает рантайм).

    [Fact]
    public async Task ResumePersonaChats_ЧужойПриватныйCsid_404_ОбеВеткиОтказ()
    {
        var beforeChats = _sessions.GetProjectlessChats(_editorId).Count;
        var beforeProject = _sessions.GetVisibleByProject(_projectId, _ownerId).Count;

        // Глобальная персона редактора, чат вне проекта: csid приватного чата владельца — отказ,
        // неотличимый от неизвестного id
        var r = await PersonasAs(_editorId).CreateChat(_editorPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: OwnerPrivateCsid));
        r.Result.Should().BeOfType<NotFoundObjectResult>("csid чужого приватного — тот же отказ, что неизвестный id");
        _sessions.GetProjectlessChats(_editorId).Count.Should().Be(beforeChats, "отказ не создаёт чат");

        // Проектная ветка (persona.ProjectId → SessionManager.cs:4378): тот же 404
        (await PersonasAs(_ownerId).CreateChat(_ownerProjectPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: EditorPrivateCsid)))
            .Result.Should().BeOfType<NotFoundObjectResult>();

        // Та же ветка через contextProjectId (req.ProjectId): тот же 404, чат проекта не создан
        (await PersonasAs(_ownerId).CreateChat(_ownerGlobalPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: EditorPrivateCsid, ProjectId: _projectId)))
            .Result.Should().BeOfType<NotFoundObjectResult>();
        _sessions.GetVisibleByProject(_projectId, _ownerId).Count.Should().Be(beforeProject,
            "отказ не создаёт чат проекта");
    }

    [Fact]
    public async Task ResumePersonaChats_СвойПриватныйОбщийИНеизвестный_200_ОбеВеткиПроходят()
    {
        // Глобальная персона редактора, чат вне проекта: свой, общий и неизвестный csid — прежний путь
        var own = (OkObjectResult)(await PersonasAs(_editorId).CreateChat(_editorPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: EditorPrivateCsid))).Result!;
        ((Session)own.Value!).ClaudeSessionId.Should().Be(EditorPrivateCsid);
        var open = (OkObjectResult)(await PersonasAs(_editorId).CreateChat(_editorPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: OwnerOpenCsid))).Result!;
        ((Session)open.Value!).ClaudeSessionId.Should().Be(OwnerOpenCsid);
        var unknown = (OkObjectResult)(await PersonasAs(_editorId).CreateChat(_editorPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: "resume-unknown-789"))).Result!;
        ((Session)unknown.Value!).ClaudeSessionId.Should().Be("resume-unknown-789");

        // Проектная ветка на своём csid — гейт её не блокирует (приватный виден автору)
        var proj = (OkObjectResult)(await PersonasAs(_ownerId).CreateChat(_ownerProjectPersonaId,
            new CreatePersonaChatRequest(ResumeSessionId: OwnerPrivateCsid))).Result!;
        ((Session)proj.Value!).ClaudeSessionId.Should().Be(OwnerPrivateCsid);
        ((Session)proj.Value!).ProjectId.Should().Be(_projectId);
    }
}
