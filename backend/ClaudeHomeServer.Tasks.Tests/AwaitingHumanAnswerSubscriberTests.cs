using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Tasks.Journal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeHomeServer.Tests.Services;

// Тесты снятия ожидания человека ответом в чате задачи (FS-E3b).
// Подписчик AwaitingHumanAnswerSubscriber по HumanMessageAccepted (издатель — приём хода
// TurnIntake, FS-E3a) находит задачу по LinkedSessionId и снимает ожидание через
// TaskManager.ClearAwaitingHuman(id, answeredAt). Платформонезависимые: пути от
// Path.GetTempPath(), ожидание — await PublishAsync (подписчик синхронный).
//
// Мутации (что тест проверяет):
// • убрать подписку bus.OnNotification в конструкторе — краснеет «ОтветВЧатеЗадачи»;
// • снять проверку владельца (task.OwnerId != ownerId) — краснеет «ЧужойВладелец»;
// • снять сравнение «AwaitingHumanSince > answeredAt» в перегрузке — краснеет ровно
//   «ОтветСтарше_НовоеОжиданиеНеСнимается» (и «ГраницаОтвета» остаётся зелёным);
// • снять try/catch обработчика — краснеет «ИсключениеTaskManager»: исключение ушло бы
//   в шину (та гасит чужие падения, TurnEventBus.cs:59–68), и собственный логгер
//   подписчика ничего не записал бы → проверка «исключение логируется собой» краснеет;
// • убрать строку AddGatedHostedService из TasksSubsystem.Register — краснеет
//   «TasksSubsystem_Register» (дескриптора IHostedService нет).
public class AwaitingHumanAnswerSubscriberTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly FeatureJournalStore _journal;
    private readonly TurnEventBus _bus;
    private readonly AwaitingHumanAnswerSubscriber _subscriber;

    public AwaitingHumanAnswerSubscriberTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aha_" + Guid.NewGuid().ToString("N"));
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
        _subscriber = new AwaitingHumanAnswerSubscriber(_tasks, _bus);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Шаг фичи, привязанный к сессии и ждущий человека (ожидание поставлено ПОСЛЕ
    // MarkClaudeStarted: старт исполнителя сам снимает ожидание).
    private (TaskItem Epic, TaskItem Step) CreateAwaitingStep(string sessionId, string owner = "u")
    {
        var epic = _tasks.Create(null, owner, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var step = _tasks.Create(null, owner, new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "E1", Lane: "бэк"));
        _tasks.MarkClaudeStarted(step.Id, sessionId, DateTime.UtcNow);
        _tasks.SetAwaitingHuman(step.Id, "ждём ответа");
        return (epic, step);
    }

    private static HumanMessageAccepted Answer(string sessionId, string? owner = "u") =>
        new(new TurnContext(sessionId, owner, 1, 0));

    // ─── Ответ в чате задачи → ожидание снято, в журнале waiting_cleared ────────────

    [Fact]
    public async Task ОтветВЧатеЗадачи_СнимаетОжидание()
    {
        var (epic, step) = CreateAwaitingStep("sess-1");
        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().NotBeNull("исходное состояние: задача ждёт человека");

        await _bus.PublishAsync(Answer("sess-1"));

        var after = _tasks.GetById(step.Id)!;
        after.AwaitingHumanSince.Should().BeNull("ответ человека в привязанном чате снимает ожидание");
        after.AwaitingHumanReason.Should().BeNull("пара снимается целиком");
        // Тот же журнал, что у существующего клира (контракт соседей).
        _journal.Read("u", epic.Id).Entries.Should().Contain(e => e.Kind == "waiting_cleared" && e.TaskId == step.Id,
            "снятие ответом пишет waiting_cleared, как ClearAwaitingHuman(id)");
    }

    // ─── Чат без задачи → ничего не делать ──────────────────────────────────────────

    [Fact]
    public async Task ЧатБезЗадачи_НичегоНеМеняет()
    {
        var (_, step) = CreateAwaitingStep("sess-1");

        // Сессия, к которой не привязана ни одна задача (общий/ключевой/куратор-чат).
        await _bus.PublishAsync(Answer("sess-orphan"));

        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().NotBeNull(
            "чат без привязанной задачи не снимает ожидание другой задачи");
    }

    // ─── Чужой владелец → не трогать даже при совпадении SessionId ──────────────────

    [Fact]
    public async Task ЧужойВладелец_НеТрогает()
    {
        var (_, step) = CreateAwaitingStep("sess-1", owner: "u");

        // Тот же SessionId, другой владелец хода: per-owner ключ — из события.
        await _bus.PublishAsync(Answer("sess-1", owner: "other"));

        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().NotBeNull(
            "задача чужого владельца не снимается чужим ответом");
    }

    // ─── Ожидания нет → ничего не делать (ни мутации, ни журнала) ────────────────────

    [Fact]
    public async Task ОжиданияНет_НичегоНеДелает()
    {
        var epic = _tasks.Create(null, "u", new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        var step = _tasks.Create(null, "u", new CreateTaskRequest("шаг",
            FeatureId: epic.Id, Role: TaskRoles.Step, StepRef: "E1", Lane: "бэк"));
        _tasks.MarkClaudeStarted(step.Id, "sess-1", DateTime.UtcNow);
        // Ожидания нет (старт исполнителя его и снял).

        await _bus.PublishAsync(Answer("sess-1"));

        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().BeNull();
        _journal.Read("u", epic.Id).Entries.Should().NotContain(e => e.Kind == "waiting_cleared",
            "нет ожидания — нет мутации и нет записи журнала");
    }

    // ─── Гонка с report_up: ответ старше ожидания → свежее ожидание не снимается ─────
    // Тот же контракт, что решает подписчик: сравнение Since с временем ответа делает
    // стор под _awaitLock. Здесь — прямой вызов перегрузки: через подписчик момент ответа
    // всегда не раньше момента постановки (отметки времени в событии нет), поэтому гонка
    // проверяется на самом решении.
    // Мутация «снять сравнение AwaitingHumanSince > answeredAt» красит ровно этот тест.

    [Fact]
    public void ОтветСтарше_НовоеОжиданиеНеСнимается()
    {
        var (_, step) = CreateAwaitingStep("sess-1");
        var since = _tasks.GetById(step.Id)!.AwaitingHumanSince!.Value;

        // «Ответ» случился РАНЬШЕ, чем человек снова поставил ожидание (report_up):
        // устаревшее событие его не снимает.
        var cleared = _tasks.ClearAwaitingHuman(step.Id, since.AddSeconds(-10));

        cleared.Should().BeNull("ответ старше ожидания — снятие не по этому ответу");
        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().Be(since, "ожидание, поставленное после ответа, осталось");
    }

    // Граница: ответ ровно в момент постановки (Since == answeredAt) — снимает
    // (условие AwaitingHumanSince <= answeredAt, closed по Since).

    [Fact]
    public void ГраницаОтветаРавнаSince_Снимает()
    {
        var (_, step) = CreateAwaitingStep("sess-1");
        var since = _tasks.GetById(step.Id)!.AwaitingHumanSince!.Value;

        var cleared = _tasks.ClearAwaitingHuman(step.Id, since);

        cleared.Should().NotBeNull("ответ ровно в момент постановки — ожидание снято");
        _tasks.GetById(step.Id)!.AwaitingHumanSince.Should().BeNull();
    }

    // ─── Исключение TaskManager: обработчик ловит его сам и логирует (шина и хост живы) ─
    // Проверяется собственный try/catch, а не только страховка шины: без него исключение
    // ушёл бы в шину (та гасит чужие падения и своим логгером), и собственный логгер
    // подписчика остался бы пустым → краснеет проверка «исключение логируется собой».

    [Fact]
    public async Task ИсключениеTaskManager_ОбработчикНеБросает()
    {
        // Стор с синхронной записью (Tasks:SaveDebounceMs=0): мутация пишет сразу, поэтому
        // «отравленный» путь стора даёт живое исключение прямо из ClearAwaitingHuman.
        var poisonDir = Path.Combine(_dir, "poison");
        var poisonConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(poisonDir, "projects.json"),
                ["Tasks:SaveDebounceMs"] = "0",
            })
            .Build();
        var poisonTasks = new TaskManager(poisonConfig);
        var task = poisonTasks.Create(null, "u", new CreateTaskRequest("шаг"));
        poisonTasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        poisonTasks.SetAwaitingHuman(task.Id, "ждём ответа");

        // Отравить путь стора: каталог стора становится обычным файлом — следующая запись
        // (Directory.CreateDirectory в JsonFileStore.Save) бросает.
        Directory.Delete(poisonDir, recursive: true);
        File.WriteAllText(poisonDir, "x");

        var log = new CapturingLogger<AwaitingHumanAnswerSubscriber>();
        var poisonBus = new TurnEventBus();
        new AwaitingHumanAnswerSubscriber(poisonTasks, poisonBus, log);

        var ex = await Record.ExceptionAsync(() => poisonBus.PublishAsync(Answer("sess-1")));
        ex.Should().BeNull("сбой хранилища не роняет шину и хост");
        log.Failures.Should().NotBeEmpty("исключение гасится и логируется самим обработчиком");

        // Ожидание в памяти снято до неудачной записи — состояние задачи не испорчено.
        poisonTasks.GetById(task.Id)!.AwaitingHumanSince.Should().BeNull();
        poisonTasks.Dispose();
    }

    // Логгер, который собирает записи с исключением: доказательство, что падение
    // обработчик обработал сам (свой catch), а не передал шине.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Failures { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Failures.Add(formatter(state, exception));
        }
    }

    // ─── Реальная регистрация: TasksSubsystem.Register (не ручной AddSingleton) ──────

    [Fact]
    public async Task TasksSubsystem_Register_ПодписчикЗарегистрирован_ИСнимаетОжидание()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                // Гейт AddGatedHostedService: в Testing hosted регистрируется только с этим флагом.
                ["Testing:EnableHostedServices"] = "true",
            })
            .Build();
        services.AddSingleton<IConfiguration>(config);
        // Main-зависимость (в проде — Program.cs): шина ходов.
        services.AddSingleton<ITurnEventBus, TurnEventBus>();
        // Main-зависимость GitWorktreeHeadReader (в проде — Program.cs): фабрика запускателей.
        services.AddSingleton(new Moq.Mock<ILauncherFactory>().Object);
        // Штатная регистрация подсистемы: TaskManager, …, AwaitingHumanAnswerSubscriber
        // (AddGatedHostedService).
        new TasksSubsystem().Register(services, config);

        // Доказательство регистрации (ядро теста): дескриптор IHostedService →
        // AwaitingHumanAnswerSubscriber. Убрать строку AddGatedHostedService — его нет.
        services.Should().Contain(s =>
            s.ServiceType == typeof(IHostedService)
            && s.ImplementationType == typeof(AwaitingHumanAnswerSubscriber),
            "Register регистрирует подписчик через AddGatedHostedService");

        var sp = services.BuildServiceProvider();
        // Зависимый сервис — из РЕАЛЬНОГО контейнера (Register его зарегистрировал).
        var tasks = sp.GetRequiredService<TaskManager>();

        var step = tasks.Create(null, "u", new CreateTaskRequest("шаг"));
        tasks.MarkClaudeStarted(step.Id, "sess-1", DateTime.UtcNow);
        tasks.SetAwaitingHuman(step.Id, "ждём ответа");

        // Собственная шина (не контейнерная): подписчик подписывается на неё, и мы
        // публикуем в неё (доказательство регистрации — дескрипторная проверка выше).
        var bus = new TurnEventBus();
        new AwaitingHumanAnswerSubscriber(tasks, bus);
        await bus.PublishAsync(Answer("sess-1"));

        tasks.GetById(step.Id)!.AwaitingHumanSince.Should().BeNull(
            "реальная регистрация TasksSubsystem.Register: подписчик на шине, ожидание снято");
    }
}
