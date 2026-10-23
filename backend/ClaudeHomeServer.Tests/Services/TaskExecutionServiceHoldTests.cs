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

    // W4-HOLD-D (контракт §1, :231): снятие паузы возвращает отнятое — прерванный шаг
    // получает продолжение в ТЕ ЖЕ сессию исполнителя, а ключевой чат — карточку с числом
    // продолженных. Мутация: убрать отправку продолжения в ResumeFeatureTurnsAfterHold —
    // первый Verify краснеет; убрать снятие пометки — краснеет последний абзац.
    [Fact]
    public async Task ClearHold_ResumesInterruptedTurn_InItsOwnSession()
    {
        var user = _userStore.Add("hold-resume", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича с общим чатом", Role: "feature",
            SourceSessionId: keyChat.Id));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        var execAdapter = SetRunningTurn(chat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(1, "прерванный паузой шаг продолжен");
        execAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжи")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "продолжение идёт в ту же сессию исполнителя, не в новый чат");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжен 1 шаг")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "ключевой чат получает карточку с числом продолженных шагов");
        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().BeNull(
            "пометка feature_held гасится вместе с продолжением: задача снова в работе");
    }

    // R-HOLD-D (п.2, п.3): изоляция сбоев. Первый чат отказывает на доставке — его шаг остаётся
    // помеченным (проектор не рисует «работает», повторное снятие паузы его подхватит), второй
    // продолжается, карточка считает только доставленные. Мутации: вернуть снятие пометки ДО
    // доставки (порядок до правки) — первый абзац краснеет; убрать try/catch — цикл обрывается
    // на первом сбое, второй шаг не продолжен — красное.
    [Fact]
    public async Task ClearHold_ResumesOthers_KeepsMarkWhenDeliveryFails()
    {
        var user = _userStore.Add("hold-fail", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var broken = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг с мёртвым чатом", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var good = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг, который продолжится", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var brokenChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "broken",
            taskId: broken.Id);
        var goodChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "good",
            taskId: good.Id);
        _tasks.MarkClaudeStarted(broken.Id, brokenChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(good.Id, goodChat.Id, DateTime.UtcNow);
        var brokenAdapter = SetRunningTurn(brokenChat.Id);
        brokenAdapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Throws(new InvalidOperationException("чат оборван"));
        var goodAdapter = SetRunningTurn(goodChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(1, "сбой одного чата не обрывает цикл: второй шаг продолжен");
        _tasks.GetById(broken.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "доставка упала — пометка остаётся: шаг не выглядит «в работе» без продолжения");
        _tasks.GetById(good.Id)!.ExecutorStopReason.Should().BeNull(
            "у доставленного продолжения пометка гасится");
        goodAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжи")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "второй шаг получает продолжение, несмотря на сбой первого");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжен 1 шаг")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "счётчик карточки — только доставленные");
    }

    // Чат «в простое»: мок-процесс не присылает exited, поэтому статус idle ставится напрямую —
    // ровно то состояние, в котором чат оказывается после реального конца прерванного хода.
    private void SetIdle(string sessionId)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entry = ((System.Collections.IDictionary)field.GetValue(_sessions)!)[sessionId]!;
        var info = (Session)entry.GetType().GetField("Info")!.GetValue(entry)!;
        info.Status = SessionStatus.Active;
    }

    // W4-HOLD-J (критерий c1, контракт §1 :264-269): чат прерванного паузой исполнителя не
    // живёт свой срок — при постановке паузы срок снимается, при снятии — возвращается с
    // якорем «сейчас». Мутации: убрать SetExpiry(…, null) в InterruptFeatureTurnsOnHold —
    // первый тик уборки краснеет (чат снёсся); убрать возврат срока в ResumeFeatureTurnsAfterHold
    // — краснеет второй абзац (чат пережил восстановленный срок).
    [Fact]
    public async Task Hold_FreezesExecutorChatExpiry_ClearingRestoresIt()
    {
        var user = _userStore.Add("hold-ttl", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10);
        SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);

        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().BeNull(
            "на паузе срок снимается: прерванный чат не должен истечь, пока пауза держится");
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().NotBeNull(
            "час прошёл, срок задачи — 10 минут: замороженный паузой чат жив");

        _tasks.ClearHold(epic.Id);
        await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "снятие паузы возвращает срок задачи");
        SetIdle(chat.Id);
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().BeNull(
            "возвращённый срок течёт от снятия: уборка сносит чат по истечении");
    }

    // W4-HOLD-J (критерий c2, контракт §1 :241): если чат исполнителя за паузу удалён, снятие
    // паузы не пытается продолжать несуществующий ход: пометка feature_held меняется на
    // терминальную feature_held_expired, шаг не считается продолженным. Мутация: удалить
    // expired-ветку — пометка осталась бы feature_held, последний абзац краснеет.
    [Fact]
    public async Task ClearHold_MissingExecutorChat_MarksFeatureHeldExpired()
    {
        var user = _userStore.Add("hold-expired", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг, чат которого исчез", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        var stoppedAt = _tasks.GetById(step.Id)!.ExecutorStoppedAt;
        await _sessions.DeleteAsync(chat.Id); // чата нет — истёк TTL или его удалил человек

        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(0, "продолжения нет: чат исполнителя удалён");
        var after = _tasks.GetById(step.Id)!;
        after.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldExpiredReason,
            "пометка не снимается, а меняется на терминальную: шаг перезапустят штатно");
        after.ExecutorStoppedAt.Should().Be(stoppedAt,
            "момент стопа не двигается: по нему таймлайн паузы scoped'ит interrupted");
    }

    // W4-HOLD-K (дефект bec00347, контракт §1 :102-105, п.4): пустой случай больше не молчит —
    // при снятии manual без прерванных ходов ключевой чат получает карточку «пауза снята,
    // запуски разрешены» с ходом. Мутация: удалить ветку карточки без продолженных — этот
    // Verify краснеет (карточка не ушла бы вовсе, но её текст не совпал бы).
    [Fact]
    public async Task ClearHold_NoInterruptedTurns_SendsPauseClearedCard()
    {
        var user = _userStore.Add("hold-resume-noop", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг", Role: "step",
            FeatureId: epic.Id));
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _tasks.ClearHold(epic.Id);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        resumed.Should().Be(0, "пауза ничего не прерывала — продолжать нечего");
        // R-HOLD-K (п.2): текст содержит указание разобрать доклады, накопленные за паузу
        // (мотив по контракту §1, :249-251). Мутация: вернуть прежнюю формулировку без
        // «разбери доклады» — этот Verify краснеет.
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("запуски разрешены") && t.Contains("разбери доклады, накопленные за паузу")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Once(), "оркестратор будится карточкой «пауза снята, запуски разрешены» с разбором докладов");
        // R-HOLD-K (п.3): ровно одна карточка — в ключевой чат не уходит ничего кроме неё.
        // Мутация: добавить вторую нотификацию в ветке — Times.Once краснеет.
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<string>()),
            Times.Once(), "в ключевой чат уходит ровно одна доставка");
    }

    // W4-HOLD-K (дефект bec00347, контракт §1 :103): снятие черновика — кнопка «Запустить»:
    // в ключевой чат уходит карточка «фича запущена» с ходом, будить куратора — смысл запроса
    // (прерванных ходов у черновика нет по гарду :880). Мутация: удалить ветку draft — Times.Once
    // краснеет: без неё карточка уходит в общей ветке «продолжены N», а её нет.
    [Fact]
    public async Task ClearHold_DraftStartsFeature_SendsStartedCardToKeyChat()
    {
        var user = _userStore.Add("hold-draft-wake", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича в черновике", Role: "feature",
            SourceSessionId: keyChat.Id));
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Draft);
        _tasks.ClearHold(epic.Id);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Draft);
        resumed.Should().Be(0, "у черновика прерванных ходов не бывает");
        // R-HOLD-K (п.2, п.3): текст содержит указание разобрать доклады за паузу; параметры
        // доставки фиксированы, а не It.IsAny: карточка — пробуждение, а не реакция на доклад,
        // поэтому suppressTasksExecute обязан быть false. Мутации: передать suppressTasksExecute:
        // true в DeliverKeyChatCardAsync — краснеет первый Verify; убрать ветку draft — краснеет
        // и первый, и второй (карточка ушла бы в общую ветку).
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("Фича запущена") && t.Contains("разбери доклады, накопленные за паузу")),
            It.IsAny<IReadOnlyList<string>>(), 0, false, It.IsAny<string>()),
            Times.Once(), "ключевой чат получает карточку «фича запущена» с ходом и без подавления действий");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<string>()),
            Times.Once(), "в ключевой чат уходит ровно одна карточка");
    }

    // R-HOLD-D (п.3, критерий c3): согласование числительных со «шаг» — то же правило, что
    // pluralSteps на фронте (FeaturePageHeader.tsx:457): решает последняя цифра, 11 — исключение.
    // Таблица покрывает весь диапазон 5–99. Мутации: вернуть прежнюю inline-форму (до правки
    // согласовала только до 4) — строки 21, 31, … краснеют; убрать исключение 11 — краснеет
    // строка 11.
    [Theory]
    [InlineData(5, "шагов")]
    [InlineData(6, "шагов")]
    [InlineData(7, "шагов")]
    [InlineData(8, "шагов")]
    [InlineData(9, "шагов")]
    [InlineData(10, "шагов")]
    [InlineData(11, "шагов")]
    [InlineData(12, "шагов")]
    [InlineData(13, "шагов")]
    [InlineData(14, "шагов")]
    [InlineData(15, "шагов")]
    [InlineData(16, "шагов")]
    [InlineData(17, "шагов")]
    [InlineData(18, "шагов")]
    [InlineData(19, "шагов")]
    [InlineData(20, "шагов")]
    [InlineData(21, "шаг")]
    [InlineData(22, "шага")]
    [InlineData(23, "шага")]
    [InlineData(24, "шага")]
    [InlineData(25, "шагов")]
    [InlineData(26, "шагов")]
    [InlineData(27, "шагов")]
    [InlineData(28, "шагов")]
    [InlineData(29, "шагов")]
    [InlineData(30, "шагов")]
    [InlineData(31, "шаг")]
    [InlineData(32, "шага")]
    [InlineData(33, "шага")]
    [InlineData(34, "шага")]
    [InlineData(35, "шагов")]
    [InlineData(36, "шагов")]
    [InlineData(37, "шагов")]
    [InlineData(38, "шагов")]
    [InlineData(39, "шагов")]
    [InlineData(40, "шагов")]
    [InlineData(41, "шаг")]
    [InlineData(42, "шага")]
    [InlineData(43, "шага")]
    [InlineData(44, "шага")]
    [InlineData(45, "шагов")]
    [InlineData(46, "шагов")]
    [InlineData(47, "шагов")]
    [InlineData(48, "шагов")]
    [InlineData(49, "шагов")]
    [InlineData(50, "шагов")]
    [InlineData(51, "шаг")]
    [InlineData(52, "шага")]
    [InlineData(53, "шага")]
    [InlineData(54, "шага")]
    [InlineData(55, "шагов")]
    [InlineData(56, "шагов")]
    [InlineData(57, "шагов")]
    [InlineData(58, "шагов")]
    [InlineData(59, "шагов")]
    [InlineData(60, "шагов")]
    [InlineData(61, "шаг")]
    [InlineData(62, "шага")]
    [InlineData(63, "шага")]
    [InlineData(64, "шага")]
    [InlineData(65, "шагов")]
    [InlineData(66, "шагов")]
    [InlineData(67, "шагов")]
    [InlineData(68, "шагов")]
    [InlineData(69, "шагов")]
    [InlineData(70, "шагов")]
    [InlineData(71, "шаг")]
    [InlineData(72, "шага")]
    [InlineData(73, "шага")]
    [InlineData(74, "шага")]
    [InlineData(75, "шагов")]
    [InlineData(76, "шагов")]
    [InlineData(77, "шагов")]
    [InlineData(78, "шагов")]
    [InlineData(79, "шагов")]
    [InlineData(80, "шагов")]
    [InlineData(81, "шаг")]
    [InlineData(82, "шага")]
    [InlineData(83, "шага")]
    [InlineData(84, "шага")]
    [InlineData(85, "шагов")]
    [InlineData(86, "шагов")]
    [InlineData(87, "шагов")]
    [InlineData(88, "шагов")]
    [InlineData(89, "шагов")]
    [InlineData(90, "шагов")]
    [InlineData(91, "шаг")]
    [InlineData(92, "шага")]
    [InlineData(93, "шага")]
    [InlineData(94, "шага")]
    [InlineData(95, "шагов")]
    [InlineData(96, "шагов")]
    [InlineData(97, "шагов")]
    [InlineData(98, "шагов")]
    [InlineData(99, "шагов")]
    public void PluralSteps_RussianForms(int n, string expected)
    {
        TaskExecutionService.PluralSteps(n).Should().Be(expected);
    }

    // Живые сессии SessionManager. Нужны для утверждения «чат задачи не создан вовсе»
    // (R-HOLD-G, находка 4): ClaudeStartedAt == null — не доказательство, ExecuteClaimedAsync
    // создаёт чат раньше MarkClaudeStarted (:341-347), поэтому следы запуска появляются
    // одновременно с чатом, а отказанная задача не должна порождать ни того, ни другого.
    private int LiveSessionCount()
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((System.Collections.IDictionary)field.GetValue(_sessions)!).Count;
    }

    // Критерий W4-HOLD-G (контракт §1 :174): tasks_run_executor и автоматика проходят через
    // ExecuteAsync — гейт стоит там, до claim'а и подъёма процесса. Мутация: убрать вызов
    // FeatureHoldRefusal в ExecuteAsync — первый абзац краснеет (запуск проходит). Перенести
    // гейт за создания сессии — краснеет проверка LiveSessionCount (чат создан до отказа).
    // Снятие паузы разрешает запуск — последний абзац ловит обратную сторону гейта.
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
        LiveSessionCount().Should().Be(0, "отказ до claim'а: чат задачи не создан вовсе");

        // Абзац на queue (W4-HOLD-G, R-HOLD-G находка 1): гейт отказывает по правилу, а не по
        // одной причине — queue та же пауза (FeatureProjector.IsPaused). Мутация: сузить
        // IsPaused до manual → запуск проходит, абзац краснеет.
        _tasks.SetHold(epic.Id, FeatureHoldReasons.Queue);
        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Фича на паузе, снимите паузу на странице фичи*",
                "queue — та же пауза: отказ до claim'а и до подъёма процесса");
        LiveSessionCount().Should().Be(0, "и на queue — чата нет");

        _tasks.ClearHold(epic.Id);
        var started = await _sut.ExecuteAsync(step, auto: false);
        started.ClaudeStartedAt.Should().NotBeNull("снятие паузы разрешает запуск");
    }

    // R-HOLD-G (находка 5): гонка «пауза между гейтом и стартом». Claim уже взят, пауза
    // встала после первого гейта — повторная проверка того же гейта в ExecuteClaimedAsync
    // обязана отказать до первого хода, и чат не создаётся. Мутация: убрать повторную
    // проверку в ExecuteClaimedAsync — чат поднимется, тест покраснеет.
    [Fact]
    public async Task Hold_RecheckAfterClaim_RefusesBeforeFirstTurn()
    {
        var user = _userStore.Add("hold-race", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг фичи", Role: "step",
            FeatureId: epic.Id));
        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);

        // Прямой вызов claimed-пути — ровно то состояние, в которое попадает гонка: claim
        // уже взят, первый гейт пройден, пауза поставлена между ними.
        var claimed = typeof(TaskExecutionService).GetMethod("ExecuteClaimedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Func<Task<TaskItem>> call = async () => await (Task<TaskItem>)
            claimed.Invoke(_sut, new object[] { step, false, null })!;
        await call.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Фича на паузе*", "повторная проверка гейта после claim'а");
        LiveSessionCount().Should().Be(0, "повторный гейт до CreateAsync: чат задачи не создан");
        step.ClaudeStartedAt.Should().BeNull("отказ не оставляет следов запуска");
    }
}
