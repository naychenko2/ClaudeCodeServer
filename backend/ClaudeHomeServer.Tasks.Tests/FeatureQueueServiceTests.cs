using System.Collections.Concurrent;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using ClaudeHomeServer.Services.Tasks.Projector;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

// Тесты живут в сборке вертикали (ADR-014, хвост RV-K-Q6-BOUND-1): FeatureQueueService — в
// ClaudeHomeServer.Tasks, его internal-тестовые швы доступны через InternalsVisibleTo на Tasks.Tests.
namespace ClaudeHomeServer.Tasks.Tests;

// Очередь фич, шаг Q3A (волна 6, разрез Александра 25.10 §4–§7): FeatureQueueService снимает
// голову очереди проекта, пока занято меньше лимита. Инварианты И1, И2, И6, И7, И9, И11.
// Снятие в тестах — записывающая реализация IFeatureHoldReleaser поверх настоящего TaskManager
// (ClearHold): проекция и порядок — настоящие, чужих побочек (чаты, карточки) нет.
public class FeatureQueueServiceTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);

    private readonly string _dir;
    private readonly FeatureJournalStore _journal;
    private readonly TaskManager _tasks;
    private readonly RecordingReleaser _releaser;
    private readonly FeatureQueueService _sut;

    public FeatureQueueServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "feature_queue_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = Config(_dir, limit: null);
        // Журнал фич включён в фикстуру: тесты шва Q3B (R-Q3B-1) читают настоящий hold_cleared,
        // который ClearHold пишет в журнал, а не только состояние Hold.
        _journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        _tasks = new TaskManager(config, journal: _journal);
        _releaser = new RecordingReleaser(_tasks);
        _sut = new FeatureQueueService(_tasks, new FeatureProjector(_tasks), _releaser, config,
            NullLogger<FeatureQueueService>.Instance);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        TestFs.DeleteDirectoryResilient(_dir);
    }

    private static IConfiguration Config(string dir, int? limit) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(dir, "projects.json"),
            [FeatureQueuePolicy.LimitConfigKey] = limit?.ToString(),
        })
        .Build();

    // Фича без удержания и без начатых задач: state not_started — занимает слот.
    private TaskItem Occupant(string owner, string? project) =>
        _tasks.Create(project, owner, new CreateTaskRequest("занимает слот", Role: TaskRoles.Feature));

    // Фича в очереди: Hold { queue } с заданным Since (порядок очереди задаём явно, а не часами).
    private TaskItem Queued(string owner, string? project, int minutes)
    {
        var epic = Occupant(owner, project);
        epic.Hold = new FeatureHold(FeatureHoldReasons.Queue, T0.AddMinutes(minutes), null, true);
        return epic;
    }

    private TaskItem HeldAs(string owner, string? project, string reason, int minutes)
    {
        var epic = Occupant(owner, project);
        epic.Hold = new FeatureHold(reason, T0.AddMinutes(minutes), null, true);
        return epic;
    }

    // Закрытие эпика человеком: state closed — слот свободен. Возвращает закрытую задачу —
    // ровно то, что приходит в событие TaskCompleted.
    private TaskItem Close(TaskItem epic) =>
        _tasks.Update(epic.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done))!;

    private FeatureHold? HoldOf(TaskItem epic) => _tasks.GetById(epic.Id)!.Hold;

    // ── И1: замок на пару (владелец, проект) ────────────────────────────────────────────────

    // Два одновременных события при лимите 1 снимают ровно одну фичу. Тест управляет окном
    // между «посмотрели» и «сделали»: первый вошедший держится внутри критической секции, пока
    // второй не упрётся в занятый замок; без замка второй входит внутрь одновременно с первым.
    // Мутация: убрать SemaphoreSlim (Wait/WaitAsync/Release) — maxInside == 2, тест краснеет.
    [Fact]
    public async Task Rebalance_ДваОдновременныхСобытия_СнимаетсяРовноОдна()
    {
        var running = Occupant("u", "p1");
        var q1 = Queued("u", "p1", 1);
        var q2 = Queued("u", "p1", 2);
        var closed = Close(running);

        // Первый вошедший держится внутри секции, пока второй не упрётся в занятый замок
        // (contended) либо — без замка — не войдёт внутрь сам (secondInside).
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inside = 0;
        var maxInside = 0;
        var entries = 0;
        _sut.Contended = () => contended.TrySetResult();
        _sut.InsideLock = async () =>
        {
            var now = Interlocked.Increment(ref inside);
            int seen;
            do { seen = Volatile.Read(ref maxInside); }
            while (now > seen && Interlocked.CompareExchange(ref maxInside, now, seen) != seen);
            if (now == 2) secondInside.TrySetResult();
            if (Interlocked.Increment(ref entries) == 1)
                await Task.WhenAny(contended.Task, secondInside.Task);
            Interlocked.Decrement(ref inside);
        };

        await Task.WhenAll(
            Task.Run(() => _sut.HandleTaskChangedAsync(closed)),
            Task.Run(() => _sut.HandleTaskChangedAsync(closed))).WaitAsync(Watchdog);

        _releaser.Released.Should().Equal(q1.Id);
        HoldOf(q1).Should().BeNull("голова очереди снята");
        HoldOf(q2)!.Reason.Should().Be(FeatureHoldReasons.Queue, "вторая ждёт: слот снова занят");
        maxInside.Should().Be(1, "критическая секция пары (владелец, проект) — не больше одного входа");
    }

    // ── И2: снимается только queue ──────────────────────────────────────────────────────────

    // Слот свободен, manual-фича старше queue-фичи: снята queue, у manual причина не изменилась;
    // draft тоже не тронут. Мутация: убрать проверку Reason == queue в FeatureQueuePolicy.IsQueued
    // — manual попадает в очередь первой и снимается, тест краснеет.
    [Fact]
    public async Task Rebalance_СлотСвободен_СнимаетТолькоQueue_ManualИDraftНеТрогает()
    {
        var manual = HeldAs("u", "p1", FeatureHoldReasons.Manual, 0);
        var draft = HeldAs("u", "p1", FeatureHoldReasons.Draft, 1);
        var queued = Queued("u", "p1", 2);

        var released = await _sut.RebalanceAsync("u", "p1");

        released.Should().Equal(queued.Id);
        HoldOf(queued).Should().BeNull();
        HoldOf(manual)!.Reason.Should().Be(FeatureHoldReasons.Manual, "manual автоснятие не трогает никогда");
        HoldOf(draft)!.Reason.Should().Be(FeatureHoldReasons.Draft, "draft автоснятие не трогает никогда");
    }

    // Между снимком проекции и снятием человек поставил manual поверх queue: актуальный Hold
    // перечитывается перед снятием. Мутация: убрать перечитывание/проверку Reason == queue в
    // RebalanceLockedAsync — manual снимается по устаревшему снимку, тест краснеет.
    [Fact]
    public async Task Rebalance_HoldСменилсяНаManualМеждуСнимкомИСнятием_НеСнимает()
    {
        var queued = Queued("u", "p1", 0);
        _sut.InsideLock = () =>
        {
            _tasks.SetHold(queued.Id, FeatureHoldReasons.Manual);
            return Task.CompletedTask;
        };

        var released = await _sut.RebalanceAsync("u", "p1");

        released.Should().BeEmpty();
        _releaser.Released.Should().BeEmpty();
        HoldOf(queued)!.Reason.Should().Be(FeatureHoldReasons.Manual, "ручная пауза человека осталась");
    }

    // ── Лимит ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rebalance_СлотЗанят_НичегоНеСнимает()
    {
        Occupant("u", "p1");
        var queued = Queued("u", "p1", 0);

        (await _sut.RebalanceAsync("u", "p1")).Should().BeEmpty();

        HoldOf(queued)!.Reason.Should().Be(FeatureHoldReasons.Queue);
    }

    [Fact]
    public async Task Rebalance_ЛимитДва_СнимаетДвеГоловы()
    {
        var config = Config(_dir, limit: 2);
        var sut = new FeatureQueueService(_tasks, new FeatureProjector(_tasks), _releaser, config,
            NullLogger<FeatureQueueService>.Instance);
        var q1 = Queued("u", "p1", 1);
        var q2 = Queued("u", "p1", 2);
        var q3 = Queued("u", "p1", 3);

        var released = await sut.RebalanceAsync("u", "p1");

        released.Should().Equal(q1.Id, q2.Id);
        HoldOf(q3)!.Reason.Should().Be(FeatureHoldReasons.Queue, "третья ждёт: занято два слота из двух");
    }

    // ── B3 (OOM-разведка): одна проекция на пачку событий ──────────────────────────────────

    // Пачка из N закрытых задач (N > прежнего MaxPasses=64) стоит ОДНУ проекцию, а не N:
    // первый пересчёт снимает всю очередь по одному снимку (собственные снятия считаются
    // локально), остальные события пачки выходят по дешёвой проверке, не строя проекцию.
    // Мутация «вернуть пересчёт в цикл» (ProjectAll на каждом шаге) — первая же задача
    // проецирует N раз, projections != 1, тест краснеет.
    [Fact]
    public async Task Rebalance_ПачкаЗакрытыхЗадач_ПроецируетОдинРаз()
    {
        const int n = 65; // > прежнего потолка проходов MaxPasses: снятие всех N головой за один проход
        var config = Config(_dir, limit: n);
        var sut = new FeatureQueueService(_tasks, new FeatureProjector(_tasks), _releaser, config,
            NullLogger<FeatureQueueService>.Instance);

        var occupants = new List<TaskItem>();
        var queued = new List<TaskItem>();
        for (var i = 0; i < n; i++) occupants.Add(Occupant("u", "p1"));
        for (var i = 0; i < n; i++) queued.Add(Queued("u", "p1", i));
        var closed = occupants.Select(Close).ToList(); // пачка закрытых задач: N × TaskCompleted

        var projector = new FeatureProjector(_tasks);
        var projections = 0;
        sut.ProjectOverride = o => { projections++; return projector.ProjectAll(o); };

        foreach (var t in closed)
            await sut.HandleTaskChangedAsync(t);

        projections.Should().Be(1, "пачка событий = один пересчёт: остальные выходят по дешёвой проверке без проекции");
        _releaser.Released.Should().HaveCount(n, "все N голов очереди сняты по одному снимку");
        queued.All(q => HoldOf(q) is null).Should().BeTrue("вся очередь пачки выпущена");
    }

    // ── Шов Q3B: автоснятие помечается by=queue (R-Q3B-1, MAJOR-1 из RV-Q3B-Q4) ─────────────

    // Единственное место в бою, где автоснятие помечается by=queue, — вызов IFeatureHoldReleaser
    // в FeatureQueueService. Тест на шов: снятие идёт через сервис (как в бою, не прямым
    // ClearHold), проверяются и переданный в шов by, и запись hold_cleared в журнале.
    // Мутация: FeatureHoldBy.Queue → null в вызове ReleaseFeatureHold
    // (backend/ClaudeHomeServer.Tasks/FeatureQueueService.cs:147) — первый Which
    // краснеет: сервис не передал by в шов.
    // Граница покрытия (R-Q3B-3): тест закрепляет только первое звено — сервис → шов. Среднее
    // звено, by в вызове ClearHold у прод-TaskExecutionService
    // (backend/ClaudeHomeServer/Services/TaskExecutionService.cs:1354),
    // здесь не проверяется: RecordingReleaser — тестовая реализация шва, а не прод-релизер,
    // и потеря by в прод-релизере этот тест не краснит. Среднее звено закрепляет
    // TaskExecutionServiceHoldTests.ReleaseFeatureHold_ByQueue_JournalWritesHoldClearedWithBy.
    [Fact]
    public async Task Rebalance_ГоловаОчереди_ЖурналПишетHoldClearedСByQueue()
    {
        var queued = Queued("u", "p1", 0);

        var released = await _sut.RebalanceAsync("u", "p1");

        released.Should().Equal(queued.Id);
        _releaser.Calls.Should().ContainSingle("голова снята ровно один раз")
            .Which.By.Should().Be(FeatureHoldBy.Queue, "автоснятие помечает снятие by=queue");
        var cleared = _journal.Read("u", queued.Id).Entries.Single(e => e.Kind == "hold_cleared");
        cleared.Reason.Should().Be(FeatureHoldReasons.Queue);
        cleared.By.Should().Be(FeatureHoldBy.Queue, "без by лента не покажет «Фича запущена из очереди»");
    }

    // Ручной путь (DELETE hold) зовёт тот же шов без by — default параметра интерфейса, как в
    // TaskExecutionService. Запись в журнале — без by: ручное снятие лента не считает
    // «запущенным из очереди». Мутация: by появился в ручном вызове — тест краснеет.
    [Fact]
    public async Task РучноеСнятие_ТотЖеШов_ЖурналПишетHoldClearedБезBy()
    {
        var queued = Queued("u", "p1", 0);

        var result = await _releaser.ReleaseFeatureHold(queued.Id, "u", _tasks.GetById(queued.Id)!.Hold);

        result.Outcome.Should().Be(FeatureHoldReleaseOutcome.Released);
        var cleared = _journal.Read("u", queued.Id).Entries.Single(e => e.Kind == "hold_cleared");
        cleared.Reason.Should().Be(FeatureHoldReasons.Queue);
        cleared.By.Should().BeNull("ручное снятие поле by не пишет");
    }

    // ── И6: изоляция по владельцу и проекту ─────────────────────────────────────────────────

    // Два владельца × два проекта, слоты свободны везде. Освобождение слота в P1 у A снимает
    // только голову очереди P1 у A; в P2 у A и у B — Hold на месте. Фичи P2 старше P1 —
    // мутация «убрать фильтр по проекту» снимет самую старую, тест краснеет.
    [Fact]
    public async Task Rebalance_ДваВладельцаДваПроекта_СнимаетТолькоГоловуСвоейПары()
    {
        var a2 = Queued("A", "P2", 0);
        var b1 = Queued("B", "P1", 1);
        var b2 = Queued("B", "P2", 2);
        var a1Head = Queued("A", "P1", 3);
        var a1Next = Queued("A", "P1", 4);

        var released = await _sut.RebalanceAsync("A", "P1");

        released.Should().Equal(a1Head.Id);
        HoldOf(a1Next)!.Reason.Should().Be(FeatureHoldReasons.Queue, "вторая в очереди P1 ждёт");
        foreach (var other in new[] { a2, b1, b2 })
            HoldOf(other)!.Reason.Should().Be(FeatureHoldReasons.Queue, "чужая пара не тронута");
    }

    // ── И7: fail-closed ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rebalance_ПроекторБросает_НольСнятийИИсключениеНеВышло()
    {
        var running = Occupant("u", "p1");
        var queued = Queued("u", "p1", 0);
        var closed = Close(running);
        _sut.ProjectOverride = _ => throw new InvalidOperationException("проекция сломана");

        var released = await _sut.RebalanceAsync("u", "p1");
        var act = () => _sut.HandleTaskChangedAsync(closed);

        released.Should().BeEmpty("состояние неизвестно — никого не снимаем");
        await act.Should().NotThrowAsync("обработчик TaskCompleted не должен ронять TaskManager");
        _releaser.Released.Should().BeEmpty();
        HoldOf(queued)!.Reason.Should().Be(FeatureHoldReasons.Queue);
    }

    // Подсистема задач выключена (features == null в DI): сервис очереди не зарегистрирован.
    [Fact]
    public void AddFeatureQueue_ПодсистемаЗадачВыключена_СервисНеЗарегистрирован()
    {
        var disabled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Subsystems:tasks:Enabled"] = "false" })
            .Build();
        var enabled = new ConfigurationBuilder().Build();

        var off = new ServiceCollection().AddFeatureQueue(disabled);
        var on = new ServiceCollection().AddFeatureQueue(enabled);

        off.Any(d => d.ServiceType == typeof(FeatureQueueService)).Should().BeFalse();
        off.Any(d => d.ServiceType == typeof(IFeatureHoldReleaser)).Should().BeFalse();
        on.Any(d => d.ServiceType == typeof(FeatureQueueService)).Should().BeTrue();
    }

    // ── И9: мерж не держит слот ─────────────────────────────────────────────────────────────

    // Фича A принимает финал: state merge_pending, слот освобождается, фича B снята из очереди.
    // Мутация: вернуть MergePending в `_ => true` (OccupiesSlot занимает слот) — B не снимается.
    [Fact]
    public async Task ФиналПринят_СнимаетСледующуюФичу_УAСостояниеMergePending()
    {
        var featureA = Occupant("u", "p1");
        var step = _tasks.Create("p1", "u", new CreateTaskRequest("шаг", Role: TaskRoles.Step,
            FeatureId: featureA.Id));
        var final = _tasks.Create("p1", "u", new CreateTaskRequest("финал", Role: TaskRoles.Final,
            FeatureId: featureA.Id));
        var featureB = Queued("u", "p1", 0);
        var projector = new FeatureProjector(_tasks);
        projector.Project(featureA.Id, "u")!.State.Should().NotBe(FeatureStates.MergePending, "исходно финал не принят");
        (await _sut.RebalanceAsync("u", "p1")).Should().BeEmpty("A занимает слот");

        _tasks.Update(step.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done));
        var done = _tasks.Update(final.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done,
            Verification: new TaskVerification { Verdict = "accepted", VerifiedAt = T0 }))!;
        await _sut.HandleTaskChangedAsync(done);

        projector.Project(featureA.Id, "u")!.State.Should().Be(FeatureStates.MergePending);
        _releaser.Released.Should().Equal(featureB.Id);
        HoldOf(featureB).Should().BeNull();
    }

    // ── И11: сверка в тике ──────────────────────────────────────────────────────────────────

    // Слот свободен, голова в очереди, событий не было: сверка снимает её. Тик планировщика
    // обязан звать сверку: мутация «убрать вызов ReconcileFeatureQueuesAsync из TickAsync» —
    // голова остаётся в очереди, тест краснеет.
    [Fact]
    public async Task Тик_СлотСвободенГоловаВОчереди_СверкаСнимаетБезСобытий()
    {
        var queued = Queued("u", "p1", 0);
        var users = new Mock<IUserStore>();
        users.Setup(u => u.GetAll()).Returns(new List<User> { new() { Id = "u", Username = "u" } });
        var scheduler = new TaskSchedulerService(_tasks, users.Object, new Mock<ISessionBroadcaster>().Object,
            new ReconcilingExecutor(_sut), new Mock<IDailyBriefingRunner>().Object,
            new Mock<IPersonaAutomationRunner>().Object, new Mock<ITaskNotificationDispatcher>().Object,
            NullLogger<TaskSchedulerService>.Instance);

        await scheduler.TickAsync(DateTime.UtcNow);

        _releaser.Released.Should().Equal(queued.Id);
        HoldOf(queued).Should().BeNull();
    }

    [Fact]
    public async Task Сверка_НетФичВОчереди_ПроекцияНеСтроится()
    {
        Occupant("u", "p1");
        _sut.ProjectOverride = _ => throw new InvalidOperationException("проекция не должна строиться");

        var act = () => _sut.ReconcileAsync("u");

        await act.Should().NotThrowAsync();
        _releaser.Released.Should().BeEmpty();
    }

    // ── Подписки ────────────────────────────────────────────────────────────────────────────

    // Подписка на TaskCompleted встаёт в StartAsync: закрытие эпика само снимает голову очереди
    // (событие приходит синхронно из Update, работа уходит в фон — ждём факт снятия, не время).
    [Fact]
    public async Task StartAsync_ЗакрытиеЭпика_СамоСнимаетГолову()
    {
        var running = Occupant("u", "p1");
        var queued = Queued("u", "p1", 0);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _releaser.OnRelease = () => released.TrySetResult();

        await _sut.StartAsync(CancellationToken.None);
        try
        {
            Close(running);
            await released.Task.WaitAsync(Watchdog);
        }
        finally
        {
            await _sut.StopAsync(CancellationToken.None);
        }

        HoldOf(queued).Should().BeNull("событие закрытия эпика сняло голову");
    }

    // Записывающая реализация шва снятия: тот же ClearHold, что у настоящего пути, без чатов.
    private sealed class RecordingReleaser(TaskManager tasks) : IFeatureHoldReleaser
    {
        public sealed record Call(string FeatureId, string? By);

        public ConcurrentQueue<string> Released { get; } = new();
        // Что реально передано в шов, включая by: тесты шва Q3B утверждают по этому.
        public ConcurrentQueue<Call> Calls { get; } = new();
        public Action? OnRelease { get; set; }

        public Task<FeatureHoldRelease> ReleaseFeatureHold(string featureId, string ownerId, FeatureHold? hold, string? by = null)
        {
            Calls.Enqueue(new Call(featureId, by));
            var epic = tasks.ClearHold(featureId, by);
            if (epic is null)
                return Task.FromResult(new FeatureHoldRelease(FeatureHoldReleaseOutcome.NotHeld, [], []));
            Released.Enqueue(featureId);
            OnRelease?.Invoke();
            return Task.FromResult(new FeatureHoldRelease(FeatureHoldReleaseOutcome.Released, [], []));
        }
    }

    // Исполнитель-заглушка шва ITaskExecutor: сверку отдаёт сервису очереди, как адаптер в проде.
    private sealed class ReconcilingExecutor(FeatureQueueService queue) : ITaskExecutor
    {
        public Task ExecuteAsync(TaskItem task, bool auto) => Task.CompletedTask;
        public Task CheckStalledExecutorAsync(TaskItem task, DateTime nowUtc) => Task.CompletedTask;
        public Task ReconcileFeatureQueuesAsync(string ownerId) => queue.ReconcileAsync(ownerId);
    }
}
