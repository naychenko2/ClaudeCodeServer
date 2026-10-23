using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// W4-HOLD-E (контракт §1, docs/research/feature-lifecycle-shared-chat-cut-2026-10.md :208):
// постановка паузы прерывает ходы задач фичи ФИЗИЧЕСКИ — SetHold лишь помечает feature_held,
// а процесс CLI продолжал жить и доводил ход до result, докладывая постановщику. Механизм —
// существующий «Стоп» (SessionManager.Interrupt). Мутация: убрать вызов
// InterruptFeatureTurnsOnHold в TasksController.SetFeatureHold — первый тест краснеет
// (адаптер не прерван). Убрать исключение ключевой цепочки — краснеет второй тест.
public class TaskExecutionServiceHoldTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly NotificationStore _notifStore;
    private readonly TaskExecutionService _sut;
    private readonly SessionManager _sessions;
    private readonly UserStore _userStore;

    public TaskExecutionServiceHoldTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_exec_hold_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
            })
            .Build();

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        _userStore = userStore;
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: personas);

        var broadcaster = new TestSessionBroadcaster();

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(_notifStore, broadcaster, push, personas, projectManager,
            NullLogger<NotificationService>.Instance);

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);

        var sessions = _sessions = CreateSessionManager(config, projectManager, userStore, appSettings,
            personas, knowledge, notesKb, broadcaster);
        _sut = new TaskExecutionService(_tasks, sessions, personas, broadcaster, push, notif,
            NullLogger<TaskExecutionService>.Instance, config, kb: notesKb);
    }

    public void Dispose()
    {
        Helpers.TestFs.DeleteDirectoryResilient(_dir);
    }

    private static SessionManager CreateSessionManager(IConfiguration config, ProjectManager projectManager,
        UserStore userStore, AppSettingsService appSettings, PersonaManager personas,
        KnowledgeService knowledge, NotesKnowledgeService notesKb, TestSessionBroadcaster broadcaster)
    {
        var llmProviders = new ClaudeHomeServer.Services.Llm.LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new ClaudeHomeServer.Services.Llm.LlmSessionAdapterFactory(
            config, new AgentPromptSourceAdapter(new SkillsService()),
            new WorkspaceDatasetLookup(new WorkspaceKnowledgeStore(config)), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(userStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, new WorkspaceKnowledgeStore(config),
            knowledge, new SkillsService(), userStore, config, NullLogger<PersonaBindingsService>.Instance,
            notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        var historyService = new ChatHistoryService(config);
        return new SessionManager(projectManager, historyService, config, adapters, falCost, usage,
            appSettings, userStore, jwt, server.Object, llmProviders, flags, personas,
            bindings, subPool, NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster: broadcaster);
    }

    // Сессия с «идущим ходом»: подставной адаптер с HasLiveTurn занимает место процесса CLI —
    // ровно так же, как в тестах Interrupt (SessionManagerTests). Возвращает мок для проверки
    // физического прерывания.
    private Mock<ILlmSessionAdapter> SetRunningTurn(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entry = ((System.Collections.IDictionary)field.GetValue(_sessions)!)[sessionId]!;
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        info.Status = SessionStatus.Working;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(info);
        adapter.SetupGet(a => a.HasLiveTurn).Returns(true);
        adapter.SetupGet(a => a.OrchestrationActive).Returns(false);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
        return adapter;
    }

    // Критерий задачи: прерванный ход не доставляет result. Пауза гасит процесс — до result
    // ход не доходит, ни ClaudeResult, ни уведомления о судьбе задачи не появляется.
    [Fact]
    public async Task Hold_PhysicallyInterruptsTaskTurn_NoResultIsDelivered()
    {
        var user = _userStore.Add("hold-exec", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        var adapter = SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, "manual");
        var stopped = _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);

        stopped.Should().Be(1, "идущий ход фичи физически прерван");
        adapter.Verify(a => a.Interrupt(), Times.Once(), "процесс CLI гасится — ход обрывается до result");
        var after = _tasks.GetById(step.Id)!;
        after.ClaudeResult.Should().BeNull("прерванный ход не доходит до result");
        after.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "ход помечён паузой, а не терминальным отказом");
        (await _notifStore.GetListAsync(user.Id)).Should().BeEmpty(
            "ни уведомления, ни доклада постановщику: прерванный ход молчит");
    }

    // Ключевой чат оркестратора не гасится (контракт :200): он общий для нескольких фич, и его
    // ход может быть про другую фичу. Шаг исполняется в том же чате (общий чат) — его ход
    // помечается паузой, но процесс не прерывается. Мутация: убрать guard по keyChain — красное.
    [Fact]
    public async Task Hold_KeyChatOfOrchestrator_NotInterrupted()
    {
        var user = _userStore.Add("hold-key", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича с общим чатом", Role: "feature",
            SourceSessionId: keyChat.Id));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг в ключевом чате", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        _tasks.MarkClaudeStarted(step.Id, keyChat.Id, DateTime.UtcNow);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        var stopped = _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);

        keyAdapter.Verify(a => a.Interrupt(), Times.Never(),
            "ключевой чат оркестратора не прерывается даже если в нём идёт помеченный ход");
        stopped.Should().Be(0, "физически нечего прерывать: единственный ход — в ключевом чате");
        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "пометка остаётся: пауза держится, хотя процесс не прерван");
    }

    // Снятие паузы ничего автоматически не возобновляет (контракт §1, шаг D — отдельный):
    // процесс не поднимается, ход-продолжение не отправляется, пометка feature_held не снимается.
    [Fact]
    public async Task ClearHold_DoesNotAutoResumeAnything()
    {
        var user = _userStore.Add("hold-clear", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        var adapter = SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        _tasks.ClearHold(epic.Id);

        adapter.Verify(a => a.StartAsync(), Times.Never(), "снятие паузы не поднимает процесс заново");
        adapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
            "снятие паузы не отправляет ход-продолжение — возврат шагов это отдельный шаг D");
        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "ClearHold пометку не гасит — иначе задача выглядела бы живой, хотя хода нет");
    }

    // Критерий W4-HOLD-G (контракт §1 :174): tasks_run_executor и автоматика проходят через
    // ExecuteAsync — гейт стоит там, до claim'а и подъёма процесса. Мутация: убрать вызов
    // FeatureHoldRefusal в ExecuteAsync — первый абзац краснеет (запуск проходит). Снятие
    // паузы разрешает запуск — второй абзац ловит обратную сторону гейта.
    [Fact]
    public async Task Hold_RefusesExecutorLaunch_ClearingPauseAllowsStart()
    {
        var user = _userStore.Add("hold-gate", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг фичи", Role: "step",
            FeatureId: epic.Id));
        _tasks.SetHold(epic.Id, "manual");

        var start = async () => await _sut.ExecuteAsync(step, auto: false);
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Фича на паузе, снимите паузу на странице фичи*",
                "запуск исполнителя по удержанной фиче отказан до подъёма процесса");
        step.ClaudeStartedAt.Should().BeNull("отказ не оставляет следов запуска");

        _tasks.ClearHold(epic.Id);
        var started = await _sut.ExecuteAsync(step, auto: false);
        started.ClaudeStartedAt.Should().NotBeNull("снятие паузы разрешает запуск");
    }
}
