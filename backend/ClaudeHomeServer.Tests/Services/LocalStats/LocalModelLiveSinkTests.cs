using System.Text.Json;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.LocalStats;

// Боевая рассылка живых событий локальной модели (флаг local-model-stats, шаг 19):
// OnLive/OnTurn шлют ServerMessage в session-группу, только если есть зритель (HasViewers).
// События эфемерные — в history.json не пишутся.
public class LocalModelLiveSinkTests : IDisposable
{
    private const string SessionId = "sess-live";
    private const string ConnectionId = "conn-1";

    private readonly string _tempDir;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly TestSessionBroadcaster _broadcaster = new();
    private readonly Mock<IClientProxy> _group = new();
    private readonly Mock<IHubClients> _clients = new();
    private readonly Mock<IHubContext<SessionHub>> _hub = new();
    private readonly List<ServerMessage> _sent = [];

    public LocalModelLiveSinkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lm_sink_tests_" + Guid.NewGuid().ToString("N"));
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

        // hub.Clients.Group(sessionId) → IClientProxy; SendAsync("message", msg) —
        // extension, который вызывает SendCoreAsync("message", [msg]) → ловим в _sent.
        _clients.Setup(c => c.Group(SessionId)).Returns(_group.Object);
        _group.Setup(g => g.SendCoreAsync("message", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) => _sent.Add((ServerMessage)args[0]!))
            .Returns(Task.CompletedTask);
        _hub.Setup(h => h.Clients).Returns(_clients.Object);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private (LocalModelLiveSink Sink, SessionManager Sessions) Arrange(bool withViewer)
    {
        var session = new Session
        {
            Id = SessionId,
            ProjectId = "proj-1",
            Status = SessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        File.WriteAllText(_sessionsJsonPath, JsonSerializer.Serialize(new List<Session> { session }));

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var userStore = new UserStore(_config, new Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        var jwt = new JwtService(_config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<IServer>();
        server.Setup(s => s.Features).Returns(new FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(_projectManager, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var sessions = new SessionManager(_projectManager, _historyService, _config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, _broadcaster);

        if (withViewer)
            sessions.AddViewer(SessionId, ConnectionId);

        return (new LocalModelLiveSink(_hub.Object, sessions), sessions);
    }

    // (1) Зритель есть → OnLive шлёт local_model_live в session-группу (фаза, tps, waiting, sessionId).
    [Fact]
    public void OnLive_СоЗрителем_ШлётВГруппу()
    {
        var (sink, _) = Arrange(withViewer: true);

        sink.OnLive(SessionId, "generating", 75.0, 2);

        var msg = _sent.Should().ContainSingle().Subject;
        var live = msg.Should().BeOfType<LocalModelLiveMessage>().Subject;
        live.Phase.Should().Be("generating");
        live.GenTps.Should().Be(75.0);
        live.WaitingTotal.Should().Be(2);
        live.SessionId.Should().Be(SessionId, "рассылка проставляет sessionId");
    }

    // (2) Зрителя нет → тик не шлём (событие одноразовое, догнать нельзя).
    [Fact]
    public void OnLive_БезЗрителя_НеШлёт()
    {
        var (sink, _) = Arrange(withViewer: false);

        sink.OnLive(SessionId, "queued", null, 0);

        _sent.Should().BeEmpty("без зрителя тик не шлём");
    }

    // (3) OnTurn → local_model_turn_stats: значения из итога, prefillTps — null (движок
    // пока не отчитывается). Мутация: перепутать ttftMs/queueMsApprox — тест краснеет.
    [Fact]
    public void OnTurn_ШлётИтогХода()
    {
        var (sink, _) = Arrange(withViewer: true);
        var summary = new LocalModelTurnSummary
        {
            OutTokens = 256,
            GenTps = 33.3,
            TtftMs = 450,
            QueueMsApprox = 2000,
            CacheHitPerc = 0.75,
        };

        sink.OnTurn(SessionId, summary);

        var msg = _sent.Should().ContainSingle().Subject;
        var turn = msg.Should().BeOfType<LocalModelTurnStatsMessage>().Subject;
        turn.GenTps.Should().Be(33.3);
        turn.PrefillTps.Should().BeNull("движок пока не отчитывается о prefill");
        turn.TtftMs.Should().Be(450);
        turn.QueueMsApprox.Should().Be(2000);
        turn.CacheHitPerc.Should().Be(0.75);
        turn.OutTokens.Should().Be(256);
        turn.SessionId.Should().Be(SessionId);
    }
}
