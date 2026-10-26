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
        // Задержка на git-проверке: имитация медленного дерева, чтобы проверить, что потолок
        // прогона идёт ДО них (иначе сумма git + проверки перевалила бы MCP_TOOL_TIMEOUT)
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;
        public string? Head(string worktreePath)
        {
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            return Heads.Count > 1 ? Heads.Dequeue() : Heads.Peek();
        }
        public bool? IsClean(string worktreePath) => Clean;
        public IReadOnlyList<string>? ChangedFiles(string worktreePath) => Changed;
    }

    private sealed class FakePregate : ITaskPregate
    {
        public PregateRequest? Request { get; private set; }
        public IReadOnlyList<PregateCheckResult> Checks { get; set; } = [new PregateCheckResult("tsc", PregateStatus.Green)];
        public int CeilingSeconds { get; set; } = 540;
        public Task<PregateVerdict> RunAsync(PregateRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new PregateVerdict(Checks));
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
        _tasks = new TaskManager(config);
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
        new TaskPregateService(_tasks, _projects, _reader, _pregate).RunAsync(_task.OwnerId!, _task.Id, "s", CancellationToken.None);

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

    // Потолок прогона ставится ДО git-проверок дерева: иначе сумма (git + проверки) считалась бы
    // отдельно и могла перевалить MCP_TOOL_TIMEOUT хода. Медленное дерево съедает потолок —
    // проверки тогда уже не запускаются.
    [Fact]
    public async Task ПотолокИстёкНаGitПроверках_ПроверкиНеЗапускались()
    {
        _pregate.CeilingSeconds = 1;
        _reader.Delay = TimeSpan.FromSeconds(2);

        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.NotRun);
        outcome.Text.Should().Contain("Потолок");
        _pregate.Request.Should().BeNull("проверки не запускались: потолок истёк на git-проверках");
        _tasks.GetById(_task.Id)!.Pregate.Should().BeNull();
    }

    // Потолок из шва — чужая реализация: значение вне (0; 540] в CancelAfter не годится
    // (0 — мгновенный отказ, отрицательное — исключение на ручке, больше MCP_TOOL_TIMEOUT —
    // нарушение инварианта «ответ модели должен успеть уйти»). Чистится на границе шва.
    // Задержка на git-проверке делает отказ мгновенным потолком детерминированным.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task МусорныйПотолокИзШва_ПрогонНеПадает(int ceilingSeconds)
    {
        _pregate.CeilingSeconds = ceilingSeconds;
        _reader.Delay = TimeSpan.FromMilliseconds(200);

        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.Recorded,
            "потолок из шва чистится на границе: прогон идёт, а не отменяется и не роняет ручку");
        _pregate.Request.Should().NotBeNull("проверки запустились: потолок не сорвал прогон");
    }

    // Правило границы целиком — тут: допустимое значение как есть, мусор — безопасный потолок.
    // Ожидаемый потолок берём из константы, а не вписываем числом: смена MaxSeconds не должна
    // ронять набор красным «неявно» (без объяснения, что именно изменилось).
    [Theory]
    [InlineData(1, 1)]
    [InlineData(PregateCeiling.MaxSeconds, PregateCeiling.MaxSeconds)]
    [InlineData(0, PregateCeiling.MaxSeconds)]
    [InlineData(-1, PregateCeiling.MaxSeconds)]
    [InlineData(PregateCeiling.MaxSeconds + 1, PregateCeiling.MaxSeconds)]
    public void ПотолокЧиститсяНаГраницеШва(int given, int expected)
    {
        PregateCeiling.Sanitize(given).Should().Be(expected);
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

    [Fact]
    public async Task ВсеПроверкиПропущены_ИзмененияВнеЗон_NotRunЗаписан()
    {
        _reader.Changed = ["docs/заметка.md"];
        _pregate.Checks =
        [
            new PregateCheckResult("tsc", PregateStatus.Skipped, "изменения не касаются проверки"),
            new PregateCheckResult("build", PregateStatus.Skipped, "изменения не касаются проверки"),
        ];

        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.Recorded);
        outcome.Text.Should().Contain("NotRun");
        var record = _tasks.GetById(_task.Id)!.Pregate!;
        record.Summary.Should().Contain("NotRun");
        record.NothingApplicable.Should().BeTrue();
        record.Green.Should().BeFalse();
        _tasks.Update(_task.Id, new UpdateTaskRequest(Status: TaskItemStatus.Done), isAgentCall: true)!
            .Status.Should().Be(TaskItemStatus.Done);
    }

    [Fact]
    public async Task ПроверкаБезЗамера_ЗелёныйНеЗаписывается_ПроверкаЗелёная_НеСпасает()
    {
        _pregate.Checks =
        [
            new PregateCheckResult("tsc", PregateStatus.Green),
            new PregateCheckResult("ui_probe 360/1280", PregateStatus.NotRun, "адрес стенда (Origin) не задан в конфиге Pregate"),
        ];

        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.NotRun);
        outcome.Text.Should().Contain("ui_probe 360/1280").And.Contain("Origin").And.Contain("результат не записан");
        _tasks.GetById(_task.Id)!.Pregate.Should().BeNull();
    }

    [Fact]
    public async Task КрасноеПерекрываетБезЗамера_КрасныйЗаписывается()
    {
        _pregate.Checks =
        [
            new PregateCheckResult("tsc", PregateStatus.Red, "error TS2322"),
            new PregateCheckResult("ui_probe", PregateStatus.NotRun, "Origin не задан"),
        ];

        (await Run()).Status.Should().Be(TaskPregateStatus.Recorded);
        _tasks.GetById(_task.Id)!.Pregate!.Green.Should().BeFalse();
    }

    [Fact]
    public async Task ПослеДвухКрасныхПодряд_ПредгейтЗапускаетсяСнова()
    {
        _pregate.Checks = [new PregateCheckResult("tsc", PregateStatus.Red, "error TS2322")];
        await Run();
        await Run();
        _tasks.GetById(_task.Id)!.Pregate!.RedStreak.Should().Be(2);

        _pregate.Checks = [new PregateCheckResult("tsc", PregateStatus.Green)];
        var outcome = await Run();

        outcome.Status.Should().Be(TaskPregateStatus.Recorded, "третьего отказа «серия исчерпана» больше нет");
        _tasks.GetById(_task.Id)!.Pregate!.Green.Should().BeTrue();
    }

    // Эскалация после второго красного — одна точка, серверная (PregateEscalated → ReportUpAsync).
    // Текст вердикта не велит исполнителю звать chats_report_up: тогда красный предгейт
    // эскалировался бы дважды (сервером и исполнителем).
    [Fact]
    public async Task ВторойКрасныйПодряд_ТекстНеВелитЭскалироватьСамому()
    {
        _pregate.Checks = [new PregateCheckResult("tsc", PregateStatus.Red, "error TS2322")];
        await Run();

        var outcome = await Run();

        outcome.Text.Should().Contain("Второй красный подряд");
        outcome.Text.Should().NotContain("chats_report_up",
            "эскалация серверная: ещё и призыв исполнителю звать её — эскалация в двух точках");
    }

    [Fact]
    public async Task ЧастьПроверокПропущенаАЧастьЗелёная_ЗелёныйЗаписывается()
    {
        _pregate.Checks =
        [
            new PregateCheckResult("tsc", PregateStatus.Skipped, "x"),
            new PregateCheckResult("build", PregateStatus.Green),
        ];

        (await Run()).Status.Should().Be(TaskPregateStatus.Recorded);
        _tasks.GetById(_task.Id)!.Pregate!.Green.Should().BeTrue();
    }

    [Fact]
    public async Task ПустойНаборПроверок_NothingApplicableБезGreen()
    {
        _pregate.Checks = [];

        (await Run()).Status.Should().Be(TaskPregateStatus.Recorded);
        var record = _tasks.GetById(_task.Id)!.Pregate!;
        record.Green.Should().BeFalse();
        record.NothingApplicable.Should().BeTrue();
    }

    [Fact]
    public async Task МаршрутыИзПостановки_ПередаютсяВПрогон()
    {
        _tasks.Update(_task.Id, new UpdateTaskRequest(Description: "Сделать.\nUI-маршруты: /#/ui-kit, /#/features\nконец"));

        await Run();

        _pregate.Request!.UiRoutes.Should().Equal("/#/ui-kit", "/#/features");
    }

    [Fact]
    public void РазборМаршрутов_ТолькоПослеМаркера()
    {
        PregateRules.UiRoutes("UI-маршруты: /a, /b ,, /c").Should().Equal("/a", "/b", "/c");
        PregateRules.UiRoutes("пишем про /#/ui-kit без маркера").Should().BeEmpty();
        PregateRules.UiRoutes("UI-маркеры: /a").Should().BeEmpty("имя маркера точное");
        PregateRules.UiRoutes(null).Should().BeEmpty();
    }
}
