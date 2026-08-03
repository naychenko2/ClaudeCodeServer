using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Уход из чата (LeaveSession): соединение остаётся в группе сессии независимо от статуса —
/// иначе события идущего хода или внеходовые сообщения (отчёт делегированной задачи,
/// эскалация, team_plan) улетают мимо вкладки без возможности повторной доставки.
/// Зритель при этом снимается всегда, чтобы сервер снова мог слать push/тост.
/// </summary>
public class SessionHubLeaveTests : IDisposable
{
    private const string ConnectionId = "conn-1";

    private readonly string _tempDir;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly Mock<IHubContext<SessionHub>> _hub;
    private readonly Mock<IGroupManager> _groups = new();

    public SessionHubLeaveTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hub_leave_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        _projectManager = new ProjectManager(_config, userStore, appSettings);
        _historyService = new ChatHistoryService(_config);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        _hub = new Mock<IHubContext<SessionHub>>();
        _hub.Setup(h => h.Clients).Returns(clients.Object);

        _groups.Setup(g => g.RemoveFromGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private SessionManager CreateSessionManager()
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new SkillsService(), new WorkspaceKnowledgeStore(_config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var userStore = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projectManager, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, _config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projectManager, _hub.Object, _historyService, _config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas,
            personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox);
    }

    // Сессия нужного статуса в живом реестре. Через файл выставить «идущий ход» нельзя:
    // LoadSessions намеренно переводит Starting/Working/Waiting в Orphaned (процесс не пережил
    // рестарт), поэтому статус доводим уже на загруженной сессии.
    private (SessionHub Hub, SessionManager Sessions, string SessionId) Arrange(SessionStatus status)
    {
        var session = new Session
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = "proj-1",
            Status = SessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        File.WriteAllText(_sessionsJsonPath, JsonSerializer.Serialize(new List<Session> { session }));

        var sessions = CreateSessionManager();
        sessions.GetById(session.Id)!.Status = status;
        sessions.AddViewer(session.Id, ConnectionId);

        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.ConnectionId).Returns(ConnectionId);

        // FileWatcherService, ConnectionDiagnostics и DevServerService нужны только другим
        // методам хаба — LeaveSession их не трогает, поэтому не тянем сюда их зависимости
        var hub = new SessionHub(sessions, _projectManager, null!, null!, null!)
        {
            Context = context.Object,
            Groups = _groups.Object,
        };
        return (hub, sessions, session.Id);
    }

    [Theory]
    [InlineData(SessionStatus.Starting)]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Waiting)]
    [InlineData(SessionStatus.Active)]
    [InlineData(SessionStatus.Finished)]
    [InlineData(SessionStatus.Error)]
    [InlineData(SessionStatus.Orphaned)]
    public async Task LeaveSession_ЛюбойСтатус_ОстаётсяВГруппе(SessionStatus status)
    {
        var (hub, sessions, sessionId) = Arrange(status);

        await hub.LeaveSession(sessionId);

        _groups.Verify(g => g.RemoveFromGroupAsync(ConnectionId, sessionId, It.IsAny<CancellationToken>()),
            Times.Never, "внеходовые и ходовые события должны и дальше доходить до вкладки");
        sessions.HasViewers(sessionId).Should().BeFalse("зритель ушёл — конец хода придёт push/тостом");
    }

    [Fact]
    public async Task LeaveSession_НеизвестнаяСессия_ОстаётсяВГруппе()
    {
        var (hub, _, _) = Arrange(SessionStatus.Finished);
        var unknown = Guid.NewGuid().ToString();

        await hub.LeaveSession(unknown);

        _groups.Verify(g => g.RemoveFromGroupAsync(ConnectionId, unknown, It.IsAny<CancellationToken>()),
            Times.Never, "из группы теперь не выходим никогда");
    }
}
