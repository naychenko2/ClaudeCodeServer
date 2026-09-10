using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Тестовый дубль ChatHistoryService: вставляет await Task.Yield() между LoadAsync и
// SaveAsync. Без этого ChatHistoryService синхронен (Task.FromResult/Task.CompletedTask),
// и Task.WhenAll над несколькими публикациями выполняет их инлайн последовательно —
// тест на конкурентность видит зелёное даже при полностью снятом _falPersistLock,
// потому что гонки физически нет. Yield даёт планировщику шанс переключить потоки
// ровно в том окне, где в проде происходит переключение контекста.
internal sealed class YieldingChatHistoryService : ChatHistoryService
{
    public YieldingChatHistoryService(IConfiguration config) : base(config) { }

    public override async Task<List<StoredMessage>> LoadAsync(string claudeSessionId)
    {
        var result = await base.LoadAsync(claudeSessionId);
        await Task.Yield();
        return result;
    }
}

// Консолидация _falPersistLock (этап 4, волна 2→3): все внеходовые операции над историей
// теперь идут через WithFalPersistLockAsync, общий шов для дедупа
// AppendIfNotDuplicateStoredNoLockAsync. Тесты ниже ловят нарушение инварианта «двойной
// клик применился ровно один раз» под РЕАЛЬНОЙ конкуренцией (yield между Load/Save
// создаёт окно гонки; без него Task.WhenAll над синхронными операциями выполнит их
// инлайн и не увидит проблемы).
[Collection(TestCollections.SessionStaticResolvers)]
public class FalPersistLockDedupTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionManager _sessions;
    private readonly UserStore _userStore;
    private readonly ProjectManager _projectManager;
    private readonly YieldingChatHistoryService _history;
    private readonly TestSessionBroadcaster _broadcaster = new();

    public FalPersistLockDedupTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fal_persist_lock_" + Guid.NewGuid().ToString("N"));
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
        _history = new YieldingChatHistoryService(config);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
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
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var spend = new SpendStore(Path.Combine(_dir, "spend"), detailDays: 30);

        _sessions = new SessionManager(_projectManager, _history, config, adapters, falCost, usage,
            appSettings, _userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            _broadcaster, spend: spend, glif: glif);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PublishGlifCostAsync_ДесятьПараллельныхПубликацийОдногоJob_ДаютРовноОднуЗапись()
    {
        var user = _userStore.Add("lock-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_lock")).FullName;
        var project = _projectManager.Create("Lock", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cs-lock-glif-1");

        // CreateAsync инициализирует Accumulator (с собственным внутренним локом), и
        // PublishGlifCostAsync предпочтёт его дисковой ветке. Сбрасываем Accumulator в null
        // через рефлексию, чтобы тест пробежал именно по дисковой ветке через WithFalPersistLockAsync
        // и AppendIfNotDuplicateStoredNoLockAsync — то есть по тому пути, который защищает _falPersistLock.
        ClearAccumulator(session.Id);

        var msg = new GlifCostMessage("job-concurrent-1", "image", 1, 1.0, "model_x");

        // Десять одновременных публикаций одного и того же job_id. Без _falPersistLock
        // yield-окно между Load и Save даёт всем десяти прочитать пустую историю: каждая
        // считает себя первой, дописывает запись и рассылает свой broadcast. В файле
        // после гонки остаётся запись последнего SaveAsync, а в эфир уходит до десяти
        // GlifCostMessage вместо одного — на этом тест и краснеет без лока.
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _sessions.PublishGlifCostAsync(session.Id, msg))
            .ToArray();
        await Task.WhenAll(tasks);

        var history = await _history.LoadAsync(session.ClaudeSessionId!);
        history.OfType<StoredGlifCostMessage>().Should().HaveCount(1);
        _broadcaster.Session.Select(t => t.Message).OfType<GlifCostMessage>().Should().HaveCount(1);
    }

    [Fact]
    public async Task AppendStoredAsync_ДесятьПараллельныхЗаписейРазныхСообщений_ДаютРовноДесять()
    {
        var user = _userStore.Add("append-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_append")).FullName;
        var project = _projectManager.Create("Append", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cs-lock-append-1");

        // Сбрасываем Accumulator, чтобы пройти через дисковую ветку AppendStoredAsync.
        ClearAccumulator(session.Id);

        // Десять параллельных добавлений РАЗНЫХ сообщений под _falPersistLock. Без лока
        // yield-окно даёт всем десяти увидеть пустую историю в начале: каждый добавляет
        // свой текст в локальный список, и последний SaveAsync перетирает остальные — в
        // файле остаётся ровно одно сообщение, не десять. Тест ловит регрессию «лок не
        // сериализует доступ к файлу», если AppendStoredAsync когда-нибудь начнёт брать
        // лок в обход WithFalPersistLockAsync / AppendIfNotDuplicateStoredNoLockAsync.
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _sessions.AppendStoredAsync(
                session.Id,
                new StoredTextMessage($"concurrent-append-payload-{i}"),
                new UserMessageMessage($"concurrent-append-payload-{i}", null, null, true, null, null)))
            .ToArray();
        await Task.WhenAll(tasks);

        var history = await _history.LoadAsync(session.ClaudeSessionId!);
        history.OfType<StoredTextMessage>().Should().HaveCount(10);
        for (var i = 0; i < 10; i++)
            history.OfType<StoredTextMessage>().Select(m => m.Text)
                .Should().Contain($"concurrent-append-payload-{i}");
    }

    // Сбрасывает Accumulator в null у записи SessionEntry — без этого CreateAsync
    // инициализирует Accumulator и Publish*/AppendStored используют его ветку
    // (у которой собственный внутренний лок). Сбрасывая Accumulator, заставляем
    // идти через дисковую ветку — ту, что защищена нашим _falPersistLock.
    private void ClearAccumulator(string sessionId)
    {
        var entriesField = typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var entries = (System.Collections.IDictionary)entriesField!.GetValue(_sessions)!;
        var entry = entries[sessionId]!;
        var accProp = entry.GetType().GetField("Accumulator")!;
        accProp.SetValue(entry, null);
    }
}
