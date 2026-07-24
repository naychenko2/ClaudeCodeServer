using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// CT-8: доклад делегированной задачи (L0-тост + модель Z) должен доставляться РОВНО один раз,
// в любом порядке прихода двух независимых сигналов — R (конец хода, ClaudeResult) и D
// (Status=Done, из tasks_complete/PUT/UI). До фикса единственной точкой была ветка ResultMessage,
// проверяющая Status==Done в момент R — если D ещё не долетел, доклад терялся навсегда.
//
// Сценарии здесь без персоны-исполнителя/постановщика: TryDeliverCompletionAsync с такой задачей
// ограничивается L0-уведомлением через NotificationService (файловый сторадж) — ReportToDelegatorAsync
// и NotifyDelegatorAsync короткозамыкаются до обращения к SessionManager. Полный пайплайн модели Z
// (реальный ход постановщика) требует claude.exe и здесь, как и в TaskExecutionServiceTests, не гоняется.
public class TaskExecutionServiceJoinTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly NotificationStore _notifStore;
    private readonly TaskExecutionService _sut;

    public TaskExecutionServiceJoinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_exec_join_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: personas);

        var hub = new Mock<IHubContext<SessionHub>>();
        var clients = new Mock<IHubClients>();
        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(_notifStore, hub.Object, push, personas, projectManager, NullLogger<NotificationService>.Instance);

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);

        var sessions = CreateSessionManager(config, projectManager, userStore, appSettings, personas, knowledge, notesKb, hub);

        _sut = new TaskExecutionService(_tasks, sessions, personas, hub.Object, push, notesKb, notif,
            NullLogger<TaskExecutionService>.Instance, config);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Построение SessionManager нужно только затем, что конструктор TaskExecutionService
    // подписывается на его событие (sessions.OnSessionMessage +=) — без живого экземпляра
    // упадёт с NRE. Сами сценарии ниже (без персоны-делегата) до SessionManager не достают.
    private static SessionManager CreateSessionManager(IConfiguration config, ProjectManager projectManager,
        UserStore userStore, AppSettingsService appSettings, PersonaManager personas,
        KnowledgeService knowledge, NotesKnowledgeService notesKb, Mock<IHubContext<SessionHub>> hub)
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new SkillsService(), new WorkspaceKnowledgeStore(config), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(userStore);
        var personaMemory = new PersonaMemoryService(knowledge, personas, userStore, config, NullLogger<PersonaMemoryService>.Instance);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, new WorkspaceKnowledgeStore(config), notesSvc, notesKb,
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var historyService = new ChatHistoryService(config);
        return new SessionManager(projectManager, hub.Object, historyService, config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, notesKb, flags, personas, personaMemory,
            bindings, promptBuilder, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox);
    }

    private TaskItem CreateTrackedTask(string ownerId = "user-1")
    {
        var task = _tasks.Create(null, ownerId, new CreateTaskRequest("Проверка join-а"));
        _tasks.MarkClaudeStarted(task.Id, "sess-1", DateTime.UtcNow);
        return _tasks.GetById(task.Id)!;
    }

    private async Task<int> CountNotificationsAsync(string ownerId) =>
        (await _notifStore.GetListAsync(ownerId)).Count;

    // ─── (а) порядок R → D ────────────────────────────────────────────────────

    [Fact]
    public async Task TryDeliverCompletionAsync_RПотомD_ДоставляетТолькоПослеD()
    {
        var task = CreateTrackedTask();

        // R: ход завершился успешно
        _tasks.MarkClaudeResult(task.Id, "success");
        await _sut.TryDeliverCompletionAsync(_tasks.GetById(task.Id)!);

        _tasks.GetById(task.Id)!.CompletionDelivered.Should().BeFalse("D ещё не пришёл — доставка не должна произойти");
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(0);

        // D: tasks_complete перевёл статус в Done (напрямую полем — событийный путь
        // TaskManager.TaskCompleted проверяется отдельно в TaskManagerTests)
        task.Status = TaskItemStatus.Done;
        await _sut.TryDeliverCompletionAsync(task);

        task.CompletionDelivered.Should().BeTrue();
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(1);
    }

    // ─── (б) порядок D → R ────────────────────────────────────────────────────

    [Fact]
    public async Task TryDeliverCompletionAsync_DПотомR_ДоставляетТолькоПослеR()
    {
        var task = CreateTrackedTask();

        // D: статус переведён в Done раньше, чем пришёл result хода
        task.Status = TaskItemStatus.Done;
        await _sut.TryDeliverCompletionAsync(task);

        task.CompletionDelivered.Should().BeFalse("R ещё не пришёл — доставка не должна произойти");
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(0);

        // R: ход завершился успешно
        _tasks.MarkClaudeResult(task.Id, "success");
        await _sut.TryDeliverCompletionAsync(_tasks.GetById(task.Id)!);

        _tasks.GetById(task.Id)!.CompletionDelivered.Should().BeTrue();
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(1);
    }

    // ─── (в) идемпотентность: двойной триггер → один доклад ─────────────────

    [Fact]
    public async Task TryDeliverCompletionAsync_ДвойнойТриггерПриОбоихСигналах_ОдинДоклад()
    {
        var task = CreateTrackedTask();
        _tasks.MarkClaudeResult(task.Id, "success");
        task.Status = TaskItemStatus.Done;
        task = _tasks.GetById(task.Id)!;

        // Оба сигнала уже на месте — гонка R и D сходится к двум почти одновременным
        // вызовам одного и того же join-метода
        await Task.WhenAll(
            _sut.TryDeliverCompletionAsync(task),
            _sut.TryDeliverCompletionAsync(task));

        task.CompletionDelivered.Should().BeTrue();
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(1, "CAS должен пропустить второй вызов");
    }

    [Fact]
    public async Task TryDeliverCompletionAsync_ПовторныйВызовПослеДоставки_НеШлётПовторно()
    {
        var task = CreateTrackedTask();
        _tasks.MarkClaudeResult(task.Id, "success");
        task.Status = TaskItemStatus.Done;

        await _sut.TryDeliverCompletionAsync(task);
        await _sut.TryDeliverCompletionAsync(task); // напр. повторный ResultMessage/PUT

        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(1);
    }

    // ─── (г) провал хода: L0 «не выполнена», доклад Z не шлётся ─────────────

    [Fact]
    public async Task OnSessionMessageAsync_ПровалХода_ШлётL0НоНеЗапускаетJoin()
    {
        var task = CreateTrackedTask();
        var session = new Session { Id = "sess-1", OwnerId = task.OwnerId };

        await _sut.OnSessionMessageAsync(session, new ResultMessage("error", DurationMs: 100, NumTurns: 1, Usage: null, TotalCostUsd: null));

        var updated = _tasks.GetById(task.Id)!;
        updated.ClaudeResult.Should().Be("error");
        updated.CompletionDelivered.Should().BeFalse("провал хода не должен запускать join/доклад Z");

        var items = await _notifStore.GetListAsync(task.OwnerId!);
        items.Should().ContainSingle();
        items[0].Title.Should().Be("Не смог выполнить задачу");
    }

    [Fact]
    public async Task OnSessionMessageAsync_УспешныйХодНоЕщёНеDone_НичегоНеШлёт()
    {
        // Промежуточный успешный ход многошаговой задачи — Status ещё InProgress,
        // D не пришёл: join гейтит доставку, спама «завершил работу» быть не должно
        var task = CreateTrackedTask();
        var session = new Session { Id = "sess-1", OwnerId = task.OwnerId };

        await _sut.OnSessionMessageAsync(session, new ResultMessage("success", DurationMs: 100, NumTurns: 1, Usage: null, TotalCostUsd: null));

        _tasks.GetById(task.Id)!.CompletionDelivered.Should().BeFalse();
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(0);
    }

    [Fact]
    public async Task OnSessionMessageAsync_УспешныйХодИУжеDone_ДоставляетСразу()
    {
        // D пришёл раньше R (tasks_complete отработал внутри хода до его физического конца).
        // Статус выставлен напрямую полем — TaskManager.Update поднял бы TaskCompleted и
        // спровоцировал фоновую доставку (Task.Run) параллельно этому тесту, что сделало бы
        // проверку count==1 недетерминированной (событийный путь — отдельно в TaskManagerTests).
        var task = CreateTrackedTask();
        task.Status = TaskItemStatus.Done;
        var session = new Session { Id = "sess-1", OwnerId = task.OwnerId };

        await _sut.OnSessionMessageAsync(session, new ResultMessage("success", DurationMs: 100, NumTurns: 1, Usage: null, TotalCostUsd: null));

        _tasks.GetById(task.Id)!.CompletionDelivered.Should().BeTrue();
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(1);
    }
}
