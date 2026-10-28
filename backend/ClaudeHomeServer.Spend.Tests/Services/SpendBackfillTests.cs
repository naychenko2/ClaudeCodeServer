using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
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
    private readonly ITaskLookup _taskLookup;
    private readonly ChatHistoryService _history;
    private readonly SessionManager _sessions;
    private readonly ClaudeHomeServer.Services.Llm.LlmProviderRegistry _llmProviders;
    // Этап 5, шаг 3: Spend идёт через Core-шов IModelResolver, не напрямую через Llm-реестр.
    private readonly ClaudeHomeServer.Services.Llm.IModelResolver _llmResolver;

    public SpendBackfillTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "spend_backfill_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _spendDir = Path.Combine(_dir, "spend");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["Glif:McpToken"] = "glif-test-token",
                // Локальный провайдер — для тестов «локальный = бесплатный»
                ["LlmProviders:local-qwen:IsLocal"] = "true",
                ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        _projectManager = new ProjectManager(config, _userStore, appSettings);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);
        _taskLookup = new TaskLookupAdapter(_tasks);
        _history = new ChatHistoryService(config);

        var broadcaster = new TestSessionBroadcaster();

        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        _llmProviders = llmProviders;
        // Этап 5, шаг 3: Spend идёт через Core-шов IModelResolver. В тестах оборачиваем
        // реальный реестр тем же адаптером, что и в DI — единая точка, никакой магии.
        _llmResolver = new LlmModelResolverAdapter(llmProviders);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)),
            llmProviders, subPool);
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
        var bindings = new PersonaBindingsService(_personas, _projectManager, wkStore,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(_projectManager, _history, config, adapters, falCost, usage,
            appSettings, _userStore, jwt, server.Object, llmProviders, flags, _personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster, glif: glif);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SpendMaintenanceService NewMaintenance(SpendStore store) =>
        new(store,
            new SessionDirectoryAdapter(_sessions),
            new ChatHistoryLoaderAdapter(_history),
            _llmResolver,
            NullLogger<SpendMaintenanceService>.Instance);

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
        var analytics = new SpendAnalyticsService(store,
            new SessionDirectoryAdapter(_sessions),
            _projectManager, _taskLookup,
            new PersonaLookupAdapter(_personas),
            _userStore, _llmResolver);
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
        var analytics = new SpendAnalyticsService(store,
            new SessionDirectoryAdapter(_sessions),
            _projectManager, _taskLookup,
            new PersonaLookupAdapter(_personas),
            _userStore, _llmResolver);
        var from = DateOnly.FromDateTime(old);
        var to = DateOnly.FromDateTime(now);

        // У u1 за окном есть свёрнутые строки — плашка «часть ходов старше окна» честная
        analytics.Turns(from, to, new SpendFilter(Owner: "u1"), 50, 0, null, "u1")
            .WindowClamped.Should().BeTrue();

        // У u2 за окном пусто — до фикса плашка показывалась и ему (любые daily без фильтра)
        analytics.Turns(from, to, new SpendFilter(Owner: "u2"), 50, 0, null, "u2")
            .WindowClamped.Should().BeFalse();
    }

    // --- glif: backfill учитывает StoredGlifCostMessage ---

    [Fact]
    public async Task Backfill_GlifCost_СоздаетSpendRecord()
    {
        var user = _userStore.Add("glif-bf-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_glif_bf")).FullName;
        var project = _projectManager.Create("GlifBF", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        session.ClaudeSessionId = "cs-glif-backfill-1";

        var history = new List<StoredMessage>
        {
            new StoredGlifCostMessage("job-glif-1", "image", 2, 9.2, "image_seedream_v45_v1"),
        };
        await _history.SaveAsync(session.ClaudeSessionId, history);

        var store = new SpendStore(_spendDir, detailDays: 30);
        var imported = await NewMaintenance(store).BackfillAsync(DateTime.UtcNow.AddDays(1), CancellationToken.None);

        imported.Should().Be(1);
        var all = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Should().HaveCount(1);
        var r = all[0];
        r.Provider.Should().Be("glif");
        r.Source.Should().Be(SpendSources.Glif);
        r.Generations.Should().Be(1);
        r.Model.Should().Be("image_seedream_v45_v1");
        r.Label.Should().Be("image");
        r.CostUsd.Should().BeNull();
    }

    // --- «локальный = бесплатный»: путь записи и пересчёт истории ---

    [Fact]
    public async Task Backfill_ЛокальныйПровайдер_ПишетсяКакБесплатный()
    {
        var user = _userStore.Add("loc-user", "pw-123456", "user");
        var projDir = Directory.CreateDirectory(Path.Combine(_dir, "proj_loc")).FullName;
        var project = _projectManager.Create("LOC", projDir, user.Id, user.Username);
        var session = await _sessions.CreateAsync(project.Id, ClaudeMode.Auto);
        session.ClaudeSessionId = "cs-local-1";
        session.Provider = "local-qwen";
        session.Model = "qwen3.8-27b";
        await _history.SaveAsync(session.ClaudeSessionId, Turns(2));

        var store = new SpendStore(Path.Combine(_dir, "spend_local"), detailDays: 30);
        await NewMaintenance(store).BackfillAsync(DateTime.UtcNow.AddDays(1), CancellationToken.None);

        var all = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Should().HaveCount(2);
        all.Should().OnlyContain(r => r.Source == SpendSources.Free,
            "IsLocal-провайдер — своя машина, подписки за ней нет");
    }

    [Fact]
    public void Reclassify_ЛокальныеХоды_СтановятсяБесплатными_Идемпотентно()
    {
        var dir = Path.Combine(_dir, "spend_reclass");
        var store = new SpendStore(dir, detailDays: 30);
        var now = DateTime.UtcNow;
        // Записи, оставшиеся chat-turn/one-shot до учёта IsLocal
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "local-qwen", Model = "qwen3.8-27b",
            InputTokens = 100, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "local-qwen", Model = "qwen3.8-27b",
            Source = SpendSources.OneShot, InputTokens = 50, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "claude", Model = "opus",
            InputTokens = 10, Timestamp = now });

        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(2);
        // Идемпотентность: уже бесплатных не трогает, повторный проход — no-op
        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(0);

        // Пересчёт переживает перезагрузку: на диске — free
        var reloaded = new SpendStore(dir, detailDays: 30);
        var all = reloaded.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Where(r => r.Provider == "local-qwen").Should().OnlyContain(r => r.Source == SpendSources.Free);
        all.Single(r => r.Provider == "claude").Source.Should().Be(SpendSources.ChatTurn);
    }

    [Fact]
    public void Reclassify_СвёрнутыеДни_СливаютсяПоКлючуБезДвойногоСчёта()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_reclass_daily"), detailDays: 30);
        var old = DateTime.UtcNow.AddDays(-40);
        // Два хода одной модели в одном чате: один записан как chat-turn, другой — уже free
        store.Record(new SpendRecord { Id = "loc-r1", OwnerId = "u1", SessionId = "s1",
            Provider = "local-qwen", Model = "qwen3.8-27b", InputTokens = 100, Timestamp = old });
        store.Record(new SpendRecord { Id = "loc-r2", OwnerId = "u1", SessionId = "s1",
            Provider = "local-qwen", Model = "qwen3.8-27b", Source = SpendSources.Free,
            InputTokens = 30, Timestamp = old });
        store.RollupOlderThan(store.WindowStart);

        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(1);

        var rows = store.DailyBetween(DateOnly.MinValue, DateOnly.MaxValue);
        var row = rows.Single(r => r.Provider == "local-qwen"); // слитая строка, не две
        row.Source.Should().Be(SpendSources.Free);
        row.InputTokens.Should().Be(130);
        row.Turns.Should().Be(2);
    }

    // Обратный проход (решение Андрея, 26.10): платный прямой адаптер, записанный free по
    // старому правилу «любой *-direct бесплатен», уходит в one-shot; freellm* (с -direct и
    // без) и локальные провайдеры остаются free. Идемпотентен в обе стороны.
    [Fact]
    public void Reclassify_ПлатныеПрямыеАдаптерыУходятИзБесплатных_FreellmОстаются_Идемпотентно()
    {
        var dir = Path.Combine(_dir, "spend_reclass_direct");
        var store = new SpendStore(dir, detailDays: 30);
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "minimax-direct", Model = "MiniMax-M3",
            Source = SpendSources.Free, Label = "changelog", InputTokens = 100, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "kimi-direct", Model = "kimi-k3",
            Source = SpendSources.Free, Label = "action-rank", InputTokens = 40, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "freellmapi-direct", Model = "auto:fast",
            Source = SpendSources.Free, Label = "notes.tags", InputTokens = 50, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "local-qwen", Model = "qwen3.8-27b",
            Source = SpendSources.Free, InputTokens = 20, Timestamp = now });
        // freellm без «-direct», записанный one-shot: новый IsFree приводит его в free
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "freellmapi", Model = "auto:fast",
            Source = SpendSources.OneShot, Label = "chat-title", InputTokens = 10, Timestamp = now });

        store.ReclassifyPaidDirectFromFree(_ => false).Should().Be(2);
        store.ReclassifyLocalAsFree(_ => false).Should().Be(1);
        store.ReclassifyPaidDirectFromFree(_ => false).Should().Be(0);
        store.ReclassifyLocalAsFree(_ => false).Should().Be(0);

        var reloaded = new SpendStore(dir, detailDays: 30);
        var all = reloaded.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Single(r => r.Provider == "minimax-direct").Source.Should().Be(SpendSources.OneShot);
        all.Single(r => r.Provider == "kimi-direct").Source.Should().Be(SpendSources.OneShot);
        all.Single(r => r.Provider == "freellmapi-direct").Source.Should().Be(SpendSources.Free);
        all.Single(r => r.Provider == "freellmapi").Source.Should().Be(SpendSources.Free);
        all.Single(r => r.Provider == "local-qwen").Source.Should().Be(SpendSources.Free);
    }

    // Условия двух проходов взаимоисключающие: в часовом цикле записи не переключаются
    // туда-обратно — второй раунд обоих даёт 0.
    [Fact]
    public void Reclassify_ДваПроходаНеВозвращаютЗаписиДругДруга()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_reclass_pingpong"), detailDays: 30);
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "minimax-direct", Model = "MiniMax-M3",
            Source = SpendSources.Free, Label = "changelog", InputTokens = 100, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "local-qwen", Model = "qwen3.8-27b",
            InputTokens = 20, Timestamp = now });

        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(1);
        store.ReclassifyPaidDirectFromFree(p => p == "local-qwen").Should().Be(1);
        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(0);
        store.ReclassifyPaidDirectFromFree(p => p == "local-qwen").Should().Be(0);
    }

    // Обратный проход не трогает бесплатное по модели («:free») и локальное по БАЗОВОМУ ключу
    // («local-x-direct» → реестр спрашивается про «local-x», а не про полный ключ).
    [Fact]
    public void Reclassify_ПлатныйПрямойАдаптер_МодельFreeИЛокальныйБазовыйКлюч_ОстаютсяFree()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_reclass_direct_keep"), detailDays: 30);
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "openrouter-direct", Model = "nvidia/nemotron:free",
            Source = SpendSources.Free, Label = "a", InputTokens = 10, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "local-x-direct", Model = "loc",
            Source = SpendSources.Free, Label = "b", InputTokens = 10, Timestamp = now });
        store.Record(new SpendRecord { OwnerId = "u1", Provider = "openrouter-direct", Model = "paid/model",
            Source = SpendSources.Free, Label = "c", InputTokens = 10, Timestamp = now });

        store.ReclassifyPaidDirectFromFree(p => p == "local-x").Should().Be(1);

        var all = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Single(r => r.Model == "nvidia/nemotron:free").Source.Should().Be(SpendSources.Free);
        all.Single(r => r.Provider == "local-x-direct").Source.Should().Be(SpendSources.Free);
        all.Single(r => r.Model == "paid/model").Source.Should().Be(SpendSources.OneShot);
    }

    // Проводка: часовой цикл SpendMaintenanceService реально зовёт обратный проход.
    // Тест на сам цикл (StartAsync), а не на метод хранилища: удаление вызова его краснит.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Обслуживание_ЧасовойЦикл_ПереводитПлатныйПрямойАдаптерИзFreeВOneShot()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_maintenance_loop"), detailDays: 30);
        store.MarkBackfillDone(); // backfill не нужен — проверяем только цикл обслуживания
        store.Record(new SpendRecord { Id = "loop-1", OwnerId = "u1", Provider = "minimax-direct", Model = "MiniMax-M3",
            Source = SpendSources.Free, Label = "changelog", InputTokens = 100, Timestamp = DateTime.UtcNow });

        var service = NewMaintenance(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Цикл стартует через 5 с после запуска; ждём факт, а не фиксированную паузу
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                   && store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single().Source != SpendSources.OneShot)
                await Task.Delay(100);
        }
        finally { await service.StopAsync(CancellationToken.None); }

        store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single().Source.Should().Be(SpendSources.OneShot);
    }

    [Fact]
    public void Reclassify_ПлатныеПрямыеАдаптерыВСвёрнутыхДнях_СливаютсяБезДвойногоСчёта()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_reclass_direct_daily"), detailDays: 30);
        var old = DateTime.UtcNow.AddDays(-40);
        // Два вызова одной модели в один день: один записан free (старое правило), другой one-shot
        store.Record(new SpendRecord { Id = "dir-r1", OwnerId = "u1", Provider = "minimax-direct", Model = "MiniMax-M3",
            Source = SpendSources.Free, Label = "changelog", InputTokens = 100, Timestamp = old });
        store.Record(new SpendRecord { Id = "dir-r2", OwnerId = "u1", Provider = "minimax-direct", Model = "MiniMax-M3",
            Source = SpendSources.OneShot, Label = "action-rank", InputTokens = 30, Timestamp = old });
        store.RollupOlderThan(store.WindowStart);

        store.ReclassifyPaidDirectFromFree(_ => false).Should().Be(1);

        var rows = store.DailyBetween(DateOnly.MinValue, DateOnly.MaxValue);
        var row = rows.Single(r => r.Provider == "minimax-direct"); // слитая строка, не две
        row.Source.Should().Be(SpendSources.OneShot);
        row.InputTokens.Should().Be(130);
        row.Turns.Should().Be(2);
    }

    // --- SP4: разовый rekey прямых пар «Claude-ключ + не-Claude модель» ---

    // Резолв модели как у реестра в проде: MiniMax → minimax, qwen → локальный ключ,
    // ghostcli перехватывает префикс «claude-» (поэтому родную модель отсекает предикат ДО резолва)
    private static string? FakeProviderByModel(string? model) => model switch
    {
        not null when model.StartsWith("MiniMax", StringComparison.OrdinalIgnoreCase) => "minimax",
        not null when model.StartsWith("qwen", StringComparison.OrdinalIgnoreCase) => "local-qwen",
        not null when model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) => "ghostcli",
        _ => null,
    };

    private static bool FakeIsNative(string? model) =>
        string.IsNullOrWhiteSpace(model) || model == "default" || model == "opus"
        || model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Rekey_ClaudeКлючСНеClaudeМоделью_ПереключаетсяНаПровайдераМодели_ЗатемLocalСтановитсяFree()
    {
        var dir = Path.Combine(_dir, "spend_rekey");
        var store = new SpendStore(dir, detailDays: 30);
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { Id = "rk-1", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3[1m]",
            InputTokens = 100, Timestamp = now });
        store.Record(new SpendRecord { Id = "rk-2", OwnerId = "u1", Provider = "claude-2", Model = "qwen3.8-27b",
            InputTokens = 50, Timestamp = now });

        store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel).Should().Be(2);
        // Порядок как в сервисе: rekey, затем часовой ReclassifyLocalAsFree
        store.ReclassifyLocalAsFree(p => p == "local-qwen").Should().Be(1);

        // Итог переживает перезагрузку с диска
        var all = new SpendStore(dir, detailDays: 30).DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        var mm = all.Single(r => r.Id == "rk-1");
        mm.Provider.Should().Be("minimax");
        mm.Source.Should().Be(SpendSources.ChatTurn);
        var qwen = all.Single(r => r.Id == "rk-2");
        qwen.Provider.Should().Be("local-qwen");
        qwen.Source.Should().Be(SpendSources.Free);
    }

    [Fact]
    public void Rekey_РодныеМоделиOneShotИDirect_НеТрогаются_ПовторныйЗапускНоль()
    {
        var dir = Path.Combine(_dir, "spend_rekey_keep");
        var store = new SpendStore(dir, detailDays: 30);
        var now = DateTime.UtcNow;
        // Подписка Claude с моделью claude-* (ghostcli перехватил бы префикс — трогать нельзя)
        store.Record(new SpendRecord { Id = "k-1", OwnerId = "u1", Provider = "claude-3", Model = "claude-opus-5-5",
            InputTokens = 10, Timestamp = now });
        // Алиас родного Claude и пустая модель
        store.Record(new SpendRecord { Id = "k-2", OwnerId = "u1", Provider = "claude", Model = "opus",
            InputTokens = 10, Timestamp = now });
        // Прямой адаптер — не Claude-ключ, суффикс «-direct» не срезается
        store.Record(new SpendRecord { Id = "k-3", OwnerId = "u1", Provider = "minimax-direct", Model = "MiniMax-M3",
            Source = SpendSources.OneShot, InputTokens = 10, Timestamp = now });
        // one-shot с Claude-ключом и не-Claude моделью — фильтр только chat-turn
        store.Record(new SpendRecord { Id = "k-4", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3",
            Source = SpendSources.OneShot, InputTokens = 10, Timestamp = now });
        // Обратная пара (minimax + модель Claude) — источника правды нет
        store.Record(new SpendRecord { Id = "k-5", OwnerId = "u1", Provider = "minimax", Model = "claude-opus-5-5",
            InputTokens = 10, Timestamp = now });
        // Модель не резолвится ни к какому провайдеру — не трогаем
        store.Record(new SpendRecord { Id = "k-6", OwnerId = "u1", Provider = "claude", Model = "unknown-x",
            InputTokens = 10, Timestamp = now });

        store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel).Should().Be(0);

        var all = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Single(r => r.Id == "k-1").Provider.Should().Be("claude-3");
        all.Single(r => r.Id == "k-2").Provider.Should().Be("claude");
        all.Single(r => r.Id == "k-3").Provider.Should().Be("minimax-direct");
        all.Single(r => r.Id == "k-4").Provider.Should().Be("claude");
        all.Single(r => r.Id == "k-5").Provider.Should().Be("minimax");
        all.Single(r => r.Id == "k-6").Provider.Should().Be("claude");

        // Идемпотентность на реально переключаемой записи: второй проход — ноль
        store.Record(new SpendRecord { Id = "k-7", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3",
            InputTokens = 10, Timestamp = now });
        store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel).Should().Be(1);
        store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel).Should().Be(0);
    }

    [Fact]
    public void Rekey_СвёрнутыеДни_СливаютсяПоКлючуБезДвойногоСчёта()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_rekey_daily"), detailDays: 30);
        var old = DateTime.UtcNow.AddDays(-40);
        // Уже правильная запись minimax и «кривая» claude+MiniMax в одном чате и дне
        store.Record(new SpendRecord { Id = "rd-1", OwnerId = "u1", SessionId = "s1",
            Provider = "minimax", Model = "MiniMax-M3", InputTokens = 100, Timestamp = old });
        store.Record(new SpendRecord { Id = "rd-2", OwnerId = "u1", SessionId = "s1",
            Provider = "claude", Model = "MiniMax-M3", InputTokens = 30, Timestamp = old });
        store.RollupOlderThan(store.WindowStart);

        store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel).Should().Be(1);

        var rows = store.DailyBetween(DateOnly.MinValue, DateOnly.MaxValue);
        rows.Should().NotContain(r => r.Provider == "claude");
        var row = rows.Single(r => r.Provider == "minimax"); // слитая строка, не две
        row.InputTokens.Should().Be(130);
        row.Turns.Should().Be(2);
    }

    // Проводка: сервис обслуживания реально зовёт rekey, ставит маркер и делает это ДО часового
    // ReclassifyLocalAsFree (локальная запись за один запуск доходит до free). Тест на сам цикл
    // (StartAsync): удаление вызова или перестановка проходов его краснит.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Обслуживание_Rekey_ЗапускаетсяОдинРазСМаркером_ДоЧасовогоReclassify()
    {
        var store = new SpendStore(Path.Combine(_dir, "spend_rekey_loop"), detailDays: 30);
        store.MarkBackfillDone();
        store.Record(new SpendRecord { Id = "rl-1", OwnerId = "u1", Provider = "claude-2", Model = "qwen3.8-27b",
            InputTokens = 100, Timestamp = DateTime.UtcNow });

        var resolver = new Mock<IModelResolver>();
        resolver.Setup(r => r.IsNativeClaudeModel(It.IsAny<string?>())).Returns<string?>(FakeIsNative);
        resolver.Setup(r => r.ResolveProviderKeyByModel(It.IsAny<string?>())).Returns<string?>(FakeProviderByModel);
        resolver.Setup(r => r.IsLocal(It.IsAny<string?>())).Returns<string?>(p => p == "local-qwen");

        var service = new SpendMaintenanceService(store, new SessionDirectoryAdapter(_sessions),
            new ChatHistoryLoaderAdapter(_history), resolver.Object, NullLogger<SpendMaintenanceService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                   && store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single().Source != SpendSources.Free)
                await Task.Delay(100);
        }
        finally { await service.StopAsync(CancellationToken.None); }

        var rec = store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single();
        rec.Provider.Should().Be("local-qwen");
        rec.Source.Should().Be(SpendSources.Free, "rekey идёт до ReclassifyLocalAsFree в том же запуске");
        store.ProviderRekeyDone.Should().BeTrue("маркер provider-rekey-v1.done ставится после прохода");
    }

    // --- SP4-доработка (вердикт Глеба a44fd2df, миноры 1, 2, 4): безопасная запись прохода ---

    // Пункт 1 (minor-1): сбой записи turns-*.jsonl в проходе пробрасывается — сервис не
    // ставит маркер, при рестарте (новый стор с диска) проход повторяется и дописывает диск.
    [Fact]
    [Trait("Category", "Slow")]
    public async Task Обслуживание_Rekey_СбойЗаписиTurns_МаркерНеСтавится_ПриРестартеПроходПовторяется()
    {
        var dir = Path.Combine(_dir, "spend_rekey_fail");
        var store = new SpendStore(dir, detailDays: 30);
        store.MarkBackfillDone();
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { Id = "rf-1", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3",
            InputTokens = 100, Timestamp = now });

        var resolver = new Mock<IModelResolver>();
        resolver.Setup(r => r.IsNativeClaudeModel(It.IsAny<string?>())).Returns<string?>(FakeIsNative);
        resolver.Setup(r => r.ResolveProviderKeyByModel(It.IsAny<string?>())).Returns<string?>(FakeProviderByModel);
        resolver.Setup(r => r.IsLocal(It.IsAny<string?>())).Returns<string?>(_ => false);
        var newService = (SpendStore s) => new SpendMaintenanceService(s,
            new SessionDirectoryAdapter(_sessions), new ChatHistoryLoaderAdapter(_history),
            resolver.Object, NullLogger<SpendMaintenanceService>.Instance);

        // Преграда записи: каталог вместо tmp-файла — запись дня не удаётся никогда
        var tmpInterference = Path.Combine(dir,
            $"turns-{DateOnly.FromDateTime(now):yyyy-MM-dd}.jsonl.tmp");
        Directory.CreateDirectory(tmpInterference);

        var service = newService(store);
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Факт попытки прохода: запись переключена в памяти (на диск не вышло)
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline
                   && store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single().Provider != "minimax")
                await Task.Delay(100);
        }
        finally { await service.StopAsync(CancellationToken.None); }

        store.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single().Provider.Should().Be("minimax",
            "проход был и упал на записи диска");
        store.ProviderRekeyDone.Should().BeFalse("сбой записи — маркер provider-rekey-v1.done не ставится");

        // Рестарт: преграда убрана, новый стор читает диск — там старый Provider, проход повторяется
        Directory.Delete(tmpInterference);
        var reloaded = new SpendStore(dir, detailDays: 30);
        reloaded.DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue).Single().Provider.Should().Be("claude",
            "упавший проход не дописал диск");

        var service2 = newService(reloaded);
        await service2.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !reloaded.ProviderRekeyDone)
                await Task.Delay(100);
        }
        finally { await service2.StopAsync(CancellationToken.None); }

        reloaded.ProviderRekeyDone.Should().BeTrue("повторный проход завершился — маркер на месте");
        new SpendStore(dir, detailDays: 30).DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue)
            .Single().Provider.Should().Be("minimax", "повторный проход дописал диск");
    }

    // Пункт 1 (minor-1), ветвь daily.json: сбой PersistDaily в rekey-проходе по свёрнутому
    // дню — провал прохода (throw), агрегаты на диске остаются прежними до повторного старта.
    [Fact]
    public void Rekey_СбойЗаписиDailyПоСвёрнутомуДню_ПровалПроходаБезПорчиАгрегатов()
    {
        var dir = Path.Combine(_dir, "spend_rekey_daily_fail");
        var store = new SpendStore(dir, detailDays: 30);
        var old = DateTime.UtcNow.AddDays(-40);
        store.Record(new SpendRecord { Id = "rd-f1", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3",
            InputTokens = 100, Timestamp = old });
        store.RollupOlderThan(store.WindowStart);

        // Преграда записи: каталог вместо tmp PersistDaily → она вернёт false → проход провален
        Directory.CreateDirectory(Path.Combine(dir, "daily.json.tmp"));

        Action act = () => store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel);
        act.Should().Throw<IOException>();

        // daily.json на диске не тронут: строка всё ещё со старым провайдером
        var rows = new SpendStore(dir, detailDays: 30).DailyBetween(DateOnly.MinValue, DateOnly.MaxValue);
        rows.Single(r => r.Model == "MiniMax-M3").Provider.Should().Be("claude");
    }

    // Пункт 2 (minor-2): перезапись дня идёт через tmp + File.Move(overwrite) — сбой посреди
    // записи (каталог вместо tmp) не трогает исходный файл: он цел и перечитывается.
    [Fact]
    public void Rekey_СбойПосредиПерезаписиTurns_ИсходныйФайлЦел()
    {
        var dir = Path.Combine(_dir, "spend_rekey_tmp_atomic");
        var store = new SpendStore(dir, detailDays: 30);
        var now = DateTime.UtcNow;
        store.Record(new SpendRecord { Id = "rt-1", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3",
            InputTokens = 100, Timestamp = now });
        var path = Path.Combine(dir, $"turns-{DateOnly.FromDateTime(now):yyyy-MM-dd}.jsonl");
        var original = File.ReadAllText(path);
        original.Should().Contain("\"provider\":\"claude\"");

        Directory.CreateDirectory(path + ".tmp");

        Action act = () => store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, FakeProviderByModel);
        // Запись в путь-каталог на обеих платформах — UnauthorizedAccessException
        act.Should().Throw<UnauthorizedAccessException>();

        // Дневной файл не тронут записью в tmp: тот же состав, строки читаемы
        File.ReadAllText(path).Should().Be(original);
        new SpendStore(dir, detailDays: 30).DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue)
            .Single().Provider.Should().Be("claude");
    }

    // Пункт 3 (minor-4): резолв модели даёт Claude-ключ (IsClaudeKey) — запись не трогается:
    // rekey к другому Claude-ключу не выход из области Claude, а перенумеровывание профиля.
    [Fact]
    public void Rekey_РезолвДаётДругойClaudeКлюч_ЗаписьНеТронута()
    {
        var dir = Path.Combine(_dir, "spend_rekey_claude_key");
        var store = new SpendStore(dir, detailDays: 30);
        var now = DateTime.UtcNow;
        Func<string?, string?> providerByModel = model => model switch
        {
            "MiniMax-M3" => "claude-2",
            "glm-4" => "sub-1",
            _ => null,
        };
        store.Record(new SpendRecord { Id = "ck-1", OwnerId = "u1", Provider = "claude", Model = "MiniMax-M3",
            InputTokens = 100, Timestamp = now });
        store.Record(new SpendRecord { Id = "ck-2", OwnerId = "u1", Provider = "claude", Model = "glm-4",
            InputTokens = 50, Timestamp = now });

        store.RekeyNonClaudeModelsOnClaudeKeys(FakeIsNative, providerByModel).Should().Be(0);

        var all = new SpendStore(dir, detailDays: 30).DetailsBetween(DateOnly.MinValue, DateOnly.MaxValue);
        all.Single(r => r.Id == "ck-1").Provider.Should().Be("claude");
        all.Single(r => r.Id == "ck-2").Provider.Should().Be("claude");
    }
}
