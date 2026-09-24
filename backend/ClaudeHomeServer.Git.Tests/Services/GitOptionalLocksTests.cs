using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Read-only команды GitService не берут необязательных блокировок (GIT_OPTIONAL_LOCKS=0):
// ретранслятор чтения (ADR-016 §5) обязан не писать в репозиторий по построению, а
// `git status` без запрета вправе переписать индекс. Write-команды механизм не получают.
[Trait("Category", "Slow")]
public class GitOptionalLocksTests : IAsyncLifetime, IDisposable
{
    // Настоящий локальный запуск плюс запись каждого ProcessSpec — видно, с каким env ушла команда
    private sealed class RecordingLauncher : IProcessLauncher
    {
        private readonly IProcessLauncher _inner = LocalProcessRunner.Instance;
        public ConcurrentQueue<ProcessSpec> Specs { get; } = new();
        public bool IsSandboxed => _inner.IsSandboxed;
        public bool TargetIsWindows => _inner.TargetIsWindows;
        public IPathMapper Paths => _inner.Paths;
        public string ClaudeCliCommand => _inner.ClaudeCliCommand;
        public string HostTempDir => _inner.HostTempDir;
        public string? McpApiUrlOverride => _inner.McpApiUrlOverride;
        public Process Start(ProcessSpec spec) { Specs.Enqueue(spec); return _inner.Start(spec); }
        public void Kill(Process process, string? turnId = null) => _inner.Kill(process, turnId);
        public int EstimateCommandLineLength(ProcessSpec spec) => _inner.EstimateCommandLineLength(spec);
    }

    private sealed class RecordingFactory(RecordingLauncher launcher) : ILauncherFactory
    {
        public IProcessLauncher Local => launcher;
        public IProcessLauncher ForOwner(string? ownerId) => launcher;
        public IProcessLauncher ForProject(ClaudeHomeServer.Models.Project project) => launcher;
    }

    private readonly string _repo;
    private readonly RecordingLauncher _launcher = new();
    private readonly GitService _git;
    private string _sha = "";

    public GitOptionalLocksTests()
    {
        _git = new GitService(new RecordingFactory(_launcher));
        _repo = Path.Combine(Path.GetTempPath(), "gitlocks_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        await _git.RunAsync(null, _repo, ["config", "user.email", "test@test"]);
        await _git.RunAsync(null, _repo, ["config", "user.name", "Тест"]);
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nдва\n");
        await _git.StageAllAsync(null, _repo);
        _sha = await _git.CommitAsync(null, _repo, "начальный коммит");
        _launcher.Specs.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    private static bool HasNoOptionalLocks(ProcessSpec s) =>
        s.Env is { } env && env.TryGetValue(GitService.OptionalLocksEnv, out var v) && v == "0";

    private List<ProcessSpec> Drain()
    {
        var list = new List<ProcessSpec>();
        while (_launcher.Specs.TryDequeue(out var s)) list.Add(s);
        return list;
    }

    // Перечень read-only методов: всё, что зовёт ретранслятор, плюс прочее чтение сервера
    public static TheoryData<string> ReadOps => new()
    {
        "Status", "DiffFile", "DiffFileStaged", "DiffFileVsHead", "Log", "FileLog", "CommitDetail",
        "CommitFileDiff", "FileAtCommit", "Branches", "StashList", "Blame", "RepoSnapshot",
        "RefExists", "ListFiles", "ReadFile", "Tip", "WorktreeList",
    };

    [Theory]
    [MemberData(nameof(ReadOps))]
    public async Task ReadOnly_Команды_Запускаются_Без_Необязательных_Блокировок(string op)
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nДВА\n");
        _launcher.Specs.Clear();

        Task call = op switch
        {
            "Status" => _git.StatusAsync(null, _repo),
            "DiffFile" => _git.DiffFileAsync(null, _repo, "a.txt", staged: false),
            "DiffFileStaged" => _git.DiffFileAsync(null, _repo, "a.txt", staged: true),
            "DiffFileVsHead" => _git.DiffFileVsHeadAsync(null, _repo, "a.txt"),
            "Log" => _git.LogAsync(null, _repo),
            "FileLog" => _git.FileLogAsync(null, _repo, "a.txt"),
            "CommitDetail" => _git.CommitDetailAsync(null, _repo, _sha),
            "CommitFileDiff" => _git.CommitFileDiffAsync(null, _repo, _sha, "a.txt"),
            "FileAtCommit" => _git.FileAtCommitAsync(null, _repo, _sha, "a.txt"),
            "Branches" => _git.BranchesAsync(null, _repo),
            "StashList" => _git.StashListAsync(null, _repo),
            "Blame" => _git.BlameAsync(null, _repo, "a.txt"),
            "RepoSnapshot" => _git.RepoSnapshotAsync(null, _repo),
            "RefExists" => _git.RefExistsAsync(null, _repo, "refs/heads/main"),
            "ListFiles" => _git.ListFilesAsync(null, _repo, "HEAD"),
            "ReadFile" => _git.ReadFileAsync(null, _repo, "HEAD", "a.txt"),
            "Tip" => _git.TipAsync(null, _repo, "HEAD"),
            "WorktreeList" => _git.WorktreeListAsync(null, _repo),
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        await call;

        var specs = Drain();
        specs.Should().NotBeEmpty();
        specs.Should().OnlyContain(s => HasNoOptionalLocks(s),
            $"{op} — чтение и не должен брать .git/index.lock");
    }

    [Fact]
    public async Task Write_Команды_Идут_Без_Запрета_Блокировок()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\nДВА\n");
        _launcher.Specs.Clear();

        await _git.StageAsync(null, _repo, "a.txt");
        await _git.CommitAsync(null, _repo, "второй коммит");
        await _git.CheckoutAsync(null, _repo, "main");

        var specs = Drain();
        specs.Should().NotBeEmpty();
        specs.Should().NotContain(s => HasNoOptionalLocks(s),
            "записи блокировка индекса нужна, запрет ей навязывать нельзя");
    }

    private string IndexHash() =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_repo, ".git", "index"))));

    // Устаревший stat-cache: содержимое то же, mtime другой. Без запрета `git status`
    // обновил бы stat-поля и переписал .git/index — с запретом индекс нетронут байт в байт.
    // Второй шаг: уже висящий index.lock не мешает статусу и не снимается им.
    [Fact]
    public async Task Status_Не_Пишет_В_Git_При_Устаревшем_StatCache_И_Висящем_Локе()
    {
        File.SetLastWriteTimeUtc(Path.Combine(_repo, "a.txt"), new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var before = IndexHash();

        var st = await _git.StatusAsync(null, _repo);

        st.IsRepo.Should().BeTrue();
        st.Unstaged.Should().BeEmpty("содержимое не менялось — только mtime");
        IndexHash().Should().Be(before, "read-only статус не переписывает индекс");

        // Порцелан `git diff` освежил бы индекс, даже с GIT_OPTIONAL_LOCKS=0
        await _git.DiffFileAsync(null, _repo, "a.txt", staged: false);
        (await _git.DiffFileVsHeadAsync(null, _repo, "a.txt")).Should().BeNull();
        IndexHash().Should().Be(before, "read-only дифф не переписывает индекс");

        var lockPath = Path.Combine(_repo, ".git", "index.lock");
        await File.WriteAllTextAsync(lockPath, "");
        var locked = await _git.StatusAsync(null, _repo);
        locked.IsRepo.Should().BeTrue();
        File.Exists(lockPath).Should().BeTrue("чужой лок статус не трогает");
        IndexHash().Should().Be(before);
    }
}
