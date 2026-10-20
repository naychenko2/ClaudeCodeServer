using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Services;

// Тесты записи ходов исполнителей фичи в журнал (волна 3 «Обзор фич», Б8c).
// Подписчик FeatureTurnJournalSubscriber по TurnCompleted пишет строку kind=turn, если
// сессия хода привязана к задаче фичи. Платформонезависимые: пути от Path.GetTempPath(),
// ожидание — await PublishAsync (подписчик синхронный, TaskCompletionSource не нужен).
public class FeatureJournalTurnsTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly FeatureJournalStore _journal;
    private readonly TurnEventBus _bus;
    private readonly FeatureTurnJournalSubscriber _subscriber;

    public FeatureJournalTurnsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fjt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();
        _journal = new FeatureJournalStore(Path.Combine(_dir, "feature-journal"));
        _tasks = new TaskManager(config, journal: _journal);
        _bus = new TurnEventBus();
        // Подписчик сам подписывается на шину в конструкторе.
        _subscriber = new FeatureTurnJournalSubscriber(_tasks, _journal, _bus);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Эпик фичи (Role=feature) + дочерняя задача-шаг, привязанная к нему.
    private (TaskItem Epic, TaskItem Step) CreateFeatureAndStep(string owner = "u")
    {
        var epic = _tasks.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var step = _tasks.Create(null, owner, new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "B1", Lane: "бэк"));
        return (epic, step);
    }

    // Паспорт хода с известными полями: success, модель claude-sonnet, 5 секунд.
    private static TurnRunPassport Passport(string sessionId, DateTime start)
    {
        var end = start.AddSeconds(5);
        return new TurnRunPassport(
            SessionId: sessionId,
            StartedAt: start,
            EndedAt: end,
            DurationSeconds: 5,
            Outcome: "success",
            StartModel: "claude-sonnet",
            StartProvider: "anthropic",
            FinalModel: "claude-sonnet",
            FinalProvider: "anthropic",
            Attempts: 1,
            Substitutions: 0,
            EgressRetries: 0,
            Chain: ["claude-sonnet"],
            LastErrorClass: null,
            LastError: null,
            ContextTokens: 100,
            RecordedAt: end);
    }

    // ─── Ход сессии задачи фичи → строка turn с полями паспорта ─────────────────

    [Fact]
    public async Task ХодСессииЗадачиФичи_ПишетTurnСПолямиПаспорта()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Привязываю задачу к сессии (LinkedSessionId) — GetBySession находит по ней.
        _tasks.MarkClaudeStarted(step.Id, "sess-1", now);

        await _bus.PublishAsync(new TurnCompleted(
            new TurnContext("sess-1", "u", 1, 0), "success", null, Passport("sess-1", now)));
        // Запись диска — в фоне (очередь): дожидаемся, пока запись дошла до файла.
        await _subscriber.WaitForDrainAsync();

        var turn = _journal.Read("u", epic.Id).Entries
            .Should().ContainSingle(e => e.Kind == "turn").Which;
        turn.TaskId.Should().Be(step.Id);
        turn.SessionId.Should().Be("sess-1");
        turn.TurnSeq.Should().Be(1);
        turn.StartedAt.Should().Be(now);
        turn.EndedAt.Should().Be(now.AddSeconds(5));
        turn.Outcome.Should().Be("success");
        turn.ErrorClass.Should().BeNull();
        turn.Model.Should().Be("claude-sonnet");
    }

    // ─── Ход чата без задачи → нет записи ─────────────────────────────────────────

    [Fact]
    public async Task ХодЧатаБезЗадачи_НеПишетTurn()
    {
        var (epic, _) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        // Сессия, к которой не привязана ни одна задача (общий оркестраторский/куратор-чат).
        await _bus.PublishAsync(new TurnCompleted(
            new TurnContext("sess-orphan", "u", 1, 0), "success", null, Passport("sess-orphan", now)));
        await _subscriber.WaitForDrainAsync();

        _journal.Read("u", epic.Id).Entries.Should().NotContain(e => e.Kind == "turn",
            "ход чата без привязки к задаче фичи не пишется");
    }

    // ─── Ход задачи без фичи → нет записи ─────────────────────────────────────────

    [Fact]
    public async Task ХодЗадачиБезФичи_НеПишетTurn()
    {
        var now = DateTime.UtcNow;
        // Личная задача без FeatureId/Role — привязана к сессии, но фичи нет.
        var task = _tasks.Create(null, "u", new CreateTaskRequest("личная"));
        _tasks.MarkClaudeStarted(task.Id, "sess-2", now);

        await _bus.PublishAsync(new TurnCompleted(
            new TurnContext("sess-2", "u", 1, 0), "success", null, Passport("sess-2", now)));
        await _subscriber.WaitForDrainAsync();

        // Файла журнала нет (нет ни эпика, ни фичи) → Read пуст.
        _journal.Read("u", "no-feature").Entries.Should().BeEmpty(
            "задача без фичи не создаёт записей журнала");
    }

    // ─── Повтор того же хода → одна строка при чтении (дедуп Б8a) ─────────────────

    [Fact]
    public async Task ПовторТогоЖеХода_ОднаСтрокаПриЧтении()
    {
        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        _tasks.MarkClaudeStarted(step.Id, "sess-1", now);

        // Один и тот же ход (тот же sessionId, turnSeq, startedAt) публикуем дважды.
        var turn = new TurnCompleted(
            new TurnContext("sess-1", "u", 1, 0), "success", null, Passport("sess-1", now));
        await _bus.PublishAsync(turn);
        await _bus.PublishAsync(turn);
        await _subscriber.WaitForDrainAsync();

        _journal.Read("u", epic.Id).Entries
            .Should().ContainSingle(e => e.Kind == "turn",
                "повтор того же хода дедуплицируется при чтении (B8a)");
    }

    // ─── Падение хранилища не роняет подписчика/шину ──────────────────────────────

    [Fact]
    public async Task ПадениеХранилища_НеРоняетПодписчика_ИШину()
    {
        // Отравленное хранилище: dir — файл (CreateDirectory падает), logger бросает,
        // чтобы сбой вырвался из try/catch хранилища в try/catch подписчика.
        var poisonDir = Path.Combine(_dir, "poison");
        File.WriteAllText(poisonDir, "x");
        var poisonStore = new FeatureJournalStore(poisonDir,
            log: new ThrowingLogger<FeatureJournalStore>());
        var poisonBus = new TurnEventBus();
        var poisonSubscriber = new FeatureTurnJournalSubscriber(_tasks, poisonStore, poisonBus);

        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        _tasks.MarkClaudeStarted(step.Id, "sess-1", now);

        // PublishAsync не бросает: сбой подписчика (и хранилища) не роняет шину/ход.
        var ex = await Record.ExceptionAsync(() => poisonBus.PublishAsync(
            new TurnCompleted(new TurnContext("sess-1", "u", 1, 0), "success", null,
                Passport("sess-1", now))));
        ex.Should().BeNull("сбой записи не роняет шину (подписчик Notification)");
        // Фоновая запись (отравленное хранилище) дойдёт до диска: сбой гасится потребителем.
        await poisonSubscriber.WaitForDrainAsync();
    }

    // ─── После старта хоста подписчик на шине (явная IHostedService, не побочный эффект) ──

    [Fact]
    public async Task ПослеСтартаХоста_ПодписчикНаШине_ПишетTurn()
    {
        // Отдельная шина (не _bus теста): DI-подписчик подписывается только на неё,
        // поэтому ход пишется ровно один раз.
        var diBus = new TurnEventBus();
        var services = new ServiceCollection();
        services.AddSingleton(_tasks);
        services.AddSingleton(_journal);
        services.AddSingleton<ITurnEventBus>(diBus);
        // Как AddGatedHostedService: singleton + IHostedService (один экземпляр).
        services.AddSingleton<FeatureTurnJournalSubscriber>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<FeatureTurnJournalSubscriber>());
        var sp = services.BuildServiceProvider();

        // Старт хоста: все IHostedService получают StartAsync — создаётся подписчик
        // (конструктор: подписка на шину + фоновый потребитель).
        foreach (var hs in sp.GetServices<IHostedService>())
            await hs.StartAsync(CancellationToken.None);

        var (epic, step) = CreateFeatureAndStep();
        var now = DateTime.UtcNow;
        _tasks.MarkClaudeStarted(step.Id, "sess-1", now);

        await diBus.PublishAsync(new TurnCompleted(
            new TurnContext("sess-1", "u", 1, 0), "success", null, Passport("sess-1", now)));

        var subscriber = sp.GetRequiredService<FeatureTurnJournalSubscriber>();
        await subscriber.WaitForDrainAsync();

        _journal.Read("u", epic.Id).Entries
            .Should().Contain(e => e.Kind == "turn",
                "после старта хоста подписчик на шине: ход записан в журнал");
    }

    // Logger, который бросает в Log: заставляет сбой хранилища вырваться из его try/catch.
    private sealed class ThrowingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("сбой хранилища (тест)");
    }
}
