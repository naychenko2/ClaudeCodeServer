using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// S2b-3a: напоминание идёт человеку-исполнителю, а «мои задачи»/календарь по общим
// проектам показывают участнику только назначенное ему.
public class SharedProjectTaskRoutingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);
    private const string Owner = "owner";
    private const string Editor = "editor";
    private const string Reader = "reader";
    private const string Stranger = "stranger";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "s2b3a_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly Project _shared = new()
    {
        Id = "p-shared",
        OwnerId = Owner,
        Members = [new() { UserId = Editor, Role = ProjectRole.Editor }, new() { UserId = Reader, Role = ProjectRole.Reader }],
    };

    public SharedProjectTaskRoutingTests()
    {
        Directory.CreateDirectory(_dir);
        _tasks = new TaskManager(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build());
    }

    public void Dispose()
    {
        _tasks.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private TaskItem NewTask(string title, string? projectId, string? assigneeUserId = null,
        TaskItemAssignee? assignee = null, bool reminder = false)
    {
        var t = _tasks.Create(projectId, Owner, new CreateTaskRequest(title, Assignee: assignee,
            DueDate: "2026-07-03", DueTime: "12:00", ReminderMinutes: reminder ? 0 : null));
        t.AssigneeUserId = assigneeUserId;
        return t;
    }

    private MyTasksQuery Query()
    {
        var projects = new Mock<IProjectManager>();
        projects.Setup(p => p.GetAll()).Returns([_shared]);
        var access = new Mock<IProjectAccess>();
        access.Setup(a => a.Resolve(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<ProjectRight>()))
            .Returns((string? user, string? pid, ProjectRight right) =>
                pid == _shared.Id && ProjectAccess.Can(_shared, user, right) && ProjectAccess.Check(_shared, user) is { } role
                    ? new RequestScope(user!, _shared.OwnerId!, role)
                    : null);
        return new MyTasksQuery(_tasks, projects.Object, access.Object);
    }

    // ─── Календарь / «мои задачи» ───────────────────────────────────────────

    [Fact]
    public void ForUser_УчастникВидитНазначенноеЕмуИСвоёЛичное_ЧужоеНет()
    {
        var mine = NewTask("мне", _shared.Id, Editor);
        var ownerOwn = NewTask("владельца", _shared.Id); // исполнитель — владелец
        var claudeTask = NewTask("клода", _shared.Id, Editor, TaskItemAssignee.Claude);
        var personalOfOwner = NewTask("личная владельца", null);
        var editorPersonal = _tasks.Create(null, Editor, new CreateTaskRequest("личная участника"));

        var ids = Query().ForUser(Editor).Select(t => t.Id).ToList();

        ids.Should().BeEquivalentTo([mine.Id, editorPersonal.Id]);
        ids.Should().NotContain([ownerOwn.Id, claudeTask.Id, personalOfOwner.Id]);
    }

    [Fact]
    public void ForUser_Посторонний_НичегоИзОбщегоПроекта()
    {
        NewTask("мне?", _shared.Id, Stranger);

        Query().ForUser(Stranger).Should().BeEmpty();
    }

    [Fact]
    public void ForUser_ЧитательСНазначением_ВидитТолькоСвоё()
    {
        var mine = NewTask("читателю", _shared.Id, Reader);
        NewTask("редактору", _shared.Id, Editor);

        Query().ForUser(Reader).Select(t => t.Id).Should().BeEquivalentTo([mine.Id]);
    }

    [Fact]
    public void ForUser_Владелец_ВидитВсёКакРаньше()
    {
        var a = NewTask("а", _shared.Id, Editor);
        var b = NewTask("б", null);

        Query().ForUser(Owner).Select(t => t.Id).Should().BeEquivalentTo([a.Id, b.Id]);
    }

    [Fact]
    public void ForUser_БывшийУчастник_НичегоНеВидит()
    {
        NewTask("бывшему", _shared.Id, Editor);
        _shared.Members = [new() { UserId = Reader, Role = ProjectRole.Reader }];

        Query().ForUser(Editor).Should().BeEmpty();
    }

    // ─── Напоминания ────────────────────────────────────────────────────────

    private static (TaskSchedulerService Svc, List<string> Sent) Scheduler(
        TaskManager tasks, params string[] users)
    {
        var sent = new List<string>();
        var userStore = new Mock<IUserStore>();
        userStore.Setup(u => u.GetAll()).Returns(users.Select(id => new User { Id = id, Username = id }).ToList());
        var notif = new Mock<ITaskNotificationDispatcher>();
        notif.Setup(n => n.SendNotificationMessageAsync(It.IsAny<string>(), It.IsAny<NotificationMessage>(), It.IsAny<bool>()))
            .Returns((string uid, NotificationMessage _, bool _) => { lock (sent) sent.Add(uid); return Task.CompletedTask; });
        var svc = new TaskSchedulerService(tasks, userStore.Object, new Mock<ISessionBroadcaster>().Object,
            new Mock<ITaskExecutor>().Object, new Mock<IDailyBriefingRunner>().Object,
            new Mock<IPersonaAutomationRunner>().Object, notif.Object, NullLogger<TaskSchedulerService>.Instance);
        return (svc, sent);
    }

    [Fact]
    public async Task Напоминание_ОбщийПроект_УходитИсполнителюАНеВладельцу()
    {
        NewTask("участнику", _shared.Id, Editor, reminder: true);
        var (svc, sent) = Scheduler(_tasks, Owner);

        await svc.TickAsync(Now);

        sent.Should().Equal(Editor);
    }

    [Fact]
    public async Task Напоминание_ЛичнаяИClaudeЗадачи_ПрежнееПоведение_Владельцу()
    {
        NewTask("личная", null, reminder: true);
        NewTask("клоду", _shared.Id, Editor, TaskItemAssignee.Claude, reminder: true);
        var (svc, sent) = Scheduler(_tasks, Owner);

        await svc.TickAsync(Now);

        sent.Should().Equal(Owner, Owner);
    }

    [Fact]
    public async Task Напоминание_ИсполнительВладелец_ОдноУведомление()
    {
        NewTask("владельцу", _shared.Id, reminder: true);
        var (svc, sent) = Scheduler(_tasks, Owner);

        await svc.TickAsync(Now);

        sent.Should().Equal(Owner);
    }

    // Конкурентность: два тика одновременно — напоминание ровно одно. Мутация: убрать
    // lock (_reminderLock) вокруг ShouldRemind+MarkReminderSent — тест краснеет.
    [Fact]
    public async Task Напоминание_ДваТикаОдновременно_УходитРовноОдин()
    {
        const int rounds = 200;
        for (var i = 0; i < rounds; i++)
            NewTask("гонка" + i, _shared.Id, Editor, reminder: true);
        var (svc, sent) = Scheduler(_tasks, Owner);
        var start = new TaskCompletionSource();

        async Task Tick() { await start.Task; await svc.TickAsync(Now); }
        var a = Task.Run(Tick);
        var b = Task.Run(Tick);
        start.SetResult();
        await Task.WhenAll(a, b);

        sent.Should().HaveCount(rounds);
        sent.Should().OnlyContain(u => u == Editor);
    }
}
