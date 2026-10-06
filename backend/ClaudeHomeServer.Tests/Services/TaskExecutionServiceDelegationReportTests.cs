using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
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
    private readonly TestSessionBroadcaster _broadcaster;

    // Захват только session-канала: оригинальный mock IHubContext настраивал клиентский
    // прокси только для session-группы (не user_/project_ — иначе задвоение в снимке),
    // и TestSessionBroadcaster сохраняет это правило: ToSession — единственная запись,
    // которая когда-либо попадала в assertions этого файла.
    private List<T> Sent<T>() => _broadcaster.Session.Select(t => t.Message)
        .OfType<T>()
        .ToList();

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

        var broadcaster = new TestSessionBroadcaster();

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        var notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(notifStore, broadcaster, push, _personas, projectManager,
            NullLogger<NotificationService>.Instance);

        var wkStore = new WorkspaceKnowledgeStore(config);
        var knowledge = new KnowledgeService(new Mock<IHttpClientFactory>().Object,
            Microsoft.Extensions.Options.Options.Create(new DifyOptions()), wkStore);
        var notesSvc = new NotesService(projectManager, config, NullLogger<NotesService>.Instance);
        var notesKb = new NotesKnowledgeService(knowledge, notesSvc, _userStore, config,
            NullLogger<NotesKnowledgeService>.Instance);

        var llmProviders = new LlmProviderRegistry(config);
        var subPool = new ClaudeSubscriptionPool(config);
        var adapters = new LlmSessionAdapterFactory(config, new AgentPromptSourceAdapter(new SkillsService()), new WorkspaceDatasetLookup(wkStore), llmProviders, subPool);
        var falCost = new FalCostService(new Mock<IHttpClientFactory>().Object, config);
        var usage = new UsageService(config);
        var server = new Mock<Microsoft.AspNetCore.Hosting.Server.IServer>();
        server.Setup(s => s.Features).Returns(new Microsoft.AspNetCore.Http.Features.FeatureCollection());
        var flags = new FeatureFlagService(_userStore);
        var bindings = new PersonaBindingsService(_personas, projectManager, wkStore,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance, notes: notesSvc, notesKb: notesKb);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(projectManager, new ChatHistoryService(config), config,
            adapters, falCost, usage, appSettings, _userStore, jwt, server.Object, llmProviders,
            flags, _personas, bindings, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox,
            broadcaster: broadcaster);

        _sut = new TaskExecutionService(_tasks, _sessions, _personas, broadcaster, push, notif,
            NullLogger<TaskExecutionService>.Instance, config, kb: notesKb);
        _broadcaster = broadcaster;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        // История пишется из fire-and-forget обработчиков — уборка temp не предмет теста
        Helpers.TestFs.DeleteDirectoryResilient(_dir);
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
        SessionStatus parentStatus = SessionStatus.Active)
    {
        var user = _userStore.Add("report-owner", "password123", "user");
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
    public async Task Доклад_ХодРеакции_НеОставляетПузыряСПромптомВЛенте()
    {
        var (task, _, _) = await ArrangeDelegatedTaskAsync();

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
    public async Task Доклад_ХодРеакции_НеОставляетПузыряСПромптомВИстории()
    {
        var (task, parent, _) = await ArrangeDelegatedTaskAsync();

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
    public async Task Доклад_ЗанятыйЧатПостановщика_ПодписьПлашкиЕдетВОчереди()
    {
        var (task, parent, _) = await ArrangeDelegatedTaskAsync(
            parentStatus: SessionStatus.Working);

        await _sut.TryDeliverCompletionAsync(task);

        var queued = _sessions.GetPending(parent.Id).Should().ContainSingle().Subject;
        queued.Silent.Should().BeTrue("призраком служебный промпт не показываем");
        queued.StaffNote.Should().Be(TaskExecutionService.DelegatorReactionStaffNote);
        _sessions.GetVisiblePending(parent.Id).Should().BeEmpty();
    }

    // --- Адресат доклада — запустивший исполнителя, а не только создатель карточки ---

    private async Task<Session> LaunchChatAsync(TaskItem task, string name)
    {
        var persona = CreatePersona(task.OwnerId!, "Запускающий " + name);
        var chat = await _sessions.CreatePersonaChatAsync(task.OwnerId!, persona.Id, ClaudeMode.AcceptEdits, name: name);
        StubProcess(chat);
        return chat;
    }

    private async Task<(int Cards, int Reactions)> CountAsync(string sessionId)
    {
        var history = await _sessions.GetHistoryAsync(sessionId);
        return (history.OfType<StoredTextMessage>().Count(m => m.DelegationTaskId is not null),
            history.OfType<StoredUserMessage>().Count(m => m.StaffNote == TaskExecutionService.DelegatorReactionStaffNote));
    }

    [Fact]
    public async Task Доклад_ЗапускИзДругогоЧата_КарточкаИАвтоХодЗапустившему_СоздателюТолькоКарточка()
    {
        var (task, creatorChat, _) = await ArrangeDelegatedTaskAsync();
        var launcher = await LaunchChatAsync(task, "Чат запустившего");
        task.ExecutorLaunchedBySessionId = launcher.Id;
        task.ExecutorLaunchedByPersonaId = launcher.PersonaId;

        await _sut.TryDeliverCompletionAsync(task);

        (await CountAsync(launcher.Id)).Should().Be((1, 1), "запустивший ждёт результат: карточка и авто-ход");
        (await CountAsync(creatorChat.Id)).Should().Be((1, 0), "создателю — только карточка, без платного авто-хода");
    }

    [Fact]
    public async Task Доклад_ЗапускИзЧатаСоздателя_КакРаньше()
    {
        var (task, creatorChat, _) = await ArrangeDelegatedTaskAsync();
        task.ExecutorLaunchedBySessionId = creatorChat.Id;
        task.ExecutorLaunchedByPersonaId = creatorChat.PersonaId;

        await _sut.TryDeliverCompletionAsync(task);

        (await CountAsync(creatorChat.Id)).Should().Be((1, 1), "одна карточка и один авто-ход, без дубля");
    }

    [Fact]
    public async Task Доклад_ПовторныйЗапускИзТретьегоЧата_АдресатПоследнийЗапустивший()
    {
        var (task, creatorChat, _) = await ArrangeDelegatedTaskAsync();
        var first = await LaunchChatAsync(task, "Первый");
        var last = await LaunchChatAsync(task, "Третий");
        task.ExecutorLaunchedBySessionId = first.Id;
        task.ExecutorLaunchedByPersonaId = first.PersonaId;
        // повторный запуск перезаписывает поля — так делает tasks.Update в tasks_run_executor
        _tasks.Update(task.Id, new UpdateTaskRequest(
            ExecutorLaunchedBySessionId: last.Id, ExecutorLaunchedByPersonaId: last.PersonaId));

        await _sut.TryDeliverCompletionAsync(task);

        (await CountAsync(last.Id)).Should().Be((1, 1));
        (await CountAsync(first.Id)).Should().Be((0, 0));
        (await CountAsync(creatorChat.Id)).Should().Be((1, 0));
    }

    [Fact]
    public async Task Доклад_ЧатИсполнительВынесенВКорень_НеУходитНикомуДажеПриЖивомЗапустившем()
    {
        var (task, creatorChat, _) = await ArrangeDelegatedTaskAsync();
        var launcher = await LaunchChatAsync(task, "Чат запустившего");
        var executorChat = await LaunchChatAsync(task, "Чат исполнителя");
        executorChat.ParentDetached = true;
        task.LinkedSessionId = executorChat.Id;
        task.ExecutorLaunchedBySessionId = launcher.Id;
        task.ExecutorLaunchedByPersonaId = launcher.PersonaId;

        await _sut.TryDeliverCompletionAsync(task);

        (await CountAsync(launcher.Id)).Should().Be((0, 0));
        (await CountAsync(creatorChat.Id)).Should().Be((0, 0));
        (await CountAsync(executorChat.Id)).Should().Be((0, 0));
    }

    // --- Адресат по цепочке: планировщик/QA создают и запускают задачи, доклад идёт оркестратору ---

    private sealed record Team(string OwnerId, Session Sofya, Session Egor, Session Vera, Persona Denis);

    private async Task<Team> ArrangeTeamAsync()
    {
        var user = _userStore.Add("chain-owner", "password123", "user");
        async Task<Session> Chat(string name)
        {
            var chat = await _sessions.CreatePersonaChatAsync(user.Id,
                CreatePersona(user.Id, name).Id, ClaudeMode.AcceptEdits, name: name);
            StubProcess(chat);
            return chat;
        }
        return new Team(user.Id, await Chat("Софья"), await Chat("Егор"), await Chat("Вера"),
            CreatePersona(user.Id, "Денис"));
    }

    // Задача, которую исполняет чат executorChat (планировщик/QA), поставленная из source и запущенная launchedBy
    private TaskItem MakeExecuting(Team team, Session executorChat, string? sourceId, string? launchedById,
        string? reportTo = null)
    {
        var t = _tasks.Create(null, team.OwnerId, new CreateTaskRequest("План " + executorChat.Name,
            ReportToSessionId: reportTo));
        _tasks.MarkClaudeStarted(t.Id, executorChat.Id, DateTime.UtcNow);
        var task = _tasks.GetById(t.Id)!;
        task.SourceSessionId = sourceId;
        task.ExecutorLaunchedBySessionId = launchedById;
        return task;
    }

    // Лист цепочки: задачу создал чат creatorChat, исполнил Денис, доклад готов уходить
    private TaskItem MakeLeaf(Team team, Session creatorChat, string? reportTo, string? launchedById)
    {
        var t = _tasks.Create(null, team.OwnerId, new CreateTaskRequest("Лист", ReportToSessionId: reportTo));
        _tasks.MarkClaudeStarted(t.Id, "leaf-executor-session", DateTime.UtcNow);
        _tasks.MarkClaudeResult(t.Id, "success");
        var task = _tasks.GetById(t.Id)!;
        task.CreatedByPersonaId = creatorChat.PersonaId;
        task.PersonaId = team.Denis.Id;
        task.SourceSessionId = creatorChat.Id;
        task.ExecutorLaunchedBySessionId = launchedById;
        task.ResultMarkdown = "Готово.";
        task.Status = TaskItemStatus.Done;
        return task;
    }

    [Fact]
    public async Task Доклад_ПланировщикСоздалСофьяЗапустила_КарточкаИАвтоХодСофье_ПланировщикуТолькоКарточка()
    {
        var team = await ArrangeTeamAsync();
        MakeExecuting(team, team.Egor, team.Sofya.Id, team.Sofya.Id);
        var leaf = MakeLeaf(team, team.Egor, reportTo: team.Sofya.Id, launchedById: team.Sofya.Id);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Sofya.Id)).Should().Be((1, 1));
        (await CountAsync(team.Egor.Id)).Should().Be((1, 0));
    }

    [Fact]
    public async Task Доклад_ПланировщикСоздалЧеловекЗапустилИзUI_АдресатReportToSessionId()
    {
        var team = await ArrangeTeamAsync();
        var leaf = MakeLeaf(team, team.Egor, reportTo: team.Sofya.Id, launchedById: null);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Sofya.Id)).Should().Be((1, 1));
        (await CountAsync(team.Egor.Id)).Should().Be((1, 0));
    }

    [Fact]
    public async Task Доклад_ПланировщикСамЗапустилИсполнителя_ПодъёмПоЦепочкеДоСофьи()
    {
        var team = await ArrangeTeamAsync();
        MakeExecuting(team, team.Egor, team.Sofya.Id, team.Sofya.Id);
        var leaf = MakeLeaf(team, team.Egor, reportTo: null, launchedById: team.Egor.Id);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Sofya.Id)).Should().Be((1, 1));
        (await CountAsync(team.Egor.Id)).Should().Be((1, 0));
    }

    [Fact]
    public async Task Доклад_СоздательНеИсполнитель_КакПослеЗапускаИзЧата()
    {
        var team = await ArrangeTeamAsync();
        var leaf = MakeLeaf(team, team.Sofya, reportTo: null, launchedById: null);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Sofya.Id)).Should().Be((1, 1));
        (await CountAsync(team.Egor.Id)).Should().Be((0, 0));
    }

    [Fact]
    public async Task Доклад_ЦепочкаИзДвухУровней_ДоСофьи()
    {
        var team = await ArrangeTeamAsync();
        MakeExecuting(team, team.Egor, team.Sofya.Id, team.Sofya.Id);
        MakeExecuting(team, team.Vera, team.Egor.Id, team.Egor.Id);
        var leaf = MakeLeaf(team, team.Vera, reportTo: null, launchedById: team.Vera.Id);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Sofya.Id)).Should().Be((1, 1));
        (await CountAsync(team.Egor.Id)).Should().Be((0, 0));
        (await CountAsync(team.Vera.Id)).Should().Be((1, 0), "создателю листа — только карточка");
    }

    [Fact]
    public async Task Доклад_РодительскаяСессияМертва_ОткатНаПоследнююЖивуюБезПадения()
    {
        var team = await ArrangeTeamAsync();
        MakeExecuting(team, team.Egor, "ghost-session", "ghost-session");
        var leaf = MakeLeaf(team, team.Egor, reportTo: "ghost-session", launchedById: team.Egor.Id);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Egor.Id)).Should().Be((1, 1), "цепочка оборвалась на Егоре — он последняя живая сессия");
    }

    [Fact]
    public async Task Доклад_ReportToМёртвый_ОткатНаСоздателяКарточки()
    {
        var team = await ArrangeTeamAsync();
        var leaf = MakeLeaf(team, team.Egor, reportTo: "ghost-session", launchedById: null);

        await _sut.TryDeliverCompletionAsync(leaf);

        (await CountAsync(team.Egor.Id)).Should().Be((1, 1));
    }

    [Fact]
    public async Task Доклад_ЦиклВЦепочке_НеВисит()
    {
        var team = await ArrangeTeamAsync();
        MakeExecuting(team, team.Egor, team.Vera.Id, team.Vera.Id);
        MakeExecuting(team, team.Vera, team.Egor.Id, team.Egor.Id);
        var leaf = MakeLeaf(team, team.Egor, reportTo: null, launchedById: team.Egor.Id);

        await _sut.TryDeliverCompletionAsync(leaf).WaitAsync(TimeSpan.FromSeconds(10));

        (await CountAsync(team.Vera.Id)).Should().Be((1, 1), "цикл Егор → Вера → Егор останавливается на Вере");
    }
}
