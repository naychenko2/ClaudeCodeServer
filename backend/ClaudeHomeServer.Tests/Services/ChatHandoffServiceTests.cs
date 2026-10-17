using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Перенос в новый чат, холодный путь (docs/features/chat-handoff.md): место chat-handoff,
// разделы промпта, черновик со ссылкой на старый чат, отказы (локаль, пустой чат, группа),
// 409 на двойной клик, неизменность старого чата. Сборка SessionManager — как в
// ChatDigestServiceTests (свой минимальный граф).
public class ChatHandoffServiceTests : IDisposable
{
    private const string TestUserId = "test-user-id";
    private const string TestUsername = "test-user";

    private readonly string _tempDir;
    private ChatHistoryService _history = null!;

    public ChatHandoffServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "chat_handoff_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFs.DeleteDirectoryResilient(_tempDir);

    [Fact]
    public void МестоChatHandoff_LargeБезЛокали_СвойПотолокВывода()
    {
        var place = LocalActionCatalog.Find(LocalActionCatalog.ChatHandoff);

        place.Should().NotBeNull();
        place!.Profile.Should().Be(CheapProfile.Large);
        place.DefaultLocal.Should().BeFalse();
        place.CloudNumPredict.Should().Be(4096);
        place.Group.Should().Be("Сессии");
        place.Agentic.Should().BeFalse();
    }

    [Fact]
    public void Промпт_СемьРазделовПоПорядку_иЗапретВыдумывать()
    {
        var prompt = ChatHandoffService.BuildPrompt("Чат", "Пользователь:\nпривет");

        string[] sections =
        [
            "### Цель", "### Решения и их причины", "### От чего отказались", "### Изменённые файлы",
            "### Где остановились", "### Следующий шаг", "### Открытые вопросы",
        ];
        var last = -1;
        foreach (var s in sections)
        {
            var at = prompt.IndexOf(s, StringComparison.Ordinal);
            at.Should().BeGreaterThan(last, $"раздел «{s}» обязан идти после предыдущего");
            last = at;
        }
        prompt.Should().Contain("Не выдумывай того, чего нет в транскрипте");
        prompt.Should().EndWith("привет" + Environment.NewLine);
    }

    [Fact]
    public void ТёплыйПромпт_ТеЖеРазделыЧтоУХолодного_ТолькоТекстом()
    {
        var cold = ChatHandoffService.BuildPrompt("Чат", "Пользователь:\nпривет");
        var warm = ChatHandoffService.BuildWarmPrompt();

        var spec = cold[cold.IndexOf("Разделы —", StringComparison.Ordinal)..cold.IndexOf("Правила:", StringComparison.Ordinal)];
        warm.Should().Contain(spec, "записки двух путей не должны разойтись по составу");
        warm.Should().Contain("Не вызывай инструменты").And.Contain("Не выдумывай того, чего нет в нашем разговоре");
        warm.Should().NotContain("Транскрипт:");
    }

    [Fact]
    public async Task Handoff_НовыйЧат_ЧерновикСЗапискойИСсылкойНаСтарый()
    {
        var runner = new StubRunner("### Цель\nнастроить значки");
        var (sut, mgr, chat) = BuildSut(runner);
        chat.Model = "claude-sonnet-5-5";

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        runner.Calls.Should().Be(1);
        runner.LastActionKey.Should().Be(LocalActionCatalog.ChatHandoff);
        runner.LastOwnerId.Should().Be(TestUserId);
        runner.LastPrompt.Should().Contain("Настройте значки проектов");

        result.Session.Id.Should().NotBe(chat.Id);
        result.Session.ProjectId.Should().Be(chat.ProjectId);
        result.Session.Model.Should().Be("claude-sonnet-5-5", "поля §11 наследуются");
        result.Session.ClaudeSessionId.Should().NotBe(chat.ClaudeSessionId, "новый разговор, без resume");
        result.Session.BranchedFromSessionId.Should().BeNull("перенос — не ветка");
        mgr.GetById(result.Session.Id).Should().NotBeNull();

        result.Draft.Should().Contain("### Цель\nнастроить значки");
        result.Draft.Split('\n')[^1].Should().Be($"Исходный чат: {chat.Id} — детали через chats_history.");
        result.Draft.Should().NotContain("временный");
    }

    [Fact]
    public async Task Handoff_СтарыйЧатНеМеняется()
    {
        var (sut, _, chat) = BuildSut(new StubRunner("записка"));
        var updatedAt = chat.UpdatedAt = DateTime.UtcNow.AddHours(-3);
        var csid = chat.ClaudeSessionId;

        await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        chat.UpdatedAt.Should().Be(updatedAt);
        chat.ClaudeSessionId.Should().Be(csid);
        chat.ArchivedAt.Should().BeNull();
        chat.ArchiveSummary.Should().BeNull();
    }

    [Fact]
    public async Task Handoff_ВременныйЧат_ПредупреждениеВЧерновике_НовыйНеВременный()
    {
        var (sut, _, chat) = BuildSut(new StubRunner("записка"));
        chat.ExpiresAfterMinutes = 60;

        var result = await sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        result.Draft.Should().Contain("Исходный чат временный");
        result.Session.ExpiresAfterMinutes.Should().BeNull();
    }

    [Fact]
    public async Task Handoff_МестоНаЛокали_ОтказДоМодели()
    {
        var runner = new StubRunner("записка") { Local = true };
        var (sut, mgr, chat) = BuildSut(runner);
        var before = mgr.GetAll().Count;

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage(ChatHandoffService.LocalRouteRefusal);
        runner.Calls.Should().Be(0);
        mgr.GetAll().Count.Should().Be(before);
    }

    [Fact]
    public async Task Handoff_ПустойЧат_НечегоПередавать()
    {
        var runner = new StubRunner("записка");
        var (sut, mgr, chat) = BuildSut(runner, withHistory: false);
        var before = mgr.GetAll().Count;

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage(ChatHandoffService.NothingToHandOff);
        runner.Calls.Should().Be(0);
        mgr.GetAll().Count.Should().Be(before);
    }

    [Fact]
    public async Task Handoff_ГрупповойЧат_Отказ()
    {
        var runner = new StubRunner("записка");
        var (sut, _, chat) = BuildSut(runner);
        chat.Participants = ["p1"];

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("Групповой чат нельзя переносить*");
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Handoff_ЧужойЧат_KeyNotFound()
    {
        var (sut, _, chat) = BuildSut(new StubRunner("записка"));

        var act = () => sut.HandoffAsync("чужой", chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Handoff_ПустойОтветМодели_ОшибкаГенерации_ЧатНеСоздан()
    {
        var (sut, mgr, chat) = BuildSut(new StubRunner("   "));
        var before = mgr.GetAll().Count;

        var act = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);

        await act.Should().ThrowExactlyAsync<HandoffGenerationException>();
        mgr.GetAll().Count.Should().Be(before);
    }

    [Fact]
    public async Task Handoff_ДвойнойКлик_409()
    {
        var runner = new StubRunner("записка") { Gated = true };
        var (sut, _, chat) = BuildSut(runner);

        var first = sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);
        var second = () => sut.HandoffAsync(TestUserId, chat.Id, CancellationToken.None);
        await second.Should().ThrowExactlyAsync<HandoffInProgressException>();

        runner.Release();
        await first;
        runner.Calls.Should().Be(1);
    }

    // ─── Сборка SUT ──────────────────────────────────────────────────────────

    private (ChatHandoffService Sut, SessionManager Manager, Session Chat) BuildSut(
        StubRunner runner, bool withHistory = true)
    {
        var (mgr, projects) = BuildManager();
        var sut = new ChatHandoffService(mgr, projects, runner, NullLogger<ChatHandoffService>.Instance);
        return (sut, mgr, NewProjectChat(mgr, projects, withHistory));
    }

    private (SessionManager Manager, ProjectManager Projects) BuildManager()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["Session:AutoSaveSeconds"] = "0",
            ["DefaultProjectsPath"] = Path.Combine(_tempDir, "homes"),
            ["ClaudeUserProfileDir"] = Path.Combine(_tempDir, "claude-profile"),
        }).Build();

        var userStore = new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        _history = new ChatHistoryService(config);

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

        var manager = new SessionManager(projectManager, _history, config, adapters, falCost,
            usage, appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance,
            TestLauncherFactory.Instance, sandbox, cheap: null, broadcaster: new TestSessionBroadcaster());
        return (manager, projectManager);
    }

    private Session NewProjectChat(SessionManager mgr, ProjectManager projects, bool withHistory)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_tempDir, "proj_" + Guid.NewGuid().ToString("N"))).FullName;
        var project = projects.Create("Проект переноса", dir, TestUserId, TestUsername);
        // История — на диске ДО создания чата: StartNewSessionAsync читает её один раз
        string? csid = null;
        if (withHistory)
        {
            csid = "handoff_hist_" + Guid.NewGuid().ToString("N");
            _history.SaveAsync(csid,
            [
                new StoredUserMessage("Настройте значки проектов"),
                new StoredTextMessage("Готово, значки настроены"),
            ]).GetAwaiter().GetResult();
        }
        return mgr.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: csid).GetAwaiter().GetResult();
    }

    // Стаб раннера: считает вызовы, помнит параметры, по Gated висит до Release()
    private sealed class StubRunner(string answer) : ICheapTextRunner
    {
        public int Calls;
        public bool Local;
        public bool Gated;
        public string? LastActionKey, LastOwnerId, LastPrompt;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public bool UsesLocal(string actionKey) => Local;
        public bool HasFreeRoute(string actionKey) => false;
        public string DescribeRoute(string actionKey, string? fallbackModel) => "stub";

        public async Task<string> RunAsync(string actionKey, string prompt, string? fallbackModel = null,
            string? ownerId = null, object? jsonFormat = null, CancellationToken ct = default)
        {
            Calls++;
            LastActionKey = actionKey;
            LastOwnerId = ownerId;
            LastPrompt = prompt;
            if (Gated) await _release.Task.WaitAsync(ct);
            return answer;
        }

        public Task<string?> RunFreeAsync(string actionKey, string prompt, object? jsonFormat = null,
            CancellationToken ct = default) => Task.FromResult<string?>(answer);

        public Task<string?> RunLocalOnlyAsync(string actionKey, string prompt,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<OneShotResult> RunDetailedAsync(string actionKey, string prompt,
            string? fallbackModel = null, string? ownerId = null, TimeSpan? timeout = null,
            int? maxTokens = null, object? jsonFormat = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
