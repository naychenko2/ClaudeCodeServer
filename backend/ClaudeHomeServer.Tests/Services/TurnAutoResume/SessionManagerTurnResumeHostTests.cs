using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services.TurnAutoResume;

/// <summary>
/// Регресс (ADR-033, причина 2) на САМ боевой адаптер: у проектной сессии OwnerId пустой, владелец
/// берётся из проекта через SessionManager.ResolveOwnerId. Тесты оркестратора идут через FakeHost и
/// владельца не резолвят — мутация «session.OwnerId вместо ResolveOwnerId(session)» их не краснила.
/// </summary>
public class SessionManagerTurnResumeHostTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _config;
    private readonly UserStore _userStore;
    private readonly ProjectManager _projectManager;
    private readonly FeatureFlagService _flags;
    private readonly SessionManagerTurnResumeHost _host;

    public SessionManagerTurnResumeHostTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "resume_host_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            })
            .Build();

        _userStore = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projectManager = new ProjectManager(_config, _userStore, new AppSettingsService(_config));
        _flags = new FeatureFlagService(_userStore);
        _host = new SessionManagerTurnResumeHost(CreateSessionManager(), _projectManager, _flags,
            new Mock<IProjectDeviceGate>().Object);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    private SessionManager CreateSessionManager()
    {
        var llmProviders = new LlmProviderRegistry(_config);
        var subPool = new ClaudeSubscriptionPool(_config);
        var adapters = new LlmSessionAdapterFactory(
            _config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(_config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, _config);
        var usage = new UsageService(_config);
        var appSettings = new AppSettingsService(_config);
        var jwt = new JwtService(_config, _userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(_config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(_projectManager, _config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, _config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(_config);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), _userStore, _config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new SandboxManager(_config, NullLogger<SandboxManager>.Instance);
        return new SessionManager(_projectManager, new ChatHistoryService(_config), _config, adapters, falCost,
            usage, appSettings, _userStore, jwt, server.Object, llmProviders, _flags, personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox, new TestSessionBroadcaster());
    }

    // Проект с владельцем-пользователем; флаг авто-возобновления у владельца — по аргументу
    private Project MakeOwnedProject(bool flagOn)
    {
        var owner = _userStore.Add("owner-" + Guid.NewGuid().ToString("N")[..8], "pass-123456", "user");
        owner.FeatureFlags = new Dictionary<string, bool> { [FeatureFlagKeys.TurnAutoResume] = flagOn };
        var root = Path.Combine(_tempDir, "proj-" + Guid.NewGuid().ToString("N")[..8]);
        return _projectManager.Create("p", root, owner.Id, owner.Username, createDirectory: true);
    }

    private static Session ProjectChat(string projectId) =>
        new() { Id = Guid.NewGuid().ToString(), ProjectId = projectId, OwnerId = null };

    [Fact]
    public void ПроектныйЧат_ПустойOwnerId_ФлагВладельцаПроектаВключён_True()
    {
        var project = MakeOwnedProject(flagOn: true);

        _host.IsEnabledFor(ProjectChat(project.Id)).Should().BeTrue(
            "владелец берётся из проекта (ResolveOwnerId), а не из пустого Session.OwnerId");
    }

    [Fact]
    public void ПроектныйЧат_ПустойOwnerId_ФлагВладельцаВыключен_False()
    {
        var project = MakeOwnedProject(flagOn: false);

        _host.IsEnabledFor(ProjectChat(project.Id)).Should().BeFalse("флаг у владельца проекта выключен");
    }

    [Fact]
    public void ПроектныйЧат_ПроектНеизвестен_False()
    {
        _host.IsEnabledFor(ProjectChat("нет-такого-проекта")).Should().BeFalse(
            "владелец не резолвится (null) — флаг считается выключенным");
    }
}
