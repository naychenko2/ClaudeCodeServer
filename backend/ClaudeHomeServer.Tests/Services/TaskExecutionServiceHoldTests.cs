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
using Microsoft.Extensions.Logging;
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
    // Части конструктора _sut: тест c2 собирает TaskExecutionService с моком broadcaster,
    // остальные зависимости те же.
    private readonly IConfiguration _config;
    private readonly PersonaManager _personas;
    private readonly PushService _push;
    private readonly NotificationService _notif;
    private readonly NotesKnowledgeService _notesKb;

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
        _config = config;

        var userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        _userStore = userStore;
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, userStore, appSettings);
        var personas = new PersonaManager(config);
        _personas = personas;
        _tasks = new TaskManager(config, personas: personas);

        var broadcaster = new TestSessionBroadcaster();

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _push = push;
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(_notifStore, broadcaster, push, personas, projectManager,
            NullLogger<NotificationService>.Instance);
        _notif = notif;

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);
        _notesKb = notesKb;

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
        // Реальный порядок ручки (TasksController.ClearFeatureHold): сначала ClearHold,
        // затем фоновое продолжение — W4-HOLD-N добавил в Resume проверку текущего Hold.
        _tasks.ClearHold(epic.Id);

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
        // Реальный порядок ручки (TasksController.ClearFeatureHold): сначала ClearHold,
        // затем фоновое продолжение — W4-HOLD-N добавил в Resume проверку текущего Hold.
        _tasks.ClearHold(epic.Id);

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

    // Чат «после конца хода»: в SessionStatus нет Idle — реальное состояние завершённого
    // чата — Active (W4-HOLD-N, хвост R-HOLD-J: прежний комментарий говорил «idle», кода
    // с таким статусом нет). Мок-процесс не присылает exited, поэтому статус ставится
    // напрямую — ровно то состояние, в котором чат оказывается после реального конца
    // прерванного хода.
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
        var keyAdapter = SetRunningTurn(keyChat.Id);

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
        // R-HOLD-J (п.3, критерий c3): карточка различает expired-шаги — не «прерванных
        // шагов нет», а «1 шаг не продолжен — чат исполнителя исчез, его нужно перезапустить».
        // Мутация: вернуть прежнюю формулировку «прерванных шагов нет» — этот Verify краснеет.
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("1 шаг не продолжен") && t.Contains("перезапустить")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Once(), "оркестратор получает карточку с expired-шагом, а не «прерванных нет»");
    }

    // R-HOLD-J (п.1, критерий c1): срок возвращается только после успешной доставки
    // продолжения: при сбое хода чат остаётся замороженным — пауза держится, контекст
    // не теряется. Мутации: вернуть SetExpiry ДО SendMessageAsync (порядок до правки) —
    // первый абзац краснеет (срок вернулся бы, хотя продолжение не доставлено);
    // убрать возврат срока вовсе — краснеет второй абзац (чат не пережил бы уборку).
    [Fact]
    public async Task Hold_FailedDeliveryKeepsChatFrozen()
    {
        var user = _userStore.Add("hold-ttl-fail", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг с боем доставки", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10);
        var adapter = SetRunningTurn(chat.Id);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Throws(new InvalidOperationException("чат оборван"));

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);

        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(0, "доставка упала — шаг не продолжен");
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().BeNull(
            "сбой продолжения: срок остаётся снятым — пауза держится, чат не должен истечь");
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().NotBeNull("замороженный чат жив, пока шаг не продолжен");
    }

    // R-HOLD-J (п.2, критерий c2): сбой рассылки в expired-ветке не обрывает цикл: пометка
    // сменена, живой шаг продолжен, карточка оркестратору уходит. Мутация: убрать try/catch
    // в expired-ветке — исключение из ToOwner выбивает из метода: второй шаг не продолжен,
    // карточки нет — первые два абзаца краснеют.
    [Fact]
    public async Task ClearHold_BroadcastFailureInExpiredBranch_DoesNotBreakLoop()
    {
        var user = _userStore.Add("hold-expired-broadcast", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var gone = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг без чата", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var live = _tasks.Create(null, user.Id, new CreateTaskRequest("Живой шаг", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var goneChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "gone",
            taskId: gone.Id);
        var liveChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "live",
            taskId: live.Id);
        _tasks.MarkClaudeStarted(gone.Id, goneChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(live.Id, liveChat.Id, DateTime.UtcNow);
        SetRunningTurn(goneChat.Id);
        var liveAdapter = SetRunningTurn(liveChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        await _sessions.DeleteAsync(goneChat.Id); // чат исчез за паузу

        // Рассылка отказывает — именно она раньше обрывала цикл в expired-ветке.
        var failing = new Mock<ISessionBroadcaster>();
        failing.Setup(b => b.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Throws(new InvalidOperationException("рассылка недоступна"));
        var sut = new TaskExecutionService(_tasks, _sessions, _personas, failing.Object, _push, _notif,
            NullLogger<TaskExecutionService>.Instance, _config, kb: _notesKb);

        _tasks.ClearHold(epic.Id);
        var resumed = await sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        _tasks.GetById(gone.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldExpiredReason,
            "сбой рассылки не отменяет смену пометки: шаг помечен терминально");
        liveAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжи")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "второй шаг продолжается, несмотря на сбой рассылки в первом");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжен 1 шаг") && t.Contains("1 шаг не продолжен")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Once(), "карточка уходит: продолжен один, один — expired");
        resumed.Should().Be(1, "сбой рассылки не отнимает возврат живого шага");
    }

    // R-HOLD-J2 (п.1, критерий c1): смешанный случай с разными счётчиками — «1+1» их не
    // различает (оба равны 1), здесь два живых шага и один expired: текст карточки сверяется
    // дословно — «продолжены 2 шага» и «1 шаг не продолжен». Мутация: переставить счётчики
    // (resumed++ в expired-ветке, expired++ в живой) — оба Verify краснеют: карточка
    // строится из фактических resumed/expired.
    [Fact]
    public async Task ClearHold_MixedTwoLiveOneExpired_CountsSeparately()
    {
        var user = _userStore.Add("hold-mixed-2-1", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var live1 = _tasks.Create(null, user.Id, new CreateTaskRequest("Живой шаг 1", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var live2 = _tasks.Create(null, user.Id, new CreateTaskRequest("Живой шаг 2", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var gone = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг без чата", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var c1 = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec1", taskId: live1.Id);
        var c2 = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec2", taskId: live2.Id);
        var goneChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "gone",
            taskId: gone.Id);
        _tasks.MarkClaudeStarted(live1.Id, c1.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(live2.Id, c2.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(gone.Id, goneChat.Id, DateTime.UtcNow);
        var a1 = SetRunningTurn(c1.Id);
        var a2 = SetRunningTurn(c2.Id);
        SetRunningTurn(goneChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        await _sessions.DeleteAsync(goneChat.Id); // чат исчез за паузу

        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(2, "оба живых шага продолжены");
        a1.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжи")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "первый живой шаг продолжается");
        a2.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжи")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "второй живой шаг продолжается");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжены 2 шага") && t.Contains("1 шаг не продолжен")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Once(), "карточка считает живых и expired отдельно: 2 продолжено, 1 — нет");
    }

    // R-HOLD-J2 (п.2, критерий c2): expired считается только при фактической смене пометки.
    // Если задачу удалили между снимком и вызовом (человек удалил, пока цикл продолжал
    // живой шаг), MarkExecutorStopExpired возвращает null — карточка не должна обещать
    // перезапуск несуществующего шага. Мутация: вернуть expired++ вне if (как было) —
    // последний Verify краснеет: в карточке появился бы «1 шаг не продолжен».
    [Fact]
    public async Task ClearHold_TaskDeletedDuringResumeLoop_NoPhantomRestartPromised()
    {
        var user = _userStore.Add("hold-deleted-during-loop", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var live = _tasks.Create(null, user.Id, new CreateTaskRequest("Живой шаг", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var gone = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг без чата", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var liveChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "live",
            taskId: live.Id);
        var goneChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "gone",
            taskId: gone.Id);
        _tasks.MarkClaudeStarted(live.Id, liveChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(gone.Id, goneChat.Id, DateTime.UtcNow);
        SetRunningTurn(liveChat.Id);
        SetRunningTurn(goneChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        await _sessions.DeleteAsync(goneChat.Id); // чат исчез за паузу

        // Человек удаляет expired-задачу в момент, когда цикл продолжает живой шаг:
        // удаление — в callback рассылки о первом продолженном шаге.
        var deleting = new Mock<ISessionBroadcaster>();
        deleting.Setup(b => b.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Returns(() =>
            {
                _tasks.Delete(gone.Id);
                return Task.CompletedTask;
            });
        var sut = new TaskExecutionService(_tasks, _sessions, _personas, deleting.Object, _push, _notif,
            NullLogger<TaskExecutionService>.Instance, _config, kb: _notesKb);

        _tasks.ClearHold(epic.Id);
        var resumed = await sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(1, "живой шаг продолжен");
        _tasks.GetById(gone.Id).Should().BeNull("задача удалена человеком посреди цикла");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжен 1 шаг") && !t.Contains("не продолжен")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Once(), "карточка не обещает перезапуск удалённой задачи");
    }

    // R-HOLD-J (п.5, критерий c3): терминальная причина имеет человеческий текст, а не
    // общую формулировку. Мутация: удалить строку в ExecutorStopText — красное.
    [Fact]
    public void ExecutorStopText_FeatureHeldExpired_SvojaFormulirovka()
    {
        TaskExecutionService.ExecutorStopText(ExecutorStopClassifier.FeatureHeldExpiredReason)
            .Should().Contain("перезапустить");
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

    // Очередь фич (волна 6): снятие queue — карточка «Фича запущена из очереди» с ходом и без
    // подавления действий. Мутация: удалить ветку Queue — карточка уйдёт в общей ветке
    // «Пауза снята: прерванных шагов нет», и Verify по тексту краснеет.
    [Fact]
    public async Task ClearHold_QueueStartsFeature_SendsQueueCardToKeyChat()
    {
        var user = _userStore.Add("hold-queue-wake", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича в очереди", Role: "feature",
            SourceSessionId: keyChat.Id));
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Queue);
        _tasks.ClearHold(epic.Id);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Queue);
        resumed.Should().Be(0);
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("Фича запущена из очереди") && t.Contains("разбери доклады")),
            It.IsAny<IReadOnlyList<string>>(), 0, false, It.IsAny<string>()),
            Times.Once(), "оркестратор будится карточкой из очереди, действия не подавлены");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(),
            It.IsAny<string>()),
            Times.Once(), "в ключевой чат уходит ровно одна карточка");
    }

    // Очередь фич (Q4): фича может стоять в очереди дольше суток. Срок чата прерванного шага
    // (10 мин) на удержании queue снят, через 25 ч уборка чат не сносит, а снятие удержания
    // продолжает шаг (не feature_held_expired) и возвращает срок с якорем «сейчас».
    // Мутация: убрать FreezeExpiryForHold в InterruptFeatureTurnsOnHold — тик на 25 ч сносит
    // чат, первый и последующие абзацы краснеют.
    [Fact]
    public async Task QueueHold_25Hours_ClearingContinuesStepNotExpired()
    {
        var user = _userStore.Add("hold-queue-ttl", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича в очереди", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг в очереди", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec", taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10);
        SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Queue);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);

        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().BeNull("на удержании queue срок снят");
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddHours(25));
        _sessions.GetById(chat.Id).Should().NotBeNull("25 ч в очереди: замороженный чат жив");

        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Queue);

        resumed.Should().Be(1, "шаг продолжен в прежнем чате");
        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().NotBe(ExecutorStopClassifier.FeatureHeldExpiredReason);
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10, "срок возвращён после снятия");
    }

    // M1 (хвост RV-Q2): признак «фича удержана» — наличие Hold, а не его Reason. Битый tasks.json
    // даёт Hold с пустой причиной; прежний вывод признака из Reason отвечал NotHeld, и паузу
    // нельзя было снять. Мутация: вернуть `if (hold?.Reason is null)` в ReleaseFeatureHold —
    // исход NotHeld, Hold остаётся, тест краснеет.
    [Fact]
    public async Task ReleaseFeatureHold_HoldСПустойПричиной_Снимается()
    {
        var user = _userStore.Add("hold-null-reason", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича с битым Hold", Role: "feature"));
        epic.Hold = new FeatureHold(null!, DateTime.UtcNow, null, true);

        var release = await _sut.ReleaseFeatureHold(epic.Id, user.Id, epic.Hold);

        release.Outcome.Should().Be(FeatureHoldReleaseOutcome.Released);
        _tasks.GetById(epic.Id)!.Hold.Should().BeNull("битую паузу можно снять кнопкой");
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
            claimed.Invoke(_sut, new object?[] { step, false, null })!;
        await call.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Фича на паузе*", "повторная проверка гейта после claim'а");
        LiveSessionCount().Should().Be(0, "повторный гейт до CreateAsync: чат задачи не создан");
        step.ClaudeStartedAt.Should().BeNull("отказ не оставляет следов запуска");
    }

    // ─── W4-HOLD-N: хвосты R-HOLD-D2 / R-HOLD-J ───────────────────────────────────────────

    // Лог-сборник: cause доставки наблюдаем только в строке «Доставка хода» — иначе она
    // нигде не проявляется.
    private sealed class CapturingLogger(List<string> sink) : ILogger<SessionManager>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Add(formatter(state, exception));
    }

    // W4-HOLD-N (хвост R-N, критерий c1): честная причина: продолжение в чат шага —
    // HoldResume (ход был прерван паузой), карточка оркестратору — HoldCard: она сама
    // нотификация снятия, а не продолжение (у черновика прерванных ходов не бывает).
    // Мутации: вернуть cause HoldResume в DeliverKeyChatCardAsync — последний Contain
    // краснеет; сменить причину продолжения — краснеет первый.
    [Fact]
    public async Task ClearHold_CauseIsHoldCardForCardAndHoldResumeForContinuation()
    {
        var user = _userStore.Add("hold-cause", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        SetRunningTurn(chat.Id);
        SetRunningTurn(keyChat.Id);

        var logLines = new List<string>();
        typeof(SessionManager).GetField("_log", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(_sessions, new CapturingLogger(logLines));

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        resumed.Should().Be(1, "шаг продолжен: атрибутировать есть что");
        logLines.Should().Contain(
            l => l.Contains("Доставка хода") && l.Contains("cause=HoldResume") && l.Contains("Ход был прерван"),
            "продолжение прерванного хода атрибутируется как HoldResume");
        logLines.Should().Contain(
            l => l.Contains("Доставка хода") && l.Contains("cause=HoldCard") && l.Contains("продолжен 1 шаг"),
            "карточка — нотификация снятия паузы, а не продолжение прерванного хода");
    }

    // W4-HOLD-N (хвост R-HOLD-J п.1, критерий c3): гонка фонового продолжения — пауза,
    // поставленная сразу после снятия, не должна пустить шаги: текущий Hold проверяется
    // в самом ResumeFeatureTurnsAfterHold. Мутация: убрать guard по epic.Hold — продолжение
    // уйдёт в чат и карточка уйдёт в ключевой: оба Verify краснеют.
    [Fact]
    public async Task Hold_NewPauseRightAfterClear_DoesNotResume()
    {
        var user = _userStore.Add("hold-race-resume", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        var execAdapter = SetRunningTurn(chat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        _tasks.ClearHold(epic.Id);
        // Новая пауза встала сразу после снятия — фоновое продолжение не должно пройти.
        _tasks.SetHold(epic.Id, FeatureHoldReasons.Queue);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        resumed.Should().Be(0, "новая пауза держится: продолжение не отправляется");
        execAdapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
            "шаг не продолжается, пока держится новая пауза");
        keyAdapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never(),
            "карточка «пауза снята» не уходит фиче под новой паузой");
    }

    // W4-HOLD-N (хвост R-HOLD-J п.3, критерий c2): возвращается сохранённый при заморозке
    // срок самого чата, а не TTL задачи: при TTL задачи null временный чат не становится
    // вечным. Мутация: вернуть ветку «if (t.ExecutionExpiresAfterMinutes is { } ttl)
    // SetExpiry» — при отсутствии TTL задачи срок не вернулся бы: последний абзац краснеет.
    [Fact]
    public async Task ClearHold_RestoresChatOwnExpiry_NotTaskTtl()
    {
        var user = _userStore.Add("hold-own-ttl", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        // Отрицательное — бессрочно (TaskManager.cs:381): задача без TTL, срок только у чата.
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг без TTL", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: -1));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10); // срок самого чата — задачи без срока
        SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().NotBeNull("на паузе чат не истекает");

        _tasks.ClearHold(epic.Id);
        await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "срок самого чата возвращается, хотя у задачи срока нет");
        SetIdle(chat.Id);
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().BeNull("возвращённый срок течёт от снятия: уборка сносит чат");
    }

    // R-HOLD-K3 (хвост вердикта): инвариант «ни один путь ResumeFeatureTurnsAfterHold не
    // выходит наружу» — ручка зовёт сервис без обёртки (TasksController.cs:951), опираясь
    // на обхват всех сбойных веток внутри сервиса. Здесь один вызов проходит через все
    // четыре catch метода: рассылку в expired-ветке (:1228), сбой продолжения (:1242),
    // рассылку об успешном продолжении (:1258) и карточку в ключевой чат (:1349).
    // Мутация: убрать try/catch expired-ветки (:1228) — исключение доходит до теста, он
    // краснеет. Внутренний catch (:1258) вложен во внешний (:1242): его снятие утечки не
    // даёт — outer ловит, проверено мутацией.
    [Fact]
    public async Task ResumeFeatureTurnsAfterHold_NoInternalFailureEscapes()
    {
        var user = _userStore.Add("hold-no-escape", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var gone = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг с исчезнувшим чатом", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var broken = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг со сбоем продолжения", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var ok = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг, который продолжится", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var goneChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "gone",
            taskId: gone.Id);
        var brokenChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "broken",
            taskId: broken.Id);
        var okChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "ok",
            taskId: ok.Id);
        _tasks.MarkClaudeStarted(gone.Id, goneChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(broken.Id, brokenChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(ok.Id, okChat.Id, DateTime.UtcNow);
        var brokenAdapter = SetRunningTurn(brokenChat.Id);
        brokenAdapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Throws(new InvalidOperationException("чат оборван"));
        SetRunningTurn(okChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);
        keyAdapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Throws(new InvalidOperationException("ключевой чат недоступен"));

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        await _sessions.DeleteAsync(goneChat.Id); // чат исчез за паузу

        // Рассылка владельцу отказывает на каждом вызове: оба catch рассылки отработаны.
        var failing = new Mock<ISessionBroadcaster>();
        failing.Setup(b => b.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Throws(new InvalidOperationException("рассылка недоступна"));
        var sut = new TaskExecutionService(_tasks, _sessions, _personas, failing.Object, _push, _notif,
            NullLogger<TaskExecutionService>.Instance, _config, kb: _notesKb);

        _tasks.ClearHold(epic.Id);
        // Без обёртки: если любая ветка выпустит exception — тест умрёт, это и есть проверка.
        var resumed = await sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        resumed.Should().Be(1, "сбой одного шага не обрывает цикл: здоровый шаг продолжен");
        _tasks.GetById(gone.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldExpiredReason,
            "терминальная пометка поставлена, несмотря на сбой рассылки");
        _tasks.GetById(broken.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "продолжение сорвалось — пометка остаётся, шаг не возвращён");
        _tasks.GetById(ok.Id)!.ExecutorStopReason.Should().BeNull(
            "пометка гасится, несмотря на сбой уведомления: шаг возвращён в работу");
    }

    // W4-HOLD-J (хвост принятого R-J3, критерий c1): сбой самой марки — записи tasks.json —
    // не обрывает цикл. Сценарий идёт через второй экземпляр стора в синхронном режиме
    // (Tasks:SaveDebounceMs=0), где tasks.json заменён каталогом: любая запись падает.
    // Первый (expired) шаг не помечается — без try IOException ушла бы из метода: второй
    // шаг не получил бы продолжения, карточка не ушла бы оркестратору. Мутация: вернуть
    // марку без try — тест умирает на исключении.
    // Хвост R-J4 (критерий c1): шаг с исчезнувшим чатом истёк по контракту §1 :241 даже
    // при сбойной марке — карточка обязана назвать его, а не молчать «прерванных шагов нет».
    // Мутация: убрать expired++ в catch марки — карточка теряет «1 шаг не продолжен» и
    // «перезапустить», последние два Verify краснеют.
    [Fact]
    public async Task ResumeFeatureTurnsAfterHold_MarkSaveFails_SecondStepContinuesAndCardSent()
    {
        var user = _userStore.Add("hold-mark-save-fail", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var gone = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг без чата", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var live = _tasks.Create(null, user.Id, new CreateTaskRequest("Живой шаг", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var goneChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "gone",
            taskId: gone.Id);
        var liveChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "live",
            taskId: live.Id);
        _tasks.MarkClaudeStarted(gone.Id, goneChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(live.Id, liveChat.Id, DateTime.UtcNow);
        SetRunningTurn(goneChat.Id);
        var liveAdapter = SetRunningTurn(liveChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        await _sessions.DeleteAsync(goneChat.Id); // чат исчез за паузу
        _tasks.ClearHold(epic.Id);

        // Тот же стор, второй экземпляр, синхронная запись: состояние на диске уже целое.
        _tasks.Flush();
        var syncConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["Tasks:SaveDebounceMs"] = "0",
        }).Build();
        var syncTasks = new TaskManager(syncConfig, personas: _personas);
        // Сбой записи: tasks.json становится каталогом — File.Move под него не проходит.
        var storePath = Path.Combine(_dir, "tasks.json");
        File.Delete(storePath);
        Directory.CreateDirectory(storePath);

        var broken = new Mock<ISessionBroadcaster>();
        broken.Setup(b => b.ToOwner(It.IsAny<string>(), It.IsAny<ServerMessage>()))
            .Returns(Task.CompletedTask);
        var sut = new TaskExecutionService(syncTasks, _sessions, _personas, broken.Object, _push, _notif,
            NullLogger<TaskExecutionService>.Instance, _config, kb: _notesKb);

        var resumed = await sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);

        // Метод не бросил и дошёл до конца цикла: второй шаг получил продолжение, карточка
        // ушла. resumed=0 — у живого шага пометку тоже не снять (стор сломан), но продолжение
        // в чат доставлено: цикл не оборвался на сбое марки.
        resumed.Should().Be(0, "ни один шаг не вернулся в работу: запись стора падает");
        liveAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("продолжи")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>()),
            Times.Once(), "сбой марки не оборвал цикл: второй шаг получил продолжение");
        keyAdapter.Verify(a => a.SendMessageAsync(
            It.Is<string>(t => t.Contains("Пауза снята") && t.Contains("1 шаг не продолжен") &&
                              t.Contains("перезапустить")),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()),
            Times.Once(), "карточка ушла оркестратору и честно назвала expired-шаг, " +
                         "несмотря на сбой стора: его перезапускают штатно");
        syncTasks.Dispose();
    }

    // W4-HOLD-N (хвост R-HOLD-D2, критерий c2): страховка необратимой заморозки — при
    // удалении эпика на паузе его шаги больше никогда не «сняются», и их чаты остались бы
    // вечными. Ручка удаления зовёт RestoreFrozenChatsAfterFeatureDelete до Delete; здесь —
    // сама сервисная метода. Мутация: убрать RestoreFrozenExpiry в теле метода — краснеет
    // абзац про срок (он не вернулся), последний — чат пережил бы уборку.
    // Хвост R-N2 (минор 2): в тесте есть и второй шаг с вечным чатом — заморозки у него
    // нет, возвращать нечего. Мутация: убрать guard HoldFrozen в счётчике — restored
    // станет 2 (RestoreFrozenExpiry возвращает unchanged запись, и она считалась бы возвратом).
    [Fact]
    public async Task FeatureDelete_RestoresFrozenChatExpiry()
    {
        var user = _userStore.Add("hold-delete", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10); // срок, который поставил запуск исполнителя
        SetRunningTurn(chat.Id);
        // Второй шаг — с вечным чатом (срока нет → FreezeExpiryForHold нечего сохранить):
        // пауза его тоже помечает, но после удаления эпика возвращать нечего.
        var eternalStep = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг с вечным чатом",
            Role: "step", FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var eternalChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits,
            name: "eternal", taskId: eternalStep.Id);
        _tasks.MarkClaudeStarted(eternalStep.Id, eternalChat.Id, DateTime.UtcNow);
        SetRunningTurn(eternalChat.Id);

        _tasks.SetHold(epic.Id, "manual");
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().BeNull("пауза заморозила срок");

        _tasks.Delete(epic.Id);
        var restored = _sut.RestoreFrozenChatsAfterFeatureDelete(epic.Id, user.Id);
        restored.Should().Be(1, "возвращён только замороженный чат: вечному нечего возвращать");
        _sessions.GetById(eternalChat.Id)!.ExpiresAfterMinutes.Should().BeNull(
            "вечный чат не стал временным от несуществующей заморозки");
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "замороженный срок восстановлен — чат больше не вечный");
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().BeNull("возвращённый срок течёт: уборка сносит чат");
    }

    // Хвост N2 (минор 3, критерий c3): step-ветка общей страховки RestoreFrozenChatsBeforeDelete
    // обязана считать только фактический возврат, как feature-ветка: прежний паттерн
    // «RestoreFrozenExpiry(...) is not null ? 1 : 0» считал и чаты без заморозки — вечному
    // чату возвращать нечего. Мутация: убрать guard HoldFrozen — первый счётный абзац
    // для вечного чата краснеет (метод вернул бы 1).
    [Fact]
    public async Task RestoreFrozenChatsBeforeDelete_StepCountsOnlyActualReturns()
    {
        var user = _userStore.Add("hold-delete-step-count", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var frozen = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг с замороженным чатом",
            Role: "step", FeatureId: epic.Id, Status: TaskItemStatus.InProgress,
            ExecutionExpiresAfterMinutes: 10));
        var frozenChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "frozen",
            taskId: frozen.Id);
        _tasks.MarkClaudeStarted(frozen.Id, frozenChat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(frozenChat.Id, 10);
        // Вечный чат: срока нет → FreezeExpiryForHold нечего сохранить, заморозки на чате нет.
        var eternal = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг с вечным чатом",
            Role: "step", FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var eternalChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "eternal",
            taskId: eternal.Id);
        _tasks.MarkClaudeStarted(eternal.Id, eternalChat.Id, DateTime.UtcNow);
        SetRunningTurn(frozenChat.Id);
        SetRunningTurn(eternalChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(frozenChat.Id);
        SetIdle(eternalChat.Id);

        // Фактический возврат считается.
        _sut.RestoreFrozenChatsBeforeDelete(_tasks.GetById(frozen.Id)!).Should().Be(1,
            "на чате есть сохранённая заморозка — возврат настоящий");
        _sessions.GetById(frozenChat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "замороженный срок вернулся его чату");
        // Помеченный шаг без заморозки — не возврат.
        _sut.RestoreFrozenChatsBeforeDelete(_tasks.GetById(eternal.Id)!).Should().Be(0,
            "вечному чату нечего возвращать: пустой RestoreFrozenExpiry не считается");
        _sessions.GetById(eternalChat.Id)!.ExpiresAfterMinutes.Should().BeNull(
            "вечный чат не стал временным от несуществующей заморозки");
    }

    // W4-HOLD-N (хвост R-N п.1, критерий c1): повторная пауза не затирает сохранённый срок —
    // FreezeExpiryForHold пишет его, только если срок снят сейчас. Вторая пауза видит
    // ExpiresAfterMinutes == null: безусловная запись затёр бы сохранённое значение null,
    // после снятия чат стал бы вечным — регрессия дельты. Мутация: убрать guard
    // «is not null» в FreezeExpiryForHold — краснеет абзац про сохранённое значение.
    [Fact]
    public async Task TwoPauses_ClearHold_RestoresSavedExpiry()
    {
        var user = _userStore.Add("hold-two-pauses", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10);
        SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);
        _sessions.GetById(chat.Id)!.HoldFrozenExpiresAfterMinutes.Should().Be(10,
            "первая пауза сохранила срок самого чата");

        // Вторая пауза той же фичи: ручка зовёт цикл пометки на каждом POST hold
        // (TasksController.cs:890) — заморозка вызывается снова.
        _tasks.SetHold(epic.Id, FeatureHoldReasons.Queue);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        _sessions.GetById(chat.Id)!.HoldFrozenExpiresAfterMinutes.Should().Be(10,
            "вторая пауза не затирает сохранённое значение null");

        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Queue);
        resumed.Should().Be(1, "шаг возвращён");
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "после снятия паузы срок возвращается — чат не вечный");
        SetIdle(chat.Id);
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(chat.Id).Should().BeNull("возвращённый срок течёт: уборка сносит чат");
    }

    // W4-HOLD-N (хвост R-N п.2, критерий c2): гонка «снятие → новая пауза до доставки».
    // Guard на входе метода пройден, а пауза встаёт во время await'ов цикла — её ловит
    // перечитывание Hold внутри цикла перед каждой отправкой. Side-effect первого
    // продолжения (мок адаптера) ставит новую паузу: второй шаг не продолжается, карточка
    // «пауза снята» не уходит. Мутация: убрать HeldNow() в цикле — второй шаг продолжится,
    // Verify и абзац про пометку краснеют.
    [Fact]
    public async Task ResumeLoop_NewPauseMidLoop_HoldsSecondStepAndCard()
    {
        var user = _userStore.Add("hold-race-loop", "password123", "user");
        var keyChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "key");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature",
            SourceSessionId: keyChat.Id));
        var first = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг, продолженный первым", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var second = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг, держимый новой паузой", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var firstChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "first",
            taskId: first.Id);
        var secondChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "second",
            taskId: second.Id);
        _tasks.MarkClaudeStarted(first.Id, firstChat.Id, DateTime.UtcNow);
        _tasks.MarkClaudeStarted(second.Id, secondChat.Id, DateTime.UtcNow);
        var firstAdapter = SetRunningTurn(firstChat.Id);
        var secondAdapter = SetRunningTurn(secondChat.Id);
        var keyAdapter = SetRunningTurn(keyChat.Id);

        // Сама гонка: новая пауза встаёт в момент доставки первого продолжения.
        firstAdapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(() => { _tasks.SetHold(epic.Id, FeatureHoldReasons.Queue); return Task.CompletedTask; });

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        _tasks.ClearHold(epic.Id);

        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        // Хвост R-N2 (минор 1): пометку ставит первая пауза, а ClearHold её не гасит
        // (TaskManager.cs:1306-1308) — «останется ли она» само по себе ничего не доказывает.
        // Поэтому проверка идёт ДО счётчика: без guard'а в цикле второй шаг был бы продолжен
        // и ClearExecutorStopped снял бы пометку — краснеет именно этот абзац.
        _tasks.GetById(second.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "guard цикла поймал новую паузу: второй шаг помечен паузой, а не «продолжен»");
        resumed.Should().Be(1, "первый шаг продолжен до того, как встала новая пауза");
        secondAdapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Never(),
            "новая пауза держит продолжение второго шага");
        keyAdapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never(),
            "карточка «пауза снята» не уходит, пока держится новая пауза");
    }

    // ─── W4-SEAM: доработка швов (R2, R3) ─────────────────────────────────────────────────

    // W4-SEAM R2 (минор-мажор): «Стоп» человека по шагу, стоящему на паузе, не затирает
    // причину паузы. Без guard'а MarkExecutorStopped безусловно пишет interrupted_by_user:
    // шаг выпадает из продолжения (фильтр по feature_held его не видит), а замороженный
    // срок чата никто не возвращает — чат вечен. Мутации: убрать guard в MarkStoppedByUserAsync
    // — краснеет первый абзац (причина затёрта), следом — отсутствие продолжения и срока.
    [Fact]
    public async Task UserStopOnPausedStep_KeepsFeatureHeld_AndClearReturnsExpiry()
    {
        var user = _userStore.Add("seam-stop-on-hold", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича на паузе", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(chat.Id, 10);
        SetRunningTurn(chat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(chat.Id);

        // «Стоп» человека по той же сессии: процесс уже гашен, пометку не трогаем.
        (await _sut.MarkStoppedByUserAsync(chat.Id)).Should().BeNull(
            "Стоп на паузе не пересоздаёт остановку: возвращать эту причину — работа паузы");
        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().Be(ExecutorStopClassifier.FeatureHeldReason,
            "«Стоп» не затирает feature_held: шаг остаётся в продолжении");

        _tasks.ClearHold(epic.Id);
        var resumed = await _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        resumed.Should().Be(1, "стопнутый на паузе шаг остаётся продолженным");
        _sessions.GetById(chat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "замороженный срок возвращается при снятии: чат не вечный");
    }

    // W4-SEAM R3 (минор, «четвёртая дверь»): штатный перезапуск шага с пометкой feature_held
    // при уже снятом Hold (доставка продолжения упала, паузу больше не снимали). Новый чат
    // поднимается, MarkClaudeStarted снимает пометку — и никто больше не вернёт старому чату
    // замороженный срок: шаг перестанет быть feature_held, эпик не удаляется. Мутация: убрать
    // вызов RestoreFrozenChatOfTask в ExecuteClaimedAsync — абзац про срок краснеет (чат
    // вечный, уборка его не сносит).
    [Fact]
    public async Task RestartOfFeatureHeldStep_ReturnsFrozenExpiryOfOldChat()
    {
        var user = _userStore.Add("seam-restart-frozen", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress, ExecutionExpiresAfterMinutes: 10));
        var oldChat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec-old",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, oldChat.Id, DateTime.UtcNow);
        _sessions.SetExpiry(oldChat.Id, 10);
        SetRunningTurn(oldChat.Id);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        SetIdle(oldChat.Id);
        // Состояние четвёртой двери: Hold снят, продолжение не доставлено — шаг и его чат
        // на паузе. Штатный перезапуск (tasks_run_executor) не должен оставить чат вечным.
        _tasks.ClearHold(epic.Id);

        var started = await _sut.ExecuteAsync(step, auto: false);
        started.LinkedSessionId.Should().NotBe(oldChat.Id, "перезапуск поднимает новый чат");
        _sessions.GetById(oldChat.Id)!.ExpiresAfterMinutes.Should().Be(10,
            "замороженный чат перезапускаемого шага получает свой срок обратно — он не вечный");
        SetIdle(oldChat.Id);
        await new ChatExpiryService(_sessions, NullLogger<ChatExpiryService>.Instance)
            .TickAsync(DateTime.UtcNow.AddMinutes(60));
        _sessions.GetById(oldChat.Id).Should().BeNull(
            "возвращённый срок течёт: сиротский чат сносится уборкой, а не живёт вечно");
    }

    // ─── W4-SEAM2: признак «доставка уже была» (хвост R-SEAM2 п.1) ─────────────────────────

    // W4-SEAM2 (минор, критерий c1): два снятия подряд — одно продолжение. Первый вызов
    // ставит признак «продолжение отправляется» ДО доставки; пока она висит на gate'е мока,
    // второй вызов видит признак и пропускает шаг. Мутации: убрать проверку признака в цикле
    // — второй вызов шлёт снова, счёт становится 2 (проверено); убрать гашение признака при
    // бое доставки — краснеет R1-тест контроллера (ClearHold_PovtorPosleSboyaDostavki):
    // после первого отказа признак остался бы, и повтор продолжения не ушёл бы.
    [Fact]
    public async Task ResumeTwiceInARow_ContinuesOnce()
    {
        var user = _userStore.Add("seam2-double-resume", "password123", "user");
        var epic = _tasks.Create(null, user.Id, new CreateTaskRequest("Фича", Role: "feature"));
        var step = _tasks.Create(null, user.Id, new CreateTaskRequest("Шаг на паузе", Role: "step",
            FeatureId: epic.Id, Status: TaskItemStatus.InProgress));
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "exec",
            taskId: step.Id);
        _tasks.MarkClaudeStarted(step.Id, chat.Id, DateTime.UtcNow);
        var execAdapter = SetRunningTurn(chat.Id);

        // Первая доставка висит на gate'е: второй вызов входит в цикл, пока она в полёте.
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        execAdapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Returns(() => gate.Task);

        _tasks.SetHold(epic.Id, FeatureHoldReasons.Manual);
        _sut.InterruptFeatureTurnsOnHold(epic.Id, user.Id);
        _tasks.ClearHold(epic.Id);

        var first = _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        var second = _sut.ResumeFeatureTurnsAfterHold(epic.Id, user.Id, FeatureHoldReasons.Manual);
        gate.SetResult(true);
        (await first + await second).Should().Be(1, "продолжение зачтено ровно один раз");
        execAdapter.Verify(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once,
            "второй вызов увидел признак «уже слалось» и не продублировал продолжение");
        _tasks.GetById(step.Id)!.ExecutorStopReason.Should().BeNull(
            "первое снятие пометку сняло: шаг снова в работе");
        _tasks.GetById(step.Id)!.HoldResumeDeliveredAt.Should().BeNull(
            "признак гасится вместе с пометкой — он живёт только при ней");
    }
}
