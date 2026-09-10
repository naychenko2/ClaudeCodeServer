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

// Регрессионный тест на гонку «выбор ветки вне _falPersistLock» (ревью волны 2, Blocker 1):
// PublishFalCostAsync читал entry.Accumulator ДО лока, выбирал дисковую ветку и парковался
// на _falPersistLock. EnsureAccumulatorAsync под тем же локом успевал загрузить историю
// БЕЗ публикуемой стоимости, создать аккумулятор, и первый же SaveSnapshotAsync затирал
// дописанную на диск стоимость. Фикс волны 3: проверка entry.Accumulator и запись теперь
// атомарны под WithFalPersistLockAsync. Тест воспроизводит сценарий руками (берёт
// _falPersistLock снаружи, имитируя тело EnsureAccumulatorAsync) и проверяет, что после
// публикации + SaveSnapshotAsync аккумулятора стоимость всё ещё в файле.
public class FalAccumulatorRaceRegressionTests : IDisposable
{
    private readonly string _dir;
    private readonly SessionManager _sessions;
    private readonly UserStore _userStore;
    private readonly ProjectManager _projectManager;
    private readonly ChatHistoryService _history;

    public FalAccumulatorRaceRegressionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fal_race_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, _userStore, appSettings);
        var personas = new PersonaManager(config);
        _history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(
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
        var bindings = new PersonaBindingsService(personas, _projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var spend = new SpendStore(Path.Combine(_dir, "spend"), detailDays: 30);

        _sessions = new SessionManager(_projectManager, _history, config, adapters, falCost, usage,
            appSettings, _userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster, spend: spend, glif: glif);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PublishFalCost_ОживлениеАккумулятораПодЛоком_СохраняетСтоимость()
    {
        var user = _userStore.Add("race-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_race")).FullName;
        var project = _projectManager.Create("Race", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto, resumeSessionId: "cs-race-1");
        var csid = session.ClaudeSessionId!;
        ClearAccumulator(session.Id);

        var sem = (SemaphoreSlim)typeof(SessionManager)
            .GetField("_falPersistLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_sessions)!;

        // Имитируем «уже идёт EnsureAccumulatorAsync»: держим _falPersistLock снаружи.
        // С фиксом волны 3 PublishFalCostAsync читает entry.Accumulator только ПОД локом,
        // поэтому увидит уже оживлённый аккумулятор и пойдёт его веткой. С багом волны 2
        // ветка выбиралась ДО WaitAsync — публикация ушла бы на диск, а SaveSnapshotAsync
        // ниже затёр бы дописанную стоимость.
        await sem.WaitAsync();

        var publish = _sessions.PublishFalCostAsync(session.Id,
            new FalCostMessage("req-race-1", "fal-ai/endpoint", 0.42));
        await Task.Delay(200);
        publish.IsCompleted.Should().BeFalse("публикация должна ждать семафор, а не выбирать ветку вне лока");

        // Тело EnsureAccumulatorAsync под локом: грузим историю (стоимости в ней ещё нет)
        // и ставим аккумулятор — ровно тот момент, в который раньше вклинивалась гонка.
        var existing = await _history.LoadAsync(csid);
        var acc = new TurnAccumulator(existing, csid);
        SetAccumulator(session.Id, acc);
        sem.Release();

        await publish;
        (await _history.LoadAsync(csid)).OfType<StoredFalCostMessage>()
            .Should().HaveCount(1, "публикация записала стоимость");

        // Любая активность хода сохраняет снимок аккумулятора поверх файла. До фикса этот
        // вызов писал устаревший снимок (загруженный без стоимости) и запись исчезала.
        await acc.SaveSnapshotAsync(_history);

        (await _history.LoadAsync(csid)).OfType<StoredFalCostMessage>()
            .Should().HaveCount(1, "стоимость не должна теряться при оживлении аккумулятора");
    }

    private void ClearAccumulator(string sessionId) => SetAccumulator(sessionId, null);

    private void SetAccumulator(string sessionId, object? value)
    {
        var entriesField = typeof(SessionManager).GetField("_sessions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var entries = (System.Collections.IDictionary)entriesField!.GetValue(_sessions)!;
        var entry = entries[sessionId]!;
        entry.GetType().GetField("Accumulator")!.SetValue(entry, value);
    }
}
