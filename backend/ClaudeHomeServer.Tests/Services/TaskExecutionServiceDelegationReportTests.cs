using System.Reflection;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// B4, ВХОД хода-реакции постановщика (дыра покрытия, из-за которой баг дожил до ревью).
// TurnAccumulator гасит ВЫХОД: ответ ровно `<no-reply/>` не оставляет реплики. А вот вход —
// сам служебный промпт — уходил в ленту и в history.json пузырём «Автоматически» от лица
// постановщика с сырым текстом протокола («…ответь ровно <no-reply/>»): silent гасит только
// призрак в очереди (VisiblePending), но не доставку. Итог — снова два сообщения об одном
// факте, и второе показывает человеку внутреннюю кухню.
//
// Здесь гоняется весь путь доклада целиком: TryDeliverCompletionAsync → ReportToDelegatorAsync
// → SendOrEnqueueAsync → SendDirectAsync. CLI не поднимается — процессом чата подставляется мок
// адаптера (как в SessionManagerTests), поэтому тест остаётся юнитом и не зависит от claude.exe.
public class TaskExecutionServiceDelegationReportTests : IDisposable
{
    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly PersonaManager _personas;
    private readonly UserStore _userStore;
    private readonly SessionManager _sessions;
    private readonly TaskExecutionService _sut;
    private readonly List<ServerMessage> _sent = [];
    // Бродкасты приходят и из фоновых задач (уведомления, наблюдатели хода) — снимок под локом
    private readonly object _sentLock = new();

    private List<T> Sent<T>()
    {
        lock (_sentLock) return _sent.OfType<T>().ToList();
    }

    public TaskExecutionServiceDelegationReportTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_report_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, _userStore, appSettings);
        _personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: _personas);

        var clientProxy = new Mock<IClientProxy>();
        clientProxy
            .Setup(c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object[], CancellationToken>((_, args, _) =>
            {
                if (args.Length > 0 && args[0] is ServerMessage msg)
                    lock (_sentLock) _sent.Add(msg);
            })
            .Returns(Task.CompletedTask);
        var clients = new Mock<IHubClients>();
        // Только session-группа: клиент открытого чата состоит и в user_/project_-группе,
        // широкая рассылка задвоила бы сообщения в снимке (как в SessionManagerTests)
        clients.Setup(c => c.Group(It.Is<string>(g => !g.StartsWith("project_") && !g.StartsWith("user_"))))
            .Returns(clientProxy.Object);
        clients.Setup(c => c.Group(It.Is<string>(g => g.StartsWith("project_") || g.StartsWith("user_"))))
            .Returns(new Mock<IClientProxy>().Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        var notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(notifStore, hub.Object, push, _personas, projectManager,
            NullLogger<NotificationService>.Instance);

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new SkillsService(), wkStore, llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(_userStore);
        var personaMemory = new PersonaMemoryService(knowledge, _personas, _userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(_personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(projectManager, hub.Object, new ChatHistoryService(config), config,
            adapters, falCost, usage, appSettings, _userStore, jwt, server.Object, llmProviders, notesKb,
            flags, _personas, personaMemory, bindings, promptBuilder, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox);

        _sut = new TaskExecutionService(_tasks, _sessions, _personas, hub.Object, push, notesKb, notif,
            NullLogger<TaskExecutionService>.Instance, config, flags: flags);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (!Directory.Exists(_dir)) return;
        // История пишется из fire-and-forget обработчиков — уборка temp не предмет теста
        for (var i = 1; ; i++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i >= 5) return;
                Thread.Sleep(50 * i);
            }
        }
    }

    // --- Обвязка сценария ---

    private Persona CreatePersona(string ownerId, string name) =>
        _personas.Create(ownerId, name, role: "Разработчик", description: null, systemPrompt: null,
            model: null, effort: null, scope: PersonaScope.Global, projectId: null,
            color: null, greeting: null, memoryEnabled: false);

    // Подставной процесс чата: без него SendDirectAsync поднял бы настоящий CLI.
    // Реестр сессий приватный — тот же white-box приём, что в SessionManagerTests.
    private void StubProcess(Session session)
    {
        var field = typeof(SessionManager).GetField("_sessions",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var entry = ((System.Collections.IDictionary)field.GetValue(_sessions)!)[session.Id]!;
        var adapter = new Mock<ILlmSessionAdapter>();
        adapter.SetupGet(a => a.Info).Returns(session);
        adapter.Setup(a => a.SendMessageAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
            It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);
        entry.GetType().GetField("Process")!.SetValue(entry, adapter.Object);
    }

    // Задача, делегированная персоной-постановщиком из её чата и закрытая персоной-исполнителем:
    // оба сигнала join-а (R и D) на месте, доклад готов уходить.
    private async Task<(TaskItem Task, Session Parent, Persona Executor)> ArrangeDelegatedTaskAsync(
        bool reportCard, SessionStatus parentStatus = SessionStatus.Active)
    {
        var user = _userStore.Add("report-owner", "password123", "user");
        if (reportCard)
            _userStore.SetFeatureFlag(user.Id, FeatureFlagKeys.TaskReportCard, true);
        var delegator = CreatePersona(user.Id, "Постановщик");
        var executor = CreatePersona(user.Id, "Исполнитель");

        var parent = await _sessions.CreatePersonaChatAsync(user.Id, delegator.Id, ClaudeMode.AcceptEdits,
            name: "Чат постановщика");
        StubProcess(parent);
        parent.Status = parentStatus;

        var created = _tasks.Create(null, user.Id, new CreateTaskRequest("Пагинация истории чата"));
        // LinkedSessionId указывает на несуществующий у SessionManager чат — адресатом доклада
        // остаётся SourceSessionId (ResolveReportTarget), как у задачи, закрытой без запуска
        _tasks.MarkClaudeStarted(created.Id, "executor-session", DateTime.UtcNow);
        _tasks.MarkClaudeResult(created.Id, "success");
        var task = _tasks.GetById(created.Id)!;
        task.CreatedByPersonaId = delegator.Id;
        task.PersonaId = executor.Id;
        task.SourceSessionId = parent.Id;
        task.ResultMarkdown = "Добавил постраничную загрузку, тесты зелёные.";
        task.Status = TaskItemStatus.Done;
        return (task, parent, executor);
    }

    // --- Критерий приёмки 3: следов пустого хода в ленте нет ---

    [Fact]
    public async Task ДокладСФлагом_ХодРеакции_НеОставляетПузыряСПромптомВЛенте()
    {
        var (task, _, _) = await ArrangeDelegatedTaskAsync(reportCard: true);

        await _sut.TryDeliverCompletionAsync(task);

        // Факт «задача выполнена» несёт РОВНО одно сообщение — карточка доклада от лица исполнителя
        Sent<GuestTextMessage>().Should().ContainSingle()
            .Which.Text.Should().StartWith(TaskExecutionService.DelegationReportMarker);
        // Ход-реакция доставлен (иначе постановщик не смог бы принять решение), но в ленте он —
        // плашка-разделитель: сырой служебный промпт человеку не показывается
        var reaction = Sent<UserMessageMessage>().Should().ContainSingle().Subject;
        reaction.Text.Should().Contain(SessionManager.NoReplyMarker, "это тот самый служебный промпт");
        reaction.StaffNote.Should().Be(TaskExecutionService.DelegatorReactionStaffNote);
        Sent<UserMessageMessage>().Where(m => m.StaffNote is null).Should()
            .BeEmpty("пузырь «Автоматически» с сырым текстом протокола — это второе сообщение об одном факте");
    }

    [Fact]
    public async Task ДокладСФлагом_ХодРеакции_НеОставляетПузыряСПромптомВИстории()
    {
        var (task, parent, _) = await ArrangeDelegatedTaskAsync(reportCard: true);

        await _sut.TryDeliverCompletionAsync(task);

        var history = await _sessions.GetHistoryAsync(parent.Id);
        history.OfType<StoredTextMessage>().Should().ContainSingle("карточка доклада — одна")
            .Which.DelegationTaskId.Should().Be(task.Id);
        var stored = history.OfType<StoredUserMessage>().Should().ContainSingle().Subject;
        stored.StaffNote.Should().Be(TaskExecutionService.DelegatorReactionStaffNote,
            "после перезагрузки страницы промпт обязан остаться плашкой, а не стать пузырём");
    }

    // Занятый чат постановщика: ход встаёт в очередь, и подпись плашки обязана дожить до
    // отложенной доставки (DeliverPendingAsync) — иначе баг возвращается на втором входе
    [Fact]
    public async Task ДокладСФлагом_ЗанятыйЧатПостановщика_ПодписьПлашкиЕдетВОчереди()
    {
        var (task, parent, _) = await ArrangeDelegatedTaskAsync(reportCard: true,
            parentStatus: SessionStatus.Working);

        await _sut.TryDeliverCompletionAsync(task);

        var queued = _sessions.GetPending(parent.Id).Should().ContainSingle().Subject;
        queued.Silent.Should().BeTrue("призраком служебный промпт не показываем");
        queued.StaffNote.Should().Be(TaskExecutionService.DelegatorReactionStaffNote);
        _sessions.GetVisiblePending(parent.Id).Should().BeEmpty();
    }

    // Критерий приёмки 5: флаг выключен — поведение ровно как до изменения (прежний промпт
    // без маркера молчания и прежний пузырь авто-хода)
    [Fact]
    public async Task ДокладБезФлага_ПрежнееПоведение()
    {
        var (task, _, _) = await ArrangeDelegatedTaskAsync(reportCard: false);

        await _sut.TryDeliverCompletionAsync(task);

        var reaction = Sent<UserMessageMessage>().Should().ContainSingle().Subject;
        reaction.StaffNote.Should().BeNull();
        reaction.Text.Should().NotContain(SessionManager.NoReplyMarker);
    }
}
