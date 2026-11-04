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

    [Theory]
    [InlineData("editor", "owner", "editor")]   // создатель — участник: он и актор
    [InlineData("owner", "owner", null)]        // создатель — владелец: как раньше
    [InlineData(null, "owner", null)]           // старая запись / фон: считаем владельцем
    [InlineData("", "owner", null)]
    [InlineData("  ", "owner", null)]
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

        // человеческий путь: создатель — владелец записи, присланный актор игнорируется
        var human = _tasks.Create("p", "user-1", new CreateTaskRequest("t", CreatedByHuman: true, CreatedByActorUserId: "evil"));
        human.CreatedByUserId.Should().Be("user-1");
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
        exec.Calls.Should().Contain((byOwner.Id, true, (string?)null));
        exec.Calls.Should().Contain((legacy.Id, true, (string?)null));
    }
}
