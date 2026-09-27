using System.Reflection;
using ClaudeHomeServer.Controllers;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Knowledge;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Skills;
using ClaudeHomeServer.Services.Tasks;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tests.Services;

// ADR-016, вариант А плана §5: разовая фоновая работа по локальному проекту при офлайн-
// устройстве не падает и не теряется — исполнитель задачи и сообщение в очереди чата встают
// в видимое «ждёт устройство» на самой сущности, стартуют при выходе устройства в онлайн,
// через 24 ч снимаются с уведомлением. Онлайн-статус — фейковый гейт, CLI не поднимается.
public class TaskExecutionServiceDeviceWaitTests : IDisposable
{
    private DeviceWaitTestHost H { get; } = new();
    private TaskManager _tasks => H._tasks;
    private UserStore _userStore => H._userStore;
    private ProjectManager _projects => H._projects;
    private SessionManager _sessions => H._sessions;
    private NotificationStore _notifStore => H._notifStore;
    private NotificationService _notif => H._notif;
    private TaskExecutionService _sut => H._sut;
    private FakeProjectDeviceGate _gate => H._gate;
    private IProjectManager _projectSeam => H._projectSeam;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        H.Dispose();
    }

    private (User Owner, Project Project) LocalProject() => H.LocalProject();
    private TaskItem ClaudeTask(User owner, Project project) => H.ClaudeTask(owner, project);
    private Task<IReadOnlyList<string>> NotificationTitlesAsync(string ownerId) => H.NotificationTitlesAsync(ownerId);

    // --- Исполнитель задачи ------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_УстройствоОфлайн_ЗадачаЖдётБезЗапуска()
    {
        var (owner, project) = LocalProject();
        var task = ClaudeTask(owner, project);

        var result = await _sut.ExecuteAsync(task, auto: true);

        result.DeviceWaitSince.Should().NotBeNull("видимое состояние «ждёт устройство» хранится на задаче");
        result.DeviceWaitReason.Should().Be(ProjectCapabilities.DeviceOfflineReason);
        result.ClaudeStartedAt.Should().BeNull("запуска не было — упавший старт не должен выглядеть начатым");
        result.LinkedSessionId.Should().BeNull();
        result.Status.Should().Be(TaskItemStatus.Todo);
        _sessions.GetByProject(project.Id).Should().BeEmpty("чат-исполнитель не создаётся впустую");
        (await NotificationTitlesAsync(owner.Id)).Should().ContainSingle(t => t == "Задача ждёт устройство");
    }

    [Fact]
    public async Task ExecuteAsync_ПовторПриОфлайне_ОтметкуНеСдвигаетИНеСпамит()
    {
        var (owner, project) = LocalProject();
        var task = ClaudeTask(owner, project);
        var first = await _sut.ExecuteAsync(task, auto: true);
        var since = first.DeviceWaitSince;

        var second = await _sut.ExecuteAsync(_tasks.GetById(task.Id)!, auto: false);

        second.DeviceWaitSince.Should().Be(since, "потолок считается от первой постановки в ожидание");
        (await NotificationTitlesAsync(owner.Id)).Count(t => t == "Задача ждёт устройство").Should().Be(1);
    }

    [Fact]
    public void ShouldAutoStart_ЗадачаЖдётУстройство_ПланировщикНеДёргает()
    {
        var due = DateTime.UtcNow.AddMinutes(-5);
        var task = new TaskItem
        {
            Title = "t", Assignee = TaskItemAssignee.Claude, Status = TaskItemStatus.Todo,
            DueDate = due.ToString("yyyy-MM-dd"), DueTime = due.ToString("HH:mm"),
            DeviceWaitSince = DateTime.UtcNow.AddMinutes(-1),
        };

        TaskSchedulerService.ShouldAutoStart(task, TimeZoneInfo.Utc, DateTime.UtcNow)
            .Should().BeFalse("ждущую задачу запускает диспетчер онлайна, а не тик по сроку");
        task.DeviceWaitSince = null;
        TaskSchedulerService.ShouldAutoStart(task, TimeZoneInfo.Utc, DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task OnDeviceOnline_ЖдавшаяЗадачаЗапускается()
    {
        var (owner, project) = LocalProject();
        var task = ClaudeTask(owner, project);
        await _sut.ExecuteAsync(task, auto: true);

        _gate.GoOnline();
        await _sut.OnDeviceOnlineAsync(owner.Id, "dev-1");

        var started = _tasks.GetById(task.Id)!;
        started.DeviceWaitSince.Should().BeNull();
        started.ClaudeStartedAt.Should().NotBeNull("устройство в сети — исполнитель стартовал");
        started.LinkedSessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task OnDeviceOnline_ДругоеУстройство_ЗадачаЖдётДальше()
    {
        var (owner, project) = LocalProject();
        var task = ClaudeTask(owner, project);
        await _sut.ExecuteAsync(task, auto: true);

        _gate.GoOnline();
        await _sut.OnDeviceOnlineAsync(owner.Id, "dev-other");

        _tasks.GetById(task.Id)!.ClaudeStartedAt.Should().BeNull();
        _tasks.GetById(task.Id)!.DeviceWaitSince.Should().NotBeNull();
    }

    [Fact]
    public async Task Sweep_ПотолокИстёк_ОстановкаСПричинойУведомлениеИКарточкаШтаба()
    {
        var (owner, project) = LocalProject();
        var task = ClaudeTask(owner, project);
        await _sut.ExecuteAsync(task, auto: true);
        TaskItem? notLaunched = null;
        _sut.TeamTaskNotLaunched = (t, _) => { notLaunched = t; return Task.CompletedTask; };

        // До потолка — ждём дальше, устройство всё ещё офлайн
        await _sut.SweepAsync(DateTime.UtcNow.AddHours(23));
        _tasks.GetById(task.Id)!.ExecutorStoppedAt.Should().BeNull();

        await _sut.SweepAsync(DateTime.UtcNow + ProjectCapabilities.DeviceWaitCeiling + TimeSpan.FromMinutes(1));

        var expired = _tasks.GetById(task.Id)!;
        expired.DeviceWaitSince.Should().BeNull();
        expired.ExecutorStoppedAt.Should().NotBeNull();
        expired.ExecutorStopReason.Should().Be(ExecutorStopClassifier.DeviceWaitExpiredReason);
        expired.ClaudeStartedAt.Should().BeNull();
        (await NotificationTitlesAsync(owner.Id)).Should().Contain("Исполнитель остановился");
        notLaunched.Should().NotBeNull("под-задача штаба получает карточку, а не тишину");
    }

    [Fact]
    public async Task Sweep_УстройствоВернулосьБезСобытия_ДогоняетЗапуск()
    {
        var (owner, project) = LocalProject();
        var task = ClaudeTask(owner, project);
        await _sut.ExecuteAsync(task, auto: true);

        _gate.GoOnline();
        await _sut.SweepAsync(DateTime.UtcNow);

        _tasks.GetById(task.Id)!.ClaudeStartedAt.Should().NotBeNull();
    }

    // --- Очередь сообщений чата ---------------------------------------------------

    private async Task<(Session Chat, TaskCompletionSource<string> Delivered)> LocalChatAsync(Project project)
    {
        var chat = await _sessions.CreateAsync(project.Id, ClaudeMode.AcceptEdits, name: "Локальный чат");
        return (chat, H.StubProcess(chat));
    }

    [Fact]
    public async Task ОчередьЧата_УстройствоОфлайн_СообщениеЖдётНаСессииИВидноВОчереди()
    {
        var (_, project) = LocalProject();
        var (chat, delivered) = await LocalChatAsync(project);

        var result = await _sessions.SendMessageAndWaitAsync(chat.Id, "⏰ Сторож «Билд»: условие выполнено.",
            TimeSpan.Zero, agentDepth: 1);

        result.Should().BeOfType<SendAndWaitResult.Queued>();
        delivered.Task.IsCompleted.Should().BeFalse("хода на офлайн-устройстве нет");
        _sessions.GetById(chat.Id)!.DeviceWaitQueue.Should().ContainSingle()
            .Which.Text.Should().Contain("Сторож");
        _sessions.GetVisiblePending(chat.Id).Should().ContainSingle(p => p.WaitingForDevice);
    }

    [Fact]
    public async Task ОчередьЧата_ФоновыйХодПриОфлайне_ПаркуетсяАНеПадает()
    {
        var (_, project) = LocalProject();
        var (chat, delivered) = await LocalChatAsync(project);

        await _sessions.SendMessageAsync(chat.Id, "Постановка исполнителю", [], auto: true);

        delivered.Task.IsCompleted.Should().BeFalse();
        _sessions.GetById(chat.Id)!.DeviceWaitQueue.Should().ContainSingle()
            .Which.Route.Should().Be(DeviceWaitMessage.RouteAuto);
    }

    [Fact]
    public async Task ОчередьЧата_ВыходУстройстваВОнлайн_СообщениеУходитВРаботу()
    {
        var (owner, project) = LocalProject();
        var (chat, delivered) = await LocalChatAsync(project);
        await _sessions.SendMessageAndWaitAsync(chat.Id, "Отчёт исполнителя", TimeSpan.Zero, agentDepth: 1);
        var handler = new ChatDeviceWaitHandler(_sessions, _projectSeam, _gate, _notif,
            NullLogger<ChatDeviceWaitHandler>.Instance);

        _gate.GoOnline();
        await handler.OnDeviceOnlineAsync(owner.Id, "dev-1");

        (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Contain("Отчёт исполнителя");
        _sessions.GetById(chat.Id)!.DeviceWaitQueue.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task ОчередьЧата_ПотолокИстёк_СообщениеСнятоСУведомлением()
    {
        var (owner, project) = LocalProject();
        var (chat, delivered) = await LocalChatAsync(project);
        await _sessions.SendMessageAndWaitAsync(chat.Id, "Отчёт исполнителя", TimeSpan.Zero, agentDepth: 1);
        var handler = new ChatDeviceWaitHandler(_sessions, _projectSeam, _gate, _notif,
            NullLogger<ChatDeviceWaitHandler>.Instance);

        await handler.SweepAsync(DateTime.UtcNow.AddHours(1));
        _sessions.GetById(chat.Id)!.DeviceWaitQueue.Should().ContainSingle("до потолка сообщение ждёт");

        await handler.SweepAsync(DateTime.UtcNow + ProjectCapabilities.DeviceWaitCeiling + TimeSpan.FromMinutes(1));

        _sessions.GetById(chat.Id)!.DeviceWaitQueue.Should().BeNullOrEmpty();
        delivered.Task.IsCompleted.Should().BeFalse();
        (await NotificationTitlesAsync(owner.Id)).Should().Contain("Сообщения не доставлены");
    }

    [Fact]
    public async Task ОчередьЧата_ОтменаКрестиком_СнимаетЖдущееУстройство()
    {
        var (_, project) = LocalProject();
        var (chat, _) = await LocalChatAsync(project);
        await _sessions.SendMessageAndWaitAsync(chat.Id, "Отчёт исполнителя", TimeSpan.Zero, agentDepth: 1);
        var id = _sessions.GetById(chat.Id)!.DeviceWaitQueue!.Single().Id;

        (await _sessions.CancelPendingAsync(chat.Id, id)).Should().BeTrue();

        _sessions.GetById(chat.Id)!.DeviceWaitQueue.Should().BeNullOrEmpty();
    }
}
