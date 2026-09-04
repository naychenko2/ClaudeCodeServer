using System.Reflection;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Hubs;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Services.Memory;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// —траховка Ђход исполнител€ закончилс€ успешно, а задачу он не закрылї. ƒо неЄ такой ход
// молча пропускалс€ гейтом join-а (TryDeliverCompletionAsync: Ђстатус не Doneї), и задача
// висела в Ђ¬ работеї вечно Ч нового хода могло не быть никогда, а человеку никто не говорил.
//
// «десь три ветки решени€ (ClassifyStall) и их эффекты: промежуточный ход многошаговой задачи
// не должен получать ничего (регресси€ на спам), брошенна€ задача Ч ровно один оклик исполнителю
// и ровно одно уведомление человеку. CLI не поднимаетс€: процесс чата подставл€етс€ моком
// адаптера (тот же приЄм, что в TaskExecutionServiceDelegationReportTests).
public class TaskExecutionServiceStallTests : IDisposable
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(15);
    private static readonly DateTime Now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly TaskManager _tasks;
    private readonly UserStore _userStore;
    private readonly SessionManager _sessions;
    private readonly NotificationStore _notifStore;
    private readonly TaskExecutionService _sut;
    private readonly List<ServerMessage> _sent = [];
    private readonly object _sentLock = new();

    private List<T> Sent<T>()
    {
        lock (_sentLock) return _sent.OfType<T>().ToList();
    }

    public TaskExecutionServiceStallTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "task_stall_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["DefaultProjectsPath"] = Path.Combine(_dir, "homes"),
                ["ClaudeUserProfileDir"] = Path.Combine(_dir, "claude-profile"),
                ["Tasks:ExecutorStaleMinutes"] = ((int)Stale.TotalMinutes).ToString(),
            })
            .Build();

        _userStore = new UserStore(config, new ClaudeHomeServer.Tests.Helpers.FakeHostEnvironment(),
            NullLogger<UserStore>.Instance);
        var appSettings = new AppSettingsService(config);
        var projectManager = new ProjectManager(config, _userStore, appSettings);
        var personas = new PersonaManager(config);
        _tasks = new TaskManager(config, personas: personas);

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
        // “олько session-группа: клиент чата состоит и в user_/project_-группе, широка€
        // рассылка задвоила бы сообщени€ в снимке
        clients.Setup(c => c.Group(It.Is<string>(g => !g.StartsWith("project_") && !g.StartsWith("user_"))))
            .Returns(clientProxy.Object);
        clients.Setup(c => c.Group(It.Is<string>(g => g.StartsWith("project_") || g.StartsWith("user_"))))
            .Returns(new Mock<IClientProxy>().Object);
        var hub = new Mock<IHubContext<SessionHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);

        var pushStore = new PushSubscriptionStore(config);
        var jwt = new JwtService(config, _userStore, NullLogger<JwtService>.Instance);
        var push = new PushService(config, pushStore, jwt, NullLogger<PushService>.Instance);
        _notifStore = new NotificationStore(config, NullLogger<NotificationStore>.Instance);
        var notif = new NotificationService(_notifStore, hub.Object, push, personas, projectManager,
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
        var personaMemory = new PersonaMemoryService(knowledge, personas, _userStore, config,
            NullLogger<PersonaMemoryService>.Instance);
        var bindings = new PersonaBindingsService(personas, projectManager, wkStore, notesSvc, notesKb,
            knowledge, new SkillsService(), _userStore, config, NullLogger<PersonaBindingsService>.Instance);
        var promptBuilder = new PersonaPromptBuilder(llmProviders);
        var sandbox = new ClaudeHomeServer.Services.Execution.SandboxManager(config,
            NullLogger<ClaudeHomeServer.Services.Execution.SandboxManager>.Instance);
        _sessions = new SessionManager(projectManager, hub.Object, new ChatHistoryService(config), config,
            adapters, falCost, usage, appSettings, _userStore, jwt, server.Object, llmProviders, notesKb,
            flags, personas, personaMemory, bindings, promptBuilder, subPool,
            NullLogger<SessionManager>.Instance, TestLauncherFactory.Instance, sandbox);

        _sut = new TaskExecutionService(_tasks, _sessions, personas, hub.Object, push, notesKb, notif,
            NullLogger<TaskExecutionService>.Instance, config);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (!Directory.Exists(_dir)) return;
        // »стори€ пишетс€ из fire-and-forget обработчиков Ч уборка temp не предмет теста
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

    // --- ѕредикат: три ветки решени€ ------------------------------------------

    // «адача после успешного хода исполнител€, который еЄ не закрыл
    private static TaskItem StaleTask(DateTime? nudgedAt = null, DateTime? alertedAt = null) => new()
    {
        Title = "ѕочинить билд",
        OwnerId = "user-1",
        Status = TaskItemStatus.InProgress,
        LinkedSessionId = "sess-1",
        ClaudeStartedAt = Now.AddHours(-1),
        ClaudeResult = "success",
        ExecutorNudgedAt = nudgedAt,
        ExecutorStaleAlertedAt = alertedAt,
        UpdatedAt = Now.AddHours(-1),
    };

    private static Session Chat(SessionStatus status, DateTime updatedAt) =>
        new() { Id = "sess-1", OwnerId = "user-1", Status = status, UpdatedAt = updatedAt };

    [Fact]
    public void ClassifyStall_»дЄт—ледующий’од_ЌичегоЌеƒелаем()
    {
        // ћногошагова€ задача: исполнитель работает дальше Ч оклик был бы спамом
        var action = TaskExecutionService.ClassifyStall(StaleTask(),
            Chat(SessionStatus.Working, Now.AddHours(-1)), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_’од∆дЄт–азрешени€_ЌичегоЌеƒелаем()
    {
        // Waiting Ч permission_request: о нЄм человека уже уведомили (BuildWaitingNotification)
        var action = TaskExecutionService.ClassifyStall(StaleTask(),
            Chat(SessionStatus.Waiting, Now.AddHours(-1)), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_“ишина орочеѕорога_ЌичегоЌеƒелаем()
    {
        var action = TaskExecutionService.ClassifyStall(StaleTask(),
            Chat(SessionStatus.Active, Now.AddMinutes(-5)), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_„атћолчитƒольшеѕорога_ќкликаем»сполнител€()
    {
        var action = TaskExecutionService.ClassifyStall(StaleTask(),
            Chat(SessionStatus.Active, Now.AddMinutes(-16)), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.Nudge);
    }

    [Fact]
    public void ClassifyStall_„атћолчитƒольшеќкна—вежести_—разу«овЄм„еловека()
    {
        // «адача висит со вчера: платный оклик в позавчерашний разговор бесполезен, а на
        // первом тике после обновлени€ такие ходы ушли бы во все старые задачи разом
        var task = StaleTask();
        task.UpdatedAt = Now - TaskExecutionService.NudgeWindow.Add(TimeSpan.FromHours(1));

        var action = TaskExecutionService.ClassifyStall(task,
            Chat(SessionStatus.Active, task.UpdatedAt), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.Alert);
    }

    [Fact]
    public void ClassifyStall_„ата»сполнител€Ќет_—разу«овЄм„еловека()
    {
        // „ат удалЄн или протух по TTL Ч окликать некого, отсчЄт от самой задачи
        var action = TaskExecutionService.ClassifyStall(StaleTask(), null, Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.Alert);
    }

    [Fact]
    public void ClassifyStall_ќклик—вежий_∆дЄмќтвета()
    {
        var action = TaskExecutionService.ClassifyStall(StaleTask(nudgedAt: Now.AddMinutes(-5)),
            Chat(SessionStatus.Active, Now.AddMinutes(-30)), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_ќкликЌеѕомог_«овЄм„еловека()
    {
        var action = TaskExecutionService.ClassifyStall(StaleTask(nudgedAt: Now.AddMinutes(-16)),
            Chat(SessionStatus.Active, Now.AddMinutes(-30)), Now, Stale);

        action.Should().Be(TaskExecutionService.ExecutorStallAction.Alert);
    }

    [Fact]
    public void ClassifyStall_„еловека”жеѕозвали_ЅольшеЌичего()
    {
        var task = StaleTask(nudgedAt: Now.AddHours(-2), alertedAt: Now.AddHours(-1));

        TaskExecutionService.ClassifyStall(task, Chat(SessionStatus.Active, Now.AddHours(-2)), Now, Stale)
            .Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_«адача«акрыта_ЌичегоЌеƒелаем()
    {
        var task = StaleTask();
        task.Status = TaskItemStatus.Done;

        TaskExecutionService.ClassifyStall(task, Chat(SessionStatus.Active, Now.AddHours(-2)), Now, Stale)
            .Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_’одѕровалилс€_ЌичегоЌеƒелаем()
    {
        // ѕровал уведомл€ет сам (ЂЌе смог выполнить задачуї) Ч дубл€ быть не должно
        var task = StaleTask();
        task.ClaudeResult = "error";

        TaskExecutionService.ClassifyStall(task, Chat(SessionStatus.Active, Now.AddHours(-2)), Now, Stale)
            .Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_»сполнительќстановлен“ерминально_ЌичегоЌеƒелаем()
    {
        // ” остановки своЄ уведомление с причиной (HandleExecutorStoppedAsync)
        var task = StaleTask();
        task.ExecutorStoppedAt = Now.AddMinutes(-30);
        task.ExecutorStopReason = ExecutorStopClassifier.AuthFailedReason;

        TaskExecutionService.ClassifyStall(task, Chat(SessionStatus.Active, Now.AddHours(-2)), Now, Stale)
            .Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    [Fact]
    public void ClassifyStall_’од≈щЄ»дЄт_ЌичегоЌеƒелаем()
    {
        // ClaudeResult пуст Ч result первого хода ещЄ не пришЄл
        var task = StaleTask();
        task.ClaudeResult = null;

        TaskExecutionService.ClassifyStall(task, Chat(SessionStatus.Active, Now.AddHours(-2)), Now, Stale)
            .Should().Be(TaskExecutionService.ExecutorStallAction.None);
    }

    // --- Ёффекты: оклик исполнителю и уведомление человеку --------------------

    // ∆ивой чат-исполнитель с подставным процессом (иначе SendOrEnqueueAsync подн€л бы CLI)
    private async Task<(TaskItem Task, Session Chat)> ArrangeExecutorChatAsync(TimeSpan silence)
    {
        var user = _userStore.Add("stall-owner", "password123", "user");
        var chat = await _sessions.CreateChatAsync(user.Id, ClaudeMode.AcceptEdits, name: "«адача: билд");
        StubProcess(chat);
        chat.Status = SessionStatus.Active;
        chat.UpdatedAt = Now - silence;

        var created = _tasks.Create(null, user.Id, new CreateTaskRequest("ѕочинить билд"));
        _tasks.MarkClaudeStarted(created.Id, chat.Id, Now.AddHours(-2));
        _tasks.MarkClaudeResult(created.Id, "success");
        return (_tasks.GetById(created.Id)!, chat);
    }

    // ѕодставной процесс чата: реестр сессий приватный Ч тот же white-box приЄм, что в
    // SessionManagerTests и TaskExecutionServiceDelegationReportTests
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

    private async Task<int> CountNotificationsAsync(string ownerId) =>
        (await _notifStore.GetListAsync(ownerId)).Count;

    [Fact]
    public async Task CheckStalledExecutorAsync_ћногошагова€«адачаћежду’одами_ЌиќкликаЌи”ведомлени€()
    {
        // ’од закончилс€ минуту назад Ч следующий вполне может начатьс€ сам
        var (task, _) = await ArrangeExecutorChatAsync(silence: TimeSpan.FromMinutes(1));

        await _sut.CheckStalledExecutorAsync(task, Now);

        var after = _tasks.GetById(task.Id)!;
        after.ExecutorNudgedAt.Should().BeNull();
        after.ExecutorStaleAlertedAt.Should().BeNull();
        Sent<UserMessageMessage>().Should().BeEmpty("между ходами исполнител€ дЄргать нельз€");
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(0);
    }

    [Fact]
    public async Task CheckStalledExecutorAsync_„атћолчит_ќкликает»сполнител€–овноќдин()
    {
        var (task, chat) = await ArrangeExecutorChatAsync(silence: TimeSpan.FromMinutes(20));

        await _sut.CheckStalledExecutorAsync(task, Now);
        // ¬торой тик планировщика через полминуты Ч оклик не должен повторитьс€
        await _sut.CheckStalledExecutorAsync(_tasks.GetById(task.Id)!, Now.AddSeconds(30));

        var after = _tasks.GetById(task.Id)!;
        after.ExecutorNudgedAt.Should().Be(Now);
        after.ExecutorStaleAlertedAt.Should().BeNull("человека зовЄм только если оклик не помог");
        var nudge = Sent<UserMessageMessage>().Should().ContainSingle("оклик ровно один").Subject;
        nudge.StaffNote.Should().Be(TaskExecutionService.StaleNudgeStaffNote,
            "в ленте это плашка-разделитель, а не пузырь с сырым служебным промптом");
        nudge.Text.Should().Contain("tasks_complete").And.Contain("chats_report_up");
        (await CountNotificationsAsync(task.OwnerId!)).Should().Be(0,
            "человека на этом шаге ещЄ не трогаем");
        chat.Id.Should().Be(task.LinkedSessionId, "оклик уходит в чат самого исполнител€");
    }

    [Fact]
    public async Task CheckStalledExecutorAsync_ќкликЌеѕомог_–овноќдно”ведомление„еловеку()
    {
        var (task, _) = await ArrangeExecutorChatAsync(silence: TimeSpan.FromMinutes(40));
        _tasks.MarkExecutorNudged(task.Id, Now.AddMinutes(-20));

        await _sut.CheckStalledExecutorAsync(_tasks.GetById(task.Id)!, Now);
        await _sut.CheckStalledExecutorAsync(_tasks.GetById(task.Id)!, Now.AddSeconds(30));

        var after = _tasks.GetById(task.Id)!;
        after.ExecutorStaleAlertedAt.Should().Be(Now);
        var items = await _notifStore.GetListAsync(task.OwnerId!);
        items.Should().ContainSingle("о брошенной задаче человека зовут один раз");
        items[0].Title.Should().Be("«адача осталась в работе");
        Sent<UserMessageMessage>().Should().BeEmpty("второго оклика исполнителю быть не должно");
    }

    [Fact]
    public async Task CheckStalledExecutorAsync_ѕерезапуск»сполнител€_—брасываетќтметки—траховки()
    {
        // „еловек перезапустил исполнител€ Ч нова€ попытка получает и свой оклик, и своЄ
        // уведомление, иначе страховка молчала бы навсегда
        var (task, chat) = await ArrangeExecutorChatAsync(silence: TimeSpan.FromMinutes(40));
        _tasks.MarkExecutorNudged(task.Id, Now.AddMinutes(-20));
        await _sut.CheckStalledExecutorAsync(_tasks.GetById(task.Id)!, Now);
        _tasks.GetById(task.Id)!.ExecutorStaleAlertedAt.Should().NotBeNull();

        _tasks.MarkClaudeStarted(task.Id, chat.Id, Now);

        var after = _tasks.GetById(task.Id)!;
        after.ExecutorNudgedAt.Should().BeNull();
        after.ExecutorStaleAlertedAt.Should().BeNull();
    }
}
