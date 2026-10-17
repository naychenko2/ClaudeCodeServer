using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// «Продолжить в новом чате»: CreateContinuationAsync публикует SessionContinued на шине (для
// подсистем), а владельцу уходит живое ChatContinuedMessage по SignalR-каналу — чтобы человек,
// смотрящий исходный чат, автоматически перешёл в новый. Адресация та же, что у прочих
// уведомлений сессии: session-группа исходного чата + project_X (проектный) или user_X (вне
// проекта). Тест проверяет адресацию и состав сообщения (newSessionId + projectId).
public class ChatContinuedEventTests : IDisposable
{
    // Владелец чатов: свой пользователь на каждую сборку (CreateChatAsync резолвит
    // домашнюю папку по UserStore — пользователя с несуществующим id он не найдёт)
    private string _ownerId = null!;
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private readonly TestSessionBroadcaster _broadcaster = new();
    private ChatHistoryService? _historyForBuild;
    private UserStore? _userStoreForBuild;

    public ChatContinuedEventTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_continued_event_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Helpers.TestFs.DeleteDirectoryResilient(_tempDir);

    // Снимок адресации: три канала broadcaster-а к единому виду (адресат × сообщение)
    private IEnumerable<(string Group, ServerMessage Msg)> Broadcasts =>
        _broadcaster.Session.Select(t => (t.SessionId, t.Message))
            .Concat(_broadcaster.Project.Select(t => ("project_" + t.ProjectId, t.Message)))
            .Concat(_broadcaster.Owner.Select(t => ("user_" + t.OwnerId, t.Message)));

    [Fact]
    public async Task Продолжение_ПроектногоЧата_ШлётChatContinuedВSessionИProjectГруппы()
    {
        var (sut, projects) = BuildSut();
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект продолжения", dir, _ownerId, TestUsername);
        var source = await sut.CreateAsync(project.Id, ClaudeMode.Auto);

        var created = await sut.CreateContinuationAsync(source.Id, _ownerId);

        var msgs = Broadcasts.Where(t => t.Msg is ChatContinuedMessage).ToList();
        msgs.Should().HaveCount(2, "session-группа + project-группа: по копии каждому адресату");
        msgs.Should().AllSatisfy(t =>
        {
            var m = (ChatContinuedMessage)t.Msg;
            m.NewSessionId.Should().Be(created.Id, "ссылка на новый чат");
            m.ProjectId.Should().Be(project.Id, "проектный чат несёт projectId");
        });
        // Базовый SessionId — исходный чат (рассылка проставляет его)
        _broadcaster.Session.Should().Contain(t => t.Message is ChatContinuedMessage && t.SessionId == source.Id);
    }

    [Fact]
    public async Task Продолжение_ЧатаВнеПроекта_ШлётChatContinuedВSessionИUserГруппы()
    {
        var (sut, _) = BuildSut();
        var source = await sut.CreateChatAsync(_ownerId, ClaudeMode.Auto);

        var created = await sut.CreateContinuationAsync(source.Id, _ownerId);

        var msgs = Broadcasts.Where(t => t.Msg is ChatContinuedMessage).ToList();
        msgs.Should().HaveCount(2, "session-группа + user-группа");
        msgs.Should().AllSatisfy(t =>
        {
            var m = (ChatContinuedMessage)t.Msg;
            m.NewSessionId.Should().Be(created.Id);
            m.ProjectId.Should().BeNull("чат вне проекта — projectId null");
        });
        _broadcaster.Session.Should().Contain(t => t.Message is ChatContinuedMessage && t.SessionId == source.Id);
    }

    // Сборка SessionManager своя (как ChatArchivedEventTests), но broadcaster ЗАПИСЫВАЕТ группы.
    private (SessionManager Sut, ProjectManager Projects) BuildSut()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userStoreForBuild = userStore;
        var owner = userStore.Add("continued-owner-" + Guid.NewGuid().ToString("N")[..8], "pw-123456", "user");
        _ownerId = owner.Id;
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _historyForBuild = new ChatHistoryService(config);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personas = new PersonaManager(config);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);

        return (new SessionManager(projectManager, _historyForBuild, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, broadcaster: _broadcaster), projectManager);
    }
}
