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
/// Тесты корректности маппинга статусов сессий при рестарте сервера (LoadSessions).
/// </summary>
public class SessionStatusTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectsJsonPath;
    private readonly string _sessionsJsonPath;
    private readonly IConfiguration _config;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _historyService;
    private readonly Mock<IHubContext<SessionHub>> _hub;

    public SessionStatusTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sess_status_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _projectsJsonPath = Path.Combine(_tempDir, "projects.json");
        _sessionsJsonPath = Path.Combine(_tempDir, "sessions.json");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = _projectsJsonPath,
            })
            .Build();

        var userStore = new UserStore(_config, NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        _projectManager = new ProjectManager(_config, userStore, appSettings);
        _historyService = new ChatHistoryService(_config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);

        _hub = new Mock<IHubContext<SessionHub>>();
        _hub.Setup(h => h.Clients).Returns(clients.Object);
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
        var userStore = new UserStore(_config, NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(_config);
        var jwt = new JwtService(_config, NullLogger<JwtService>.Instance);
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
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, _config, NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, _config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(_config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        return new SessionManager(_projectManager, _hub.Object, _historyService, _config, adapters, falCost, usage, appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas, personaMemory, bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox);
    }

    private void WriteSessions(IEnumerable<Session> sessions)
    {
        var json = JsonSerializer.Serialize(sessions.ToList());
        File.WriteAllText(_sessionsJsonPath, json);
    }

    private Session MakeSession(SessionStatus status, string projectId = "proj-1") =>
        new Session
        {
            Id = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    // --- LoadSessions: маппинг статусов ---

    [Theory]
    [InlineData(SessionStatus.Working)]
    [InlineData(SessionStatus.Starting)]
    [InlineData(SessionStatus.Waiting)]
    public void LoadSessions_ActiveProcessStatuses_BecomeOrphaned(SessionStatus status)
    {
        var session = MakeSession(status);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        var loaded = sut.GetById(session.Id);
        loaded.Should().NotBeNull();
        loaded!.Status.Should().Be(SessionStatus.Orphaned,
            because: $"статус {status} означает, что процесс работал — после рестарта он прерван");
    }

    [Fact]
    public void LoadSessions_ActiveStatus_BecomesFinished()
    {
        var session = MakeSession(SessionStatus.Active);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        var loaded = sut.GetById(session.Id);
        loaded!.Status.Should().Be(SessionStatus.Finished,
            because: "Active означает сессия была открыта, но Клод не работал — корректно завершена");
    }

    [Fact]
    public void LoadSessions_FinishedStatus_StaysFinished()
    {
        var session = MakeSession(SessionStatus.Finished);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Finished);
    }

    [Fact]
    public void LoadSessions_ErrorStatus_StaysError()
    {
        var session = MakeSession(SessionStatus.Error);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Error);
    }

    [Fact]
    public void LoadSessions_OrphanedStatus_StaysOrphaned()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        WriteSessions([session]);

        var sut = CreateSessionManager();

        sut.GetById(session.Id)!.Status.Should().Be(SessionStatus.Orphaned,
            because: "уже помеченная прерванной сессия не меняет статус при следующем рестарте");
    }

    // --- Все статусы сразу ---

    [Fact]
    public void LoadSessions_MixedStatuses_MappedCorrectly()
    {
        var working = MakeSession(SessionStatus.Working);
        var starting = MakeSession(SessionStatus.Starting);
        var waiting = MakeSession(SessionStatus.Waiting);
        var active = MakeSession(SessionStatus.Active);
        var finished = MakeSession(SessionStatus.Finished);
        var error = MakeSession(SessionStatus.Error);
        var orphaned = MakeSession(SessionStatus.Orphaned);

        WriteSessions([working, starting, waiting, active, finished, error, orphaned]);

        var sut = CreateSessionManager();

        sut.GetById(working.Id)!.Status.Should().Be(SessionStatus.Orphaned);
        sut.GetById(starting.Id)!.Status.Should().Be(SessionStatus.Orphaned);
        sut.GetById(waiting.Id)!.Status.Should().Be(SessionStatus.Orphaned);
        sut.GetById(active.Id)!.Status.Should().Be(SessionStatus.Finished);
        sut.GetById(finished.Id)!.Status.Should().Be(SessionStatus.Finished);
        sut.GetById(error.Id)!.Status.Should().Be(SessionStatus.Error);
        sut.GetById(orphaned.Id)!.Status.Should().Be(SessionStatus.Orphaned);
    }

    // --- Целостность enum (сериализация через System.Text.Json) ---

    [Fact]
    public void SessionStatus_EnumValues_StableAfterSerializationRoundtrip()
    {
        // Проверяем, что добавление Orphaned в конец не сдвинуло числовые значения
        // существующих статусов. Если сдвинулось — прочитанные из файла данные
        // будут неверно трактоваться.
        ((int)SessionStatus.Starting).Should().Be(0);
        ((int)SessionStatus.Working).Should().Be(1);
        ((int)SessionStatus.Active).Should().Be(2);
        ((int)SessionStatus.Waiting).Should().Be(3);
        ((int)SessionStatus.Finished).Should().Be(4);
        ((int)SessionStatus.Error).Should().Be(5);
        ((int)SessionStatus.Orphaned).Should().Be(6);
    }

    [Fact]
    public void SessionStatus_SerializedAsInteger_DeserializesCorrectly()
    {
        // Симулируем формат, в котором sessions.json сохраняет статусы
        // System.Text.Json сериализует enum как int по умолчанию
        var session = MakeSession(SessionStatus.Finished);

        var json = JsonSerializer.Serialize(new List<Session> { session });

        // Значение 4 (Finished) должно читаться как Finished, а не Orphaned
        json.Should().Contain("\"Status\":4",
            because: "Finished имеет целочисленное значение 4");

        var restored = JsonSerializer.Deserialize<List<Session>>(json)!;
        restored[0].Status.Should().Be(SessionStatus.Finished);
    }

    [Fact]
    public void SessionStatus_Orphaned_SerializesAsInt6()
    {
        var session = MakeSession(SessionStatus.Orphaned);
        var json = JsonSerializer.Serialize(new List<Session> { session });

        // Важно: Orphaned = 6, не 4 (Finished = 4 до добавления Orphaned в конец)
        json.Should().Contain("\"Status\":6",
            because: "Orphaned добавлен в конец enum и имеет значение 6");
    }

    // --- LoadSessions: нет файла ---

    [Fact]
    public void LoadSessions_NoFile_StartsEmpty()
    {
        // sessions.json не существует — менеджер стартует без сессий
        File.Exists(_sessionsJsonPath).Should().BeFalse();

        var sut = CreateSessionManager();

        sut.GetById("any-id").Should().BeNull();
    }

    // --- LoadSessions: повреждённый файл ---

    [Fact]
    public void LoadSessions_CorruptFile_StartsEmpty()
    {
        File.WriteAllText(_sessionsJsonPath, "not valid json {{{");

        var sut = CreateSessionManager();

        // Не должен бросать исключение, просто пустой список
        sut.GetById("any-id").Should().BeNull();
    }
}
