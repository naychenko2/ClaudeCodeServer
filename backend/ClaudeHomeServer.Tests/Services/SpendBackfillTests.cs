using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Spend;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// Доработка по ревью Глеба (major-1, major-2, minor-3):
// 1) backfill идемпотентен при сбое — рестарт посреди импорта (маркер backfill.done не стоит)
//    не задваивает уже записанную часть: детерминированные Id + дедуп SpendStore.Record;
// 2) own в topTurns обзора считается от ТЕКУЩЕГО пользователя, а не от фильтра среза;
// 3) WindowClamped в Turns учитывает фильтр среза, а не любые daily-строки периода.
public class SpendBackfillTests : IDisposable
{
    private readonly string _dir;
    private readonly string _spendDir;
    private readonly UserStore _userStore;
    private readonly ProjectManager _projectManager;
    private readonly PersonaManager _personas;
    private readonly TaskManager _tasks;
    private readonly ChatHistoryService _history;
    private readonly SessionManager _sessions;

    public SpendBackfillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spend_backfill_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _spendDir = Path.Combine(_dir, "spend");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, _userStore, appSettings);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);
        _history = new ChatHistoryService(config);

        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var flags = new FeatureFlagService(_userStore);
        var notesSvc = new NotesService(_projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        var personaMemory = new PersonaMemoryService(knowledge, _personas, _userStore, config, NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(_personas, _projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(_projectManager, hub.Object, _history, config, adapters, falCost, usage,
            appSettings, _userStore, jwt, server.Object, llmProviders, notesKb, flags, _personas, personaMemory,
            bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendMaintenanceService NewMaintenance(SpendStore store) =>
        new(store, _sessions, _history, NullLogger<SpendMaintenanceService>.Instance);

    private static List<StoredMessage> Turns(int count) =>
        [.. Enumerable.Range(0, count).Select(StoredMessage (i) =>
            new StoredResultMessage("success", durationMs: 1000 + i, numTurns: 1,
                usage: new UsageInfo(10, 5, 100, 20)))];

    // --- major-1: идемпотентность backfill при оборванном прогоне ---

    [Fact]
    public async Task Backfill_ПовторПослеОборванногоПрогона_НеДублирует()
    {
        var user = _userStore.Add("bf-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_bf")).FullName;
        var project = _projectManager.Create("BF", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        session.ClaudeSessionId = "cs-backfill-1";

        // t0 в будущем — граница live-дедупа не отсекает импортируемые записи
        var t0 = DateTime.UtcNow.AddDays(1);

        // «Оборванный прогон»: успела импортироваться половина истории (2 хода из 4),
        // сервер упал — маркер backfill.done не поставлен
        await _history.SaveAsync(session.ClaudeSessionId, Turns(2));
        var store1 = new SpendStore(_spendDir, detailDays: 30);
        await NewMaintenance(store1).BackfillAsync(t0, CancellationToken.None);
        store1.BackfillDone.Should().BeFalse("маркер ставит ExecuteAsync только после полного импорта");

        // Рестарт сервера: стор перечитывает jsonl с диска, backfill стартует заново
        // уже по полной истории
        await _history.SaveAsync(session.ClaudeSessionId, Turns(4));
        var store2 = new SpendStore(_spendDir, detailDays: 30);
        var imported = await NewMaintenance(store2).BackfillAsync(t0, CancellationToken.None);

        var all = store2.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Should().HaveCount(4, "уже импортированная при первом прогоне половина не задваивается");
        all.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        imported.Should().Be(4, "счётчик Record-вызовов прогона; дубли отсеял стор");

        // И контрольный полный повтор (маркер так и не стоял) — состав не меняется
        await NewMaintenance(store2).BackfillAsync(t0, CancellationToken.None);
        store2.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Should().HaveCount(4);
    }

    // --- major-2: own в topTurns — от текущего пользователя, не от фильтра ---

    [Fact]
    public void Overview_TopTurns_OwnОтТекущегоПользователя_АНеОтФильтра()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_own"), detailDays: 30);
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { OwnerId = "admin-1", Timestamp = now, InputTokens = 100 });
        store.Record(new SpendRecord { OwnerId = "user-2", Timestamp = now, InputTokens = 50 });
        var analytics = new SpendAnalyticsService(store, _sessions, _projectManager, _tasks, _personas, _userStore);
        var today = DateOnly.FromDateTime(now);

        // Админ в scope=all: фильтр без владельца — его собственные ходы обязаны быть own
        var all = analytics.Overview(today, today, new SpendFilter(), allUsers: true, currentUserId: "admin-1");
        all.TopTurns.Should().HaveCount(2);
        all.TopTurns.Single(t => t.OwnerId == "admin-1").Own.Should().BeTrue();
        all.TopTurns.Single(t => t.OwnerId == "user-2").Own.Should().BeFalse();

        // Админ сузил scope=all&user=X: чужие ходы не становятся own из-за фильтра
        var narrowed = analytics.Overview(today, today, new SpendFilter(Owner: "user-2"),
            allUsers: true, currentUserId: "admin-1");
        narrowed.TopTurns.Single().Own.Should().BeFalse();
    }

    // --- minor-3: WindowClamped учитывает фильтр среза ---

    [Fact]
    public void Turns_WindowClamped_УчитываетФильтрСреза()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_clamp"), detailDays: 30);
        var now = DateTime.UtcNow;
        var old = now.AddDays(-40);
        store.Record(new SpendRecord { OwnerId = "u1", Timestamp = old, InputTokens = 100 });
        store.Record(new SpendRecord { OwnerId = "u1", Timestamp = now, InputTokens = 10 });
        store.Record(new SpendRecord { OwnerId = "u2", Timestamp = now, InputTokens = 20 });
        store.RollupOlderThan(store.WindowStart);
        var analytics = new SpendAnalyticsService(store, _sessions, _projectManager, _tasks, _personas, _userStore);
        var from = DateOnly.FromDateTime(old);
        var to = DateOnly.FromDateTime(now);

        // У u1 за окном есть свёрнутые строки — плашка «часть ходов старше окна» честная
        analytics.Turns(from, to, new SpendFilter(Owner: "u1"), 50, 0, null, "u1")
            .WindowClamped.Should().BeTrue();

        // У u2 за окном пусто — до фикса плашка показывалась и ему (любые daily без фильтра)
        analytics.Turns(from, to, new SpendFilter(Owner: "u2"), 50, 0, null, "u2")
            .WindowClamped.Should().BeFalse();
    }
}
