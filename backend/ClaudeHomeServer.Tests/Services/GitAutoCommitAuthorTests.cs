using System.Reflection;
using System.Runtime.CompilerServices;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Access;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// S3-1b: автокоммит хода в общем проекте несёт автора — актора идущего хода.
// Актор читается СИНХРОННО в OnSessionMessageAsync до Task.Run: к моменту фоновой задачи
// реестр хода уже пуст (ядро снимает актора при выходе статуса из Working — тот же класс,
// что исправлял dffec3dc7 для журнала событий). «Очищающий» реестр-подмена отдаёт актора
// только пока TurnLive=true; тест снимает флаг сразу после события — автор обязан остаться.
// Настоящий git во временной папке (по образцу GitCommitAuthorTests).
public class GitAutoCommitAuthorTests : IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;
    }

    // Реестр-подмена: актор живёт только пока TurnLive (флаг тест снимает сразу после
    // события — к фоновой задаче реестр «уже пуст»). null ActorId — фон без актора.
    private sealed class ClearingTurnActorRegistry : ITurnActorRegistry
    {
        public string? ActorId { get; init; }
        public bool TurnLive { get; set; } = true;
        public string? Actor(string sessionId, string ownerId) => TurnLive ? ActorId : null;
    }

    private sealed class NoOpBroadcaster : ISessionBroadcaster
    {
        public Task ToSession(string sessionId, ServerMessage message) => Task.CompletedTask;
        public Task ToSessionExcept(string sessionId, string exceptConnectionId, ServerMessage message) => Task.CompletedTask;
        public Task ToOwner(string ownerId, ServerMessage message) => Task.CompletedTask;
        public Task ToProject(string projectId, ServerMessage message) => Task.CompletedTask;
        public Task ToPreviewLog(string projectId, string serviceId, ServerMessage message) => Task.CompletedTask;
    }

    private readonly string _dir;
    private readonly string _repo;
    private readonly IConfiguration _config;
    private readonly UserStore _users;
    private readonly ProjectManager _projects;
    private readonly GitService _git = new(new LocalOnlyFactory());

    public GitAutoCommitAuthorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gitautocommit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataPath"] = Path.Combine(_dir, "projects.json") })
            .Build();
        _users = new UserStore(_config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance);
        _projects = new ProjectManager(_config, _users, new AppSettingsService(_config));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // Арранж: репозиторий с базовым коммитом (committer «Тест» — он же ожидаемый при
    // отсутствии автора) и одним незакоммиченным файлом — «изменения хода».
    // shared=true — проекту добавлен участник-редактор (общий проект).
    private async Task<Project> SetupProjectAsync(bool shared)
    {
        await _git.InitAsync(null, _repo);
        await _git.RunAsync(null, _repo, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, _repo, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
        // «изменения хода» — появившиеся ПОСЛЕ базового коммита: без них автокоммит
        // считает «ход ничего не поменял» и не коммитит
        await File.WriteAllTextAsync(Path.Combine(_repo, "b.txt"), "два\n");

        var ownerId = _users.GetFirst()!.Id;
        var project = _projects.Create("автокоммит-" + Guid.NewGuid().ToString("N")[..6], _repo, ownerId, "testuser");
        _projects.UpdateGitSettings(project.Id, autoCommit: true);
        if (shared)
        {
            var member = _users.Add("member-" + Guid.NewGuid().ToString("N")[..6], "password", "user");
            _projects.SetMembers(project.Id,
            [
                new ProjectMember { UserId = member.Id, Role = ProjectRole.Editor, AddedBy = ownerId },
            ]);
        }
        return project;
    }

    // Ядро — «голый» SessionManager (по образцу SessionMessageObserverTests): нужен только
    // event OnSessionMessage; поднимаем его через reflection backing-поля, как там.
    private static void FireOnSessionMessage(SessionManager sessions, Session s, ServerMessage m)
    {
        var field = typeof(SessionManager).GetField(nameof(SessionManager.OnSessionMessage),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var del = (Func<Session, ServerMessage, Task>?)field.GetValue(sessions);
        if (del is null) return;
        foreach (var d in del.GetInvocationList().Cast<Func<Session, ServerMessage, Task>>())
            d(s, m).GetAwaiter().GetResult();
    }

    private static Session MakeSession(string projectId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ProjectId = projectId,
        Name = "чат автокоммита",
        Status = SessionStatus.Active,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    // Ждём фоновый автокоммит: HEAD сдвинулся с базового — читаем %an/%ae/%cn и тело.
    private async Task<(string An, string Ae, string Cn, string Body)> WaitForAutoCommitAsync(string baseSha)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var head = await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"]);
            if (head.Ok && head.Stdout.Trim() != baseSha)
            {
                var r = await _git.RunAsync(null, _repo, ["log", "-1", "--format=%an\u001f%ae\u001f%cn\u001f%B"]);
                r.Ok.Should().BeTrue();
                var parts = r.Stdout.Trim().Split('\u001f');
                parts.Should().HaveCount(4);
                return (parts[0], parts[1], parts[2], parts[3].Trim());
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("фоновый автокоммит не произошёл за 15 с");
    }

    [Fact]
    public async Task АвтокоммитОбщегоПроекта_АвторАкторХода_РеестрОчищенМеждуЧтениемИКоммитом()
    {
        var project = await SetupProjectAsync(shared: true);
        var member = project.Members![0];
        var baseSha = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();

        var sessions = (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));
        var registry = new ClearingTurnActorRegistry { ActorId = member.UserId };
        var sut = new GitAutoCommitService(
            sessions, _projects, _users, _git, new NoOpBroadcaster(), registry,
            NullLogger<GitAutoCommitService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        FireOnSessionMessage(sessions, MakeSession(project.Id), new ResultMessage("success", 0, 1, null, null));
        // «реестр очищается между чтением и коммитом»: актор читался синхронно во время
        // Fire…, здесь ядро уже бы его сняло — фоновая задача обязана нести взятого автора
        registry.TurnLive = false;

        var (an, ae, cn, body) = await WaitForAutoCommitAsync(baseSha);
        var memberUser = _users.GetById(member.UserId)!;
        an.Should().Be(memberUser.Username, "автор коммита — актор хода (git %an)");
        ae.Should().Be($"{memberUser.Username}@users.invalid");
        cn.Should().Be("Тест", "committer — прежний, из конфига репозитория");
        body.Should().Contain($"CCS-User: {member.UserId}", "id человека — в трейлере");
        System.Text.RegularExpressions.Regex.Matches(body, @"(?m)^CCS-User:").Count.Should().Be(1);
    }

    [Fact]
    public async Task АвтокоммитБезАктора_БезАвтора_ПоведениеПрежнее()
    {
        var project = await SetupProjectAsync(shared: true);
        var baseSha = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();

        var sessions = (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));
        var registry = new ClearingTurnActorRegistry { ActorId = null }; // фон, исполнитель без инициатора
        var sut = new GitAutoCommitService(
            sessions, _projects, _users, _git, new NoOpBroadcaster(), registry,
            NullLogger<GitAutoCommitService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        FireOnSessionMessage(sessions, MakeSession(project.Id), new ResultMessage("success", 0, 1, null, null));

        var (an, ae, cn, body) = await WaitForAutoCommitAsync(baseSha);
        an.Should().Be("Тест", "без автора — коммит из конфига репозитория, как раньше");
        body.Should().NotContain("CCS-User");
    }

    [Fact]
    public async Task АвтокоммитЛичногоПроекта_АвтораНет_ДажеСЖивымАктором()
    {
        var project = await SetupProjectAsync(shared: false); // участников нет — личный проект
        var baseSha = (await _git.RunAsync(null, _repo, ["rev-parse", "HEAD"])).Stdout.Trim();

        var sessions = (SessionManager)RuntimeHelpers.GetUninitializedObject(typeof(SessionManager));
        var registry = new ClearingTurnActorRegistry { ActorId = _users.GetFirst()!.Id };
        var sut = new GitAutoCommitService(
            sessions, _projects, _users, _git, new NoOpBroadcaster(), registry,
            NullLogger<GitAutoCommitService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        FireOnSessionMessage(sessions, MakeSession(project.Id), new ResultMessage("success", 0, 1, null, null));

        var (an, _, _, body) = await WaitForAutoCommitAsync(baseSha);
        an.Should().Be("Тест", "личный проект — поведение прежнее, автора не было и нет");
        body.Should().NotContain("CCS-User");
    }
}
