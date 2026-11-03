using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S3-3: журнал правок человека коммитит ТОЛЬКО записанные пути с автором-человеком, после окна
// дебаунса. Настоящий git во временной папке (по образцу GitAutoCommitAuthorTests); время —
// управляемые часы, поэтому окно проверяется без Task.Delay.
public class HumanEditCommitterTests : IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    // Лог-приёмник для проверок «потеря названа в логе» (QA-D1: Warning об игнорируемом
    // пути журнала вместо тихой пустоты).
    private sealed class RecordingLogger<T>(List<(LogLevel Level, string Message)> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => sink.Add((logLevel, formatter(state, exception)));
    }

    private readonly string _dir;
    private readonly string _repo;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly GitService _git = new(new LocalOnlyFactory());
    private readonly ManualClock _clock = new();
    private readonly HumanEditCommitter _sut;

    public HumanEditCommitterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "humanedit_" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_dir, "projects.json"),
                ["HumanEdit:DebounceSeconds"] = "20",
            })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
        _sut = new HumanEditCommitter(_projects, _users, _git, _config, NullLogger<HumanEditCommitter>.Instance, _clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // Репозиторий с базовым коммитом; shared=true — проекту добавлен участник. Возвращает проект
    // и человека, который «правит» (участник общего проекта либо владелец личного).
    // gitignoreContent — чужой .gitignore, записанный ДО InitAsync (тот пишет шаблон только
    // когда файла нет): тесты «свой файл не трогается» строятся на нём.
    private async Task<(Project Project, User Human)> SetupAsync(bool shared, bool initGit = true, string? gitignoreContent = null)
    {
        if (initGit)
        {
            if (gitignoreContent is not null)
                await File.WriteAllTextAsync(Path.Combine(_repo, ".gitignore"), gitignoreContent);
            await _git.InitAsync(null, _repo);
            await _git.RunAsync(null, _repo, ["config", "user.email", "test@test"]);
            await _git.RunAsync(null, _repo, ["config", "user.name", "Тест"]);
            await File.WriteAllTextAsync(Path.Combine(_repo, "base.txt"), "база\n");
            await _git.StageAllAsync(null, _repo);
            await _git.CommitAsync(null, _repo, "начальный коммит");
        }
        var ownerId = _users.GetFirst()!.Id;
        var project = _projects.Create("правка-" + Guid.NewGuid().ToString("N")[..6], _repo, ownerId, "testuser");
        var human = _users.Add("human-" + Guid.NewGuid().ToString("N")[..6], "password", "user");
        if (shared)
            _projects.SetMembers(project.Id,
                [new ProjectMember { UserId = human.Id, Role = ProjectRole.Editor, AddedBy = ownerId }]);
        return (_projects.GetById(project.Id)!, human);
    }

    private async Task<string> LogAsync() =>
        (await _git.RunAsync(null, _repo, ["log", "--format=%an|%s|%(trailers:key=CCS-User,valueonly)", "--no-merges"])).Stdout;

    private async Task<string> CommitsCountAsync() =>
        (await _git.RunAsync(null, _repo, ["rev-list", "--count", "HEAD"])).Stdout.Trim();

    // Автор технического коммита .gitignore — владелец (тот же порядок, что в прод-вызовах
    // ProjectMembershipService/HumanEditCommitter).
    private GitCommitAuthor OwnerAuthor(Project project)
    {
        var owner = _users.GetById(project.OwnerId!)!;
        return new(owner.Id, owner.DisplayName ?? owner.Username, owner.Username);
    }

    [Fact]
    public async Task Flush_коммитит_только_записанный_путь_с_автором_человеком()
    {
        var (project, human) = await SetupAsync(shared: true);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "правка человека\n");
        // «Работа агента» в том же дереве: и незакоммиченный файл, и staged — в коммит попасть не должны
        await File.WriteAllTextAsync(Path.Combine(_repo, "agent.txt"), "агент\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "agent-staged.txt"), "агент в индексе\n");
        await _git.StageAsync(null, _repo, "agent-staged.txt");

        _sut.Record(project.Id, _repo, "mine.md", human.Id);
        _clock.Advance(TimeSpan.FromSeconds(21));
        await _sut.FlushAsync(all: false);

        var files = (await _git.RunAsync(null, _repo, ["show", "--name-only", "--format=", "HEAD"])).Stdout.Trim();
        files.Should().Be("mine.md");
        var log = await LogAsync();
        log.Should().Contain(human.DisplayName ?? human.Username).And.Contain(human.Id);
        // Работа агента осталась на месте: файл не закоммичен, индекс не тронут
        var status = (await _git.RunAsync(null, _repo, ["status", "--porcelain"])).Stdout;
        status.Should().Contain("?? agent.txt").And.Contain("A  agent-staged.txt");
    }

    [Fact]
    public async Task Flush_не_трогает_запись_до_конца_окна_и_PendingAuthor_её_показывает()
    {
        var (project, human) = await SetupAsync(shared: true);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");
        var before = await CommitsCountAsync();

        _sut.Record(project.Id, _repo, "mine.md", human.Id);
        _clock.Advance(TimeSpan.FromSeconds(19));
        await _sut.FlushAsync(all: false);

        (await CommitsCountAsync()).Should().Be(before);
        _sut.PendingAuthor(_repo, "mine.md")!.UserId.Should().Be(human.Id);

        // Ровно на границе окна (>= debounce) — срабатывает
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _sut.FlushAsync(all: false);
        (await CommitsCountAsync()).Should().NotBe(before);
        _sut.PendingAuthor(_repo, "mine.md").Should().BeNull();
    }

    [Fact]
    public async Task Новый_Record_сдвигает_окно_и_последний_записавший_становится_автором()
    {
        var (project, human) = await SetupAsync(shared: true);
        var second = _users.Add("second-" + Guid.NewGuid().ToString("N")[..6], "password", "user");
        _projects.SetMembers(project.Id,
        [
            new ProjectMember { UserId = human.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId! },
            new ProjectMember { UserId = second.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId! },
        ]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");

        _sut.Record(project.Id, _repo, "mine.md", human.Id);
        _clock.Advance(TimeSpan.FromSeconds(15));
        _sut.Record(project.Id, _repo, "mine.md", second.Id);
        _clock.Advance(TimeSpan.FromSeconds(15));
        await _sut.FlushAsync(all: false); // с первой записи прошло 30 с, со второй — 15

        _sut.PendingAuthor(_repo, "mine.md")!.UserId.Should().Be(second.Id);
        _clock.Advance(TimeSpan.FromSeconds(5));
        await _sut.FlushAsync(all: false);
        (await LogAsync()).Should().Contain(second.Id).And.NotContain(human.Id);
    }

    [Fact]
    public async Task StopAsync_сбрасывает_буфер_не_дожидаясь_окна()
    {
        var (project, human) = await SetupAsync(shared: true);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");
        _sut.Record(project.Id, _repo, "mine.md", human.Id);

        await _sut.StopAsync(CancellationToken.None);

        (await LogAsync()).Should().Contain(human.Id);
        _sut.PendingAuthor(_repo, "mine.md").Should().BeNull();
    }

    [Fact]
    public async Task Личный_проект_и_не_репозиторий_молча_игнорируются()
    {
        var (personal, human) = await SetupAsync(shared: false);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");
        var before = await CommitsCountAsync();

        _sut.Record(personal.Id, _repo, "mine.md", human.Id);
        _sut.PendingAuthor(_repo, "mine.md").Should().BeNull();
        await _sut.StopAsync(CancellationToken.None);
        (await CommitsCountAsync()).Should().Be(before);

        // Общий проект, но каталог — не git-репозиторий
        var plain = Path.Combine(_dir, "plain");
        Directory.CreateDirectory(plain);
        var shared = _projects.Create("без-git-" + Guid.NewGuid().ToString("N")[..6], plain, personal.OwnerId!, "testuser");
        _projects.SetMembers(shared.Id, [new ProjectMember { UserId = human.Id, Role = ProjectRole.Editor, AddedBy = personal.OwnerId! }]);
        var act = () => _sut.Record(shared.Id, plain, "f.md", human.Id);
        act.Should().NotThrow();
        _sut.PendingAuthor(plain, "f.md").Should().BeNull();
    }

    [Fact]
    public async Task Путь_вне_корня_не_попадает_в_буфер()
    {
        var (project, human) = await SetupAsync(shared: true);
        _sut.Record(project.Id, _repo, "../outside.md", human.Id);
        _sut.PendingAuthor(_repo, "../outside.md").Should().BeNull();
    }

    [Fact]
    public async Task Путь_каталог_не_попадает_в_буфер_и_работа_агента_не_уезжает_в_коммит()
    {
        var (project, human) = await SetupAsync(shared: true);
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_repo, "docs", "agent.md"), "агент\n");
        var before = await CommitsCountAsync();

        _sut.Record(project.Id, _repo, "docs", human.Id);
        _sut.PendingAuthor(_repo, "docs").Should().BeNull();
        await _sut.StopAsync(CancellationToken.None);

        (await CommitsCountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Удалённый_файл_принимается_как_правка()
    {
        var (project, human) = await SetupAsync(shared: true);
        File.Delete(Path.Combine(_repo, "base.txt"));

        _sut.Record(project.Id, _repo, "base.txt", human.Id);
        _sut.PendingAuthor(_repo, "base.txt")!.UserId.Should().Be(human.Id);
        await _sut.StopAsync(CancellationToken.None);

        (await LogAsync()).Should().Contain(human.Id);
    }

    [Fact]
    public async Task Чужой_корень_не_попадает_в_буфер_и_не_коммитится()
    {
        var (project, human) = await SetupAsync(shared: true);
        var foreign = Path.Combine(_dir, "foreign");
        Directory.CreateDirectory(foreign);
        await _git.InitAsync(null, foreign);
        await _git.RunAsync(null, foreign, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, foreign, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(foreign, "x.md"), "чужое\n");

        _sut.Record(project.Id, foreign, "x.md", human.Id);
        _sut.PendingAuthor(foreign, "x.md").Should().BeNull();
        await _sut.StopAsync(CancellationToken.None);

        var count = await _git.RunAsync(null, foreign, ["rev-list", "--all", "--count"]);
        count.Stdout.Trim().Should().Be("0");
    }

    [Fact]
    public async Task Корень_ниже_корня_проекта_принимается_и_коммитится_в_свой_репозиторий()
    {
        var (project, human) = await SetupAsync(shared: true);
        var sub = Path.Combine(_repo, "sub");
        Directory.CreateDirectory(sub);
        await _git.InitAsync(null, sub);
        await _git.RunAsync(null, sub, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, sub, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(sub, "x.md"), "правка\n");

        _sut.Record(project.Id, sub, "x.md", human.Id);
        _sut.PendingAuthor(sub, "x.md").Should().NotBeNull();
        await _sut.StopAsync(CancellationToken.None);

        var log = await _git.RunAsync(null, sub, ["log", "--format=%an|%s|%(trailers:key=CCS-User,valueonly)"]);
        log.Stdout.Should().Contain(human.Id);
    }

    [Fact]
    public async Task Префикс_сосед_корня_проекта_не_попадает_в_буфер_и_не_коммитится()
    {
        var (project, human) = await SetupAsync(shared: true);
        // «repo2» начинается с «repo», но не лежит под ним: сверка без разделителя его пропустила бы
        var neighbour = Path.Combine(_dir, "repo2");
        Directory.CreateDirectory(neighbour);
        await _git.InitAsync(null, neighbour);
        await _git.RunAsync(null, neighbour, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, neighbour, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(neighbour, "x.md"), "чужое\n");

        _sut.Record(project.Id, neighbour, "x.md", human.Id);
        _sut.PendingAuthor(neighbour, "x.md").Should().BeNull();
        await _sut.StopAsync(CancellationToken.None);

        var count = await _git.RunAsync(null, neighbour, ["rev-list", "--all", "--count"]);
        count.Stdout.Trim().Should().Be("0");
    }

    [Fact]
    public async Task Пользователь_не_из_проекта_не_попадает_в_буфер()
    {
        var (project, _) = await SetupAsync(shared: true);
        var stranger = _users.Add("stranger-" + Guid.NewGuid().ToString("N")[..6], "password", "user");
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");

        _sut.Record(project.Id, _repo, "mine.md", stranger.Id);

        _sut.PendingAuthor(_repo, "mine.md").Should().BeNull();
    }

    [Fact]
    public async Task Нечего_коммитить_если_содержимое_совпало_с_HEAD()
    {
        var (project, human) = await SetupAsync(shared: true);
        var before = await CommitsCountAsync();
        // base.txt записан теми же байтами — git не видит изменения
        await File.WriteAllTextAsync(Path.Combine(_repo, "base.txt"), "база\n");
        _sut.Record(project.Id, _repo, "base.txt", human.Id);

        await _sut.StopAsync(CancellationToken.None);

        (await CommitsCountAsync()).Should().Be(before);
        _sut.PendingAuthor(_repo, "base.txt").Should().BeNull();
    }

    [Fact]
    public async Task Гонка_таймер_и_новый_Record_по_тому_же_пути_не_теряет_автора()
    {
        var (project, first) = await SetupAsync(shared: true);
        var second = _users.Add("second-" + Guid.NewGuid().ToString("N")[..6], "password", "user");
        _projects.SetMembers(project.Id,
        [
            new ProjectMember { UserId = first.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId! },
            new ProjectMember { UserId = second.Id, Role = ProjectRole.Editor, AddedBy = project.OwnerId! },
        ]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");
        _sut.Record(project.Id, _repo, "mine.md", first.Id);
        _clock.Advance(TimeSpan.FromSeconds(30));

        // Между осмотром созревшей записи и её снятием приходит новая запись по тому же пути
        _sut.BeforeClaim = _ => { _sut.BeforeClaim = null; _sut.Record(project.Id, _repo, "mine.md", second.Id); };
        var before = await CommitsCountAsync();
        await _sut.FlushAsync(all: false);

        // Снятие не прошло: старой записи не коммитим, новая (со своим автором) жива
        (await CommitsCountAsync()).Should().Be(before);
        _sut.PendingAuthor(_repo, "mine.md")!.UserId.Should().Be(second.Id);

        _clock.Advance(TimeSpan.FromSeconds(20));
        await _sut.FlushAsync(all: false);
        var log = await LogAsync();
        log.Should().Contain(second.Id).And.NotContain(first.Id);
        _sut.PendingAuthor(_repo, "mine.md").Should().BeNull();
    }

    [Fact]
    public async Task Замок_репозитория_не_держится_на_время_дебаунса()
    {
        var (project, human) = await SetupAsync(shared: true);
        await File.WriteAllTextAsync(Path.Combine(_repo, "mine.md"), "x\n");
        _sut.Record(project.Id, _repo, "mine.md", human.Id);

        // Запись ждёт окна; в это время обычный коммит агента не блокируется замком репозитория
        await File.WriteAllTextAsync(Path.Combine(_repo, "agent.txt"), "агент\n");
        await _git.StageAsync(null, _repo, "agent.txt");
        var agentCommit = _git.CommitAsync(null, _repo, "коммит агента");
        (await Task.WhenAny(agentCommit, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(agentCommit);
        _sut.PendingAuthor(_repo, "mine.md").Should().NotBeNull();
    }

    // ── QA-D1: заметки общего проекта версионируются (решение (б) разреза 2cc7578d) ──

    [Fact]
    public async Task Заметки_общего_проекта_после_Ensure_коммитятся_журналом_с_автором_участника()
    {
        var (project, human) = await SetupAsync(shared: true);
        // Репозиторий создан шаблоном InitAsync: /notes/ игнорируется. Лечение — Ensure
        // (тот же вызов, что при приглашении). Мутация «убрать этот вызов» краснит тест:
        // игнорируемый путь не попадает в index, коммит по pathspec выходит пустым.
        (await _git.EnsureNotesVersionedAsync(null, _repo, author: OwnerAuthor(project)))
            .Should().NotBeEmpty();
        Directory.CreateDirectory(Path.Combine(_repo, "notes"));
        await File.WriteAllTextAsync(Path.Combine(_repo, "notes", "a.md"), "заметка\n");

        _sut.Record(project.Id, _repo, "notes/a.md", human.Id);
        await _sut.FlushAsync(all: true);

        var log = (await _git.RunAsync(null, _repo, ["log", "--format=%an|%s", "--", "notes/a.md"])).Stdout.Trim();
        log.Should().Be($"{human.DisplayName ?? human.Username}|Правка: notes/a.md");
        (await _git.RunAsync(null, _repo, ["rev-list", "--count", "HEAD", "--", "notes/a.md"])).Stdout.Trim()
            .Should().Be("1");
    }

    [Fact]
    public async Task Гонка_двух_Ensure_даёт_ровно_один_коммит_по_gitignore()
    {
        var (project, _) = await SetupAsync(shared: true);
        var author = OwnerAuthor(project);
        var before = int.Parse(await CommitsCountAsync());

        // Две Ensure на одном проекте (приглашение + стартовый проход) — под замком
        // репозитория: вторая видит уже очищенный файл и выходит no-op.
        await Task.WhenAll(
            _git.EnsureNotesVersionedAsync(null, _repo, author: author),
            _git.EnsureNotesVersionedAsync(null, _repo, author: author));

        (await File.ReadAllTextAsync(Path.Combine(_repo, ".gitignore"))).Should().NotContain("/notes/");
        (int.Parse(await CommitsCountAsync())).Should().Be(before + 1);
    }

    [Fact]
    public async Task Ensure_убирает_только_наш_блок_чужие_правила_целы_повтор_не_коммитит()
    {
        var (project, _) = await SetupAsync(shared: true);
        var gitignorePath = Path.Combine(_repo, ".gitignore");
        // Сверху — свои правила пользователя: их Ensure не трогает.
        await File.AppendAllTextAsync(gitignorePath, "build/\nnotes/\n");
        var author = OwnerAuthor(project);

        (await _git.EnsureNotesVersionedAsync(null, _repo, author: author)).Should().NotBeEmpty();
        var after = await File.ReadAllTextAsync(gitignorePath);
        after.Should().NotContain("Заметки и комментарии проекта").And.NotContain("/notes/");
        after.Should().Contain("build/").And.Contain("\nnotes/\n").And.Contain(".cc-attachments/");

        var count = await CommitsCountAsync();
        (await _git.EnsureNotesVersionedAsync(null, _repo, author: author)).Should().BeEmpty();
        (await CommitsCountAsync()).Should().Be(count);
    }

    [Fact]
    public async Task Свой_gitignore_не_меняется_и_журнал_пишет_Warning_вместо_тихой_потери()
    {
        var log = new List<(LogLevel Level, string Message)>();
        var sut = new HumanEditCommitter(_projects, _users, _git, _config,
            new RecordingLogger<HumanEditCommitter>(log), _clock);
        var (project, human) = await SetupAsync(shared: true, gitignoreContent: "notes/\n");
        var gitignorePath = Path.Combine(_repo, ".gitignore");

        // Нашего блока в файле нет — Ensure не трогает чужой файл и не коммитит.
        var before = await CommitsCountAsync();
        (await _git.EnsureNotesVersionedAsync(null, _repo, author: OwnerAuthor(project))).Should().BeEmpty();
        (await File.ReadAllTextAsync(gitignorePath)).Should().Be("notes/\n");

        Directory.CreateDirectory(Path.Combine(_repo, "notes"));
        await File.WriteAllTextAsync(Path.Combine(_repo, "notes", "a.md"), "заметка\n");
        sut.Record(project.Id, _repo, "notes/a.md", human.Id);
        await sut.FlushAsync(all: true);

        // Путь игнорируется: коммита нет — и это названо в логе (c3), а не потеряно молча.
        (await CommitsCountAsync()).Should().Be(before);
        log.Should().Contain(l => l.Level == LogLevel.Warning
            && l.Message.Contains("исключены .gitignore") && l.Message.Contains(project.Id));
    }

    [Fact]
    public async Task Личный_проект_сохраняет_игнор_notes_и_стартовый_проход_его_не_трогает()
    {
        var (personal, _) = await SetupAsync(shared: false);
        var gitignorePath = Path.Combine(_repo, ".gitignore");
        (await File.ReadAllTextAsync(gitignorePath)).Should().Contain("/notes/");
        var before = await CommitsCountAsync();

        // Тот же проход, который StartAsync делает в фоне: проекты без участников — мимо.
        await _sut.EnsureNotesVersionedSweepAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(gitignorePath)).Should().Contain("/notes/");
        (await CommitsCountAsync()).Should().Be(before);
    }
}
