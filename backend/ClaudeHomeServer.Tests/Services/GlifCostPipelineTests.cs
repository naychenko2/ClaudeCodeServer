using ClaudeHomeServer.Core.Services;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Pipeline glif_cost: tool_result → GlifCostParser → PublishGlifCostAsync →
// история + SignalR broadcast + SpendRecord.
public class GlifCostPipelineTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionManager _sessions;
    private readonly UserStore _userStore;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _history;
    private readonly TestSessionBroadcaster _broadcaster = new();
    private readonly SpendStore _spend;

    public GlifCostPipelineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "glif_cost_pipeline_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Glif:McpToken"] = "glif-test-token",
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, _userStore, appSettings);
        var personas = new PersonaManager(config);
        var tasks = new TaskManager(config, personas: personas);
        _history = new ChatHistoryService(config);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var glif = new GlifAccountService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(_userStore);
        var notesSvc = new NotesService(_projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _spend = new SpendStore(Path.Combine(_dir, "spend"), detailDays: 30);

        _sessions = new SessionManager(_projectManager, _history, config, adapters, falCost, usage,
            appSettings, _userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            _broadcaster, spend: _spend, glif: glif);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PublishGlifCostAsync_ПишетИсторию_SignalR_And_Spend()
    {
        var user = _userStore.Add("glif-pipe-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_pipe")).FullName;
        var project = _projectManager.Create("Pipe", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cs-glif-pipe-1");

        var msg = new GlifCostMessage("job-pipe-1", "image", 2, 5.5, "image_tool_x");
        await _sessions.PublishGlifCostAsync(session.Id, msg);

        // История
        var history = await _history.LoadAsync(session.ClaudeSessionId!);
        history.Should().ContainSingle(m => m is StoredGlifCostMessage);
        var stored = (StoredGlifCostMessage)history.Single(m => m is StoredGlifCostMessage);
        stored.JobId.Should().Be("job-pipe-1");
        stored.OutputType.Should().Be("image");
        stored.MediaCount.Should().Be(2);
        stored.Credits.Should().BeApproximately(5.5, 0.001);
        stored.Model.Should().Be("image_tool_x");

        // SignalR
        var broadcasts = _broadcaster.Session.Select(t => t.Message).ToList();
        broadcasts.Should().ContainSingle(m => m is GlifCostMessage);
        var broadcast = (GlifCostMessage)broadcasts.Single(m => m is GlifCostMessage);
        broadcast.JobId.Should().Be("job-pipe-1");

        // Spend
        var all = _spend.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Should().ContainSingle(r => r.Source == SpendSources.Glif);
        var spend = all.Single(r => r.Source == SpendSources.Glif);
        spend.Provider.Should().Be("glif");
        spend.Generations.Should().Be(1);
        spend.Model.Should().Be("image_tool_x");
        spend.Label.Should().Be("image");
        spend.CostUsd.Should().BeNull();
    }

    [Fact]
    public async Task PublishGlifCostAsync_ДедупПоJobId_НеДублирует()
    {
        var user = _userStore.Add("glif-dup-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_dup")).FullName;
        var project = _projectManager.Create("Dup", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cs-glif-dup-1");

        var msg = new GlifCostMessage("job-dup-1", "image", 1, 1.0, null);
        await _sessions.PublishGlifCostAsync(session.Id, msg);
        await _sessions.PublishGlifCostAsync(session.Id, msg);

        var history = await _history.LoadAsync(session.ClaudeSessionId!);
        history.OfType<StoredGlifCostMessage>().Should().HaveCount(1);
        _broadcaster.Session.Select(t => t.Message).OfType<GlifCostMessage>().Should().HaveCount(1);
        _spend.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Count(r => r.Source == SpendSources.Glif).Should().Be(1);
    }
}
