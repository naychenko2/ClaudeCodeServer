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
            new Session { Id = OwnerPrivateId, ProjectId = _projectId, CreatedByUserId = _ownerId, PrivateToUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = EditorPrivateId, ProjectId = _projectId, CreatedByUserId = _editorId, PrivateToUserId = _editorId, Status = SessionStatus.Finished },
            new Session { Id = WithMessagesId, ProjectId = _projectId, CreatedByUserId = _editorId, MessageCount = 2, Status = SessionStatus.Finished },
            new Session { Id = PersonalChatId, ProjectId = _personalProjectId, CreatedByUserId = _ownerId, Status = SessionStatus.Finished },
            new Session { Id = OutsideChatId, OwnerId = _ownerId, CreatedByUserId = _ownerId, Status = SessionStatus.Finished },
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
        return services.BuildServiceProvider();
    }

    private ChatsController ChatsAs(string userId) =>
        new(_sessions, _projects, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ChatsController>.Instance) { ControllerContext = AsUser(userId) };

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

        Read(c.SetVisibility(EditorChatId, new SetVisibilityRequest(true))).Status.Should().Be(200);
        var s = _sessions.GetById(EditorChatId)!;
        s.PrivateToUserId.Should().Be(_editorId);
        s.ExcludeFromDossiers.Should().BeTrue();

        Read(c.SetVisibility(EditorChatId, new SetVisibilityRequest(false))).Status.Should().Be(200);
        s.PrivateToUserId.Should().BeNull();
        s.ExcludeFromDossiers.Should().BeTrue("после «сделать общим» прошлое не раскрываем");

        Read(c.SetVisibility(EditorChatId, new SetVisibilityRequest(true))).Should().Be((409, "chat_has_messages"));
    }

    [Fact]
    public void Читатель_403reader_ПриватностьНеСтавится()
    {
        var c = ChatsAs(_readerId);

        Read(c.SetVisibility(EditorChat2Id, new SetVisibilityRequest(true))).Should().Be((403, "reader"));
        _sessions.GetById(EditorChat2Id)!.PrivateToUserId.Should().BeNull();
    }

    [Fact]
    public void НеАвтор_403notChatAuthor_ДажеВладелецПроекта()
    {
        Read(ChatsAs(_ownerId).SetVisibility(EditorChat2Id, new SetVisibilityRequest(true)))
            .Should().Be((403, "not_chat_author"));
        Read(ChatsAs(_editorId).SetVisibility(OwnerChatId, new SetVisibilityRequest(true)))
            .Should().Be((403, "not_chat_author"));
    }

    [Fact]
    public void ЧужойПриватный_404_КакНесуществующий_ДажеАвторуНеЕгоЧата()
    {
        var c = ChatsAs(_editorId);

        Read(c.SetVisibility(OwnerPrivateId, new SetVisibilityRequest(false))).Status.Should().Be(404);
        Read(c.SetVisibility("no-such-chat", new SetVisibilityRequest(false))).Status.Should().Be(404);
        _sessions.GetById(OwnerPrivateId)!.PrivateToUserId.Should().Be(_ownerId, "чужую приватность не снять");
    }

    [Fact]
    public void ВладелецПроектаНаПриватномЧатеРедактора_404_ПриватностьНеМеняется()
    {
        var c = ChatsAs(_ownerId);

        Read(c.SetVisibility(EditorPrivateId, new SetVisibilityRequest(false))).Status.Should().Be(404);
        Read(c.SetVisibility(EditorPrivateId, new SetVisibilityRequest(true))).Status.Should().Be(404);
        _sessions.GetById(EditorPrivateId)!.PrivateToUserId.Should().Be(_editorId);
    }

    [Fact]
    public void ЛичныйПроектИЧатВнеПроекта_409notSharedProject()
    {
        var c = ChatsAs(_ownerId);

        Read(c.SetVisibility(PersonalChatId, new SetVisibilityRequest(true))).Should().Be((409, "not_shared_project"));
        Read(c.SetVisibility(OutsideChatId, new SetVisibilityRequest(true))).Should().Be((409, "not_shared_project"));
    }

    [Fact]
    public void ЧатСоСообщениями_409chatHasMessages()
    {
        Read(ChatsAs(_editorId).SetVisibility(WithMessagesId, new SetVisibilityRequest(true)))
            .Should().Be((409, "chat_has_messages"));
        _sessions.GetById(WithMessagesId)!.PrivateToUserId.Should().BeNull();
    }
}
