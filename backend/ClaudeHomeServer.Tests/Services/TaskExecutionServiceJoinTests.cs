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
//
// Здесь же — дедупликация уведомлений о судьбе задачи (та же точка доставки): о факте завершения
// приходит РОВНО одно уведомление, делегирование лишь меняет его лицо и ссылку.
public class TaskExecutionServiceJoinTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly PersonaManager _personas;
    private readonly NotificationStore _notifStore;
    private readonly TaskExecutionService _sut;
    private readonly UserStore _userStore;

    public TaskExecutionServiceJoinTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_exec_join_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _userStore = userStore;
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var personas = new PersonaManager(config);
        _personas = personas;
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
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
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
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
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

    // ─── (е) волна 6: гонка двух конкурентных ExecuteAsync одной задачи ─────

    // Guard «одна живая сессия на задачу» в ExecuteAsync читает LinkedSessionId ДО того, как
    // CreateAsync/MarkClaudeStarted успеют его выставить — окно в секунды на подъём CLI-процесса.
    // Живая приёмка волны 5: первый вызов tasks_run_executor иногда «падал», координатор
    // ретраил — и раньше второй конкурентный вызов проскакивал мимо гарда в это окно, поднимая
    // ВТОРОГО исполнителя на ту же задачу (следствие — задвоенный доклад TryDeliverCompletionAsync,
    // т.к. оба исполнителя её закрывают). In-memory claim (_launching) закрывает окно на входе
    // метода: второй конкурентный вызов получает явный отказ вместо гонки.
    [Fact]
    public async Task ExecuteAsync_ДваКонкурентныхВызоваОднойЗадачи_ЗапускаетРовноОдногоИсполнителя()
    {
        var user = _userStore.Add("race-owner", "password123", "user");
        var task = _tasks.Create(null, user.Id, new CreateTaskRequest("Гонка запуска исполнителя"));

        var call1 = InvokeExecuteAsyncSafe(task);
        var call2 = InvokeExecuteAsyncSafe(task);
        var results = await Task.WhenAll(call1, call2);

        results.Count(r => r.Ok).Should().Be(1, "исполнитель должен запуститься ровно один раз");
        var failure = results.SingleOrDefault(r => !r.Ok);
        failure.Error.Should().NotBeNull("второй конкурентный вызов обязан получить явный отказ");
        failure.Error.Should().ContainAny("уже запускается", "уже работает сессия");
    }

    private async Task<(bool Ok, string? Error)> InvokeExecuteAsyncSafe(TaskItem task)
    {
        try
        {
            await _sut.ExecuteAsync(task, auto: false);
            return (true, null);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }
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

    // ─── (д) дедупликация: о факте завершения — ровно одно уведомление ──────
    // Постановщик-персона и владелец задачи — всегда один человек (персоны per-owner,
    // обе отправки идут в task.OwnerId), поэтому «Завершил работу над задачей» +
    // «Делегированная задача выполнена» были двумя тостами об одном факте.

    private Persona CreatePersona(string ownerId, string name) =>
        _personas.Create(ownerId, name, role: "Тимлид", description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: false);

    [Fact]
    public async Task TryDeliverCompletionAsync_ДелегированнаяЗадача_ОдноУведомлениеПостановщика()
    {
        // SourceSessionId не задаём: доклад модели Z тут неприменим (ShouldReportToDelegator),
        // проверяем именно состав тостов
        var task = CreateTrackedTask();
        var delegator = CreatePersona(task.OwnerId!, "Постановщик");
        task.CreatedByPersonaId = delegator.Id;
        _tasks.MarkClaudeResult(task.Id, "success");
        task.Status = TaskItemStatus.Done;

        await _sut.TryDeliverCompletionAsync(task);

        var items = await _notifStore.GetListAsync(task.OwnerId!);
        items.Should().ContainSingle("о факте завершения приходит одно уведомление");
        items[0].Title.Should().Be("Делегированная задача выполнена");
        items[0].Tag.Should().Be("Постановщик");
    }

    [Fact]
    public async Task TryDeliverCompletionAsync_ПостановщикСовпадаетСИсполнителем_ОбычноеУведомление()
    {
        // Персона поставила задачу сама себе — делегирования нет, работает обычный тост
        var task = CreateTrackedTask();
        var persona = CreatePersona(task.OwnerId!, "Исполнитель");
        task.CreatedByPersonaId = persona.Id;
        task.PersonaId = persona.Id;
        _tasks.MarkClaudeResult(task.Id, "success");
        task.Status = TaskItemStatus.Done;

        await _sut.TryDeliverCompletionAsync(task);

        var items = await _notifStore.GetListAsync(task.OwnerId!);
        items.Should().ContainSingle();
        items[0].Title.Should().Be("Завершил работу над задачей");
    }

    [Fact]
    public async Task TryDeliverCompletionAsync_ПостановщикУдалён_ФолбэкНаОбычноеУведомление()
    {
        // Персона-постановщик удалена (или чужая) — без фолбэка юзер не узнал бы о завершении
        var task = CreateTrackedTask();
        task.CreatedByPersonaId = Guid.NewGuid().ToString();
        _tasks.MarkClaudeResult(task.Id, "success");
        task.Status = TaskItemStatus.Done;

        await _sut.TryDeliverCompletionAsync(task);

        var items = await _notifStore.GetListAsync(task.OwnerId!);
        items.Should().ContainSingle();
        items[0].Title.Should().Be("Завершил работу над задачей");
    }

    [Fact]
    public async Task OnSessionMessageAsync_ПровалДелегированной_ОдноУведомлениеСоСсылкойНаЗадачу()
    {
        var task = CreateTrackedTask();
        var delegator = CreatePersona(task.OwnerId!, "Постановщик");
        task.CreatedByPersonaId = delegator.Id;
        var session = new Session { Id = "sess-1", OwnerId = task.OwnerId };

        await _sut.OnSessionMessageAsync(session, new ResultMessage("error", DurationMs: 100, NumTurns: 1, Usage: null, TotalCostUsd: null));

        var items = await _notifStore.GetListAsync(task.OwnerId!);
        items.Should().ContainSingle("уведомление о провале тоже одно");
        items[0].Title.Should().Be("Делегированная задача не выполнена");
        items[0].Url.Should().Be(TaskSchedulerService.TaskUrl(_tasks.GetById(task.Id)!),
            "доклада в исходном чате нет — разбираться идём в карточку задачи");
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
