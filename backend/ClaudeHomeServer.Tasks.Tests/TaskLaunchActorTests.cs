using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ClaudeHomeServer.Tasks.Tests;

// F-9: автозапуск исполнителя наследует актора-создателя задачи (шов ITaskExecutor несёт актора).
public class TaskLaunchActorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launch_actor_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;

    public TaskLaunchActorTests()
    {
        Directory.CreateDirectory(_dir);
        _tasks = new TaskManager(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") }).Build());
    }

    public void Dispose()
    {
        _tasks.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // После О2 финального ревью «Общих проектов» null в акторе значит «никаких прав» (общий
    // проект), поэтому создатель-владелец и legacy-запись называются именем владельца
    // буквально; безымянной остаётся только запись без владельца.
    [Theory]
    [InlineData("editor", "owner", "editor")]   // создатель — участник: он и актор
    [InlineData("owner", "owner", "owner")]     // создатель — владелец: его имя
    [InlineData(null, "owner", "owner")]        // старая запись / фон: считаем владельцем буквально
    [InlineData("", "owner", "owner")]
    [InlineData("  ", "owner", "owner")]
    [InlineData(null, "", null)]               // записи нет → актора нет (fail-closed)
    public void For_СоздательУчастникИлиВладелецИлиНеизвестен(string? creator, string owner, string? expected)
    {
        var task = new TaskItem { Title = "t", OwnerId = owner, CreatedByUserId = creator };
        TaskLaunchActor.For(task).Should().Be(expected);
    }

    [Fact]
    public void Create_АкторСервера_ПишетсяСоздателем_ТелоНеМожетПодменитьЧеловеческийПуть()
    {
        var byActor = _tasks.Create("p", "owner", new CreateTaskRequest("t", CreatedByActorUserId: " editor "));
        byActor.CreatedByUserId.Should().Be("editor");

        var plain = _tasks.Create("p", "owner", new CreateTaskRequest("t"));
        plain.CreatedByUserId.Should().BeNull("фон/система — null");

        // Человеческий путь без названного актора: создающий и есть владелец записи (личная
        // задача, свой проект). Названный актор — автор: в общем проекте запись лежит под
        // арендатором, а поставил её участник с правом WriteContent (F2-T1), поэтому автор и
        // владелец записи разные. Тело поля не касается: человеческие эндпоинты либо зануляют
        // его, либо называют id вызывающего сами (TasksController) — подменить ему нечем.
        var human = _tasks.Create("p", "user-1", new CreateTaskRequest("t", CreatedByHuman: true, CreatedByActorUserId: "evil"));
        human.CreatedByUserId.Should().Be("evil", "названный актор — автор, даже когда запись лежит под другим владельцем");

        var humanNoActor = _tasks.Create("p", "user-1", new CreateTaskRequest("t", CreatedByHuman: true));
        humanNoActor.CreatedByUserId.Should().Be("user-1", "без названного актора человек и есть владелец записи");
    }

    private sealed class RecordingExecutor : ITaskExecutor
    {
        public List<(string TaskId, bool Auto, string? Actor)> Calls { get; } = [];
        public Task ExecuteAsync(TaskItem task, bool auto, string? actorUserId)
        {
            Calls.Add((task.Id, auto, actorUserId));
            return Task.CompletedTask;
        }
        public Task CheckStalledExecutorAsync(TaskItem task, DateTime nowUtc) => Task.CompletedTask;
        public Task ReconcileFeatureQueuesAsync(string ownerId) => Task.CompletedTask;
    }

    [Fact]
    public async Task Планировщик_ПередаётСоздателяАктором_ВладельцаИLegacyКакРаньше()
    {
        var users = new Mock<IUserStore>();
        users.Setup(u => u.GetAll()).Returns(new List<User> { new() { Id = "owner", Username = "owner" } });
        var exec = new RecordingExecutor();
        var scheduler = new TaskSchedulerService(_tasks, users.Object, new Mock<ISessionBroadcaster>().Object, exec,
            new Mock<IDailyBriefingRunner>().Object, new Mock<IPersonaAutomationRunner>().Object,
            new Mock<ITaskNotificationDispatcher>().Object, NullLogger<TaskSchedulerService>.Instance);

        TaskItem Due(string title, string? creator)
        {
            var t = _tasks.Create("p", "owner", new CreateTaskRequest(title, Assignee: TaskItemAssignee.Claude,
                DueDate: "2026-07-03", DueTime: "12:00", CreatedByActorUserId: creator));
            return t;
        }
        var byEditor = Due("редактор", "editor");
        var byOwner = Due("владелец", "owner");
        var legacy = Due("legacy", null);

        await scheduler.TickAsync(new DateTime(2026, 7, 3, 12, 30, 0, DateTimeKind.Utc));

        exec.Calls.Should().Contain((byEditor.Id, true, "editor"));
        // владельца и legacy называем явно: безымянный ход общего проекта (О2) не имел бы
        // ни одного права и не мог бы выполнить свою задачу
        exec.Calls.Should().Contain((byOwner.Id, true, "owner"));
        exec.Calls.Should().Contain((legacy.Id, true, "owner"));
    }
}
