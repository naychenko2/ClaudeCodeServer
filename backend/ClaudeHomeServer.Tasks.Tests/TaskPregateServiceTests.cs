using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Запуск предгейта шага (NC-P1b): чистое дерево до прогона, тот же HEAD после, набор изменённых файлов
public class TaskPregateServiceTests : IDisposable
{
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";

    private sealed class HostEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class Reader : IWorktreeHeadReader
    {
        public Queue<string?> Heads { get; } = new();
        public bool? Clean { get; set; } = true;
        public IReadOnlyList<string>? Changed { get; set; } = ["frontend/a.tsx"];
        public string? Head(string worktreePath) => Heads.Count > 1 ? Heads.Dequeue() : Heads.Peek();
        public bool? IsClean(string worktreePath) => Clean;
        public IReadOnlyList<string>? ChangedFiles(string worktreePath) => Changed;
    }

    private sealed class FakePregate : ITaskPregate
    {
        public PregateRequest? Request { get; private set; }
        public Task<PregateVerdict> RunAsync(PregateRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new PregateVerdict([new PregateCheckResult("tsc", PregateStatus.Green)]));
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "task_pregate_" + Guid.NewGuid().ToString("N"));
    private readonly TaskManager _tasks;
    private readonly ProjectManager _projects;
    private readonly Reader _reader = new();
    private readonly FakePregate _pregate = new();
    private readonly TaskItem _task;

    public TaskPregateServiceTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_dir, "projects.json"),
            ["DefaultProjectsPath"] = _dir,
            ["Backup:Path"] = Path.Combine(_dir, "archives"),
            ["Backup:SecretsPath"] = Path.Combine(_dir, "secrets"),
            ["Sandbox:ContainerName"] = "",
        }).Build();
        var users = new UserStore(config, new HostEnv(), NullLogger<UserStore>.Instance);
        var owner = users.Add("pg_" + Guid.NewGuid().ToString("N")[..8], "pwd", "admin");
        _projects = new ProjectManager(config, users, new AppSettingsService(config));
        var root = Path.Combine(_dir, "proj");
        Directory.CreateDirectory(root);
        var project = _projects.Create("п", root, owner.Id, "tester");
        _tasks = new TaskManager(config, heads: _reader);
        var epic = _tasks.Create(project.Id, owner.Id, new CreateTaskRequest("эпик", Role: TaskRoles.Feature));
        _task = _tasks.Create(project.Id, owner.Id, new CreateTaskRequest("шаг", FeatureId: epic.Id, Role: TaskRoles.Step,
            StepRef: "B1", WorktreePath: _dir));
        _reader.Heads.Enqueue(Sha1);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task<TaskPregateOutcome> Run() =>
        new TaskPregateService(_tasks, _projects, _reader, _pregate).RunAsync(_task.OwnerId, _task.Id, "s", CancellationToken.None);

    [Fact]
    public async Task ГрязноеДерево_ПрогонНеНачат_РезультатНеЗаписан()
    {
        _reader.Clean = false;

        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.NotRun);
        outcome.Text.Should().Contain("незакоммиченные");
        _pregate.Request.Should().BeNull("проверки по рабочему дереву не запускались");
        _tasks.GetById(_task.Id)!.Pregate.Should().BeNull();
    }

    [Fact]
    public async Task СтатусДереваНеПрочитан_ПрогонНеНачат()
    {
        _reader.Clean = null;

        (await Run()).Status.Should().Be(TaskPregateStatus.NotRun);
        _pregate.Request.Should().BeNull();
    }

    [Fact]
    public async Task ЧистоеДерево_ПрогонПолучаетИзменённыеФайлы_РезультатПривязанКSha()
    {
        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.Recorded);
        _pregate.Request!.ChangedFiles.Should().Equal("frontend/a.tsx");
        _tasks.GetById(_task.Id)!.Pregate!.Sha.Should().Be(Sha1);
    }

    [Fact]
    public async Task HeadСменилсяВоВремяПрогона_РезультатНеЗаписан()
    {
        _reader.Heads.Enqueue(Sha2);

        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.NotRun);
        outcome.Text.Should().Contain("сменился");
        _tasks.GetById(_task.Id)!.Pregate.Should().BeNull();
    }
}
