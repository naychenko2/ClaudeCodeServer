using System.Diagnostics;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Перф-регресс листинга файловой панели (27.08): FileService.GetGitStatus звал полный
// GitService.StatusAsync, чей numstat запускал отдельный `git diff --no-index` на КАЖДЫЙ
// untracked-файл (замер на проде: 42 untracked → 2.5 с на листинг одной папки).
// Интеграционные тесты на настоящем git CLI, временный репозиторий на диске.
[Trait("Category", "Slow")]
public class FileServiceGitStatusTests : IAsyncLifetime, IDisposable
{
    // Обёртка над LocalProcessRunner: считает запуски `git diff --no-index` —
    // именно они и были per-file ценой листинга.
    private sealed class RecordingLauncher : IProcessLauncher
    {
        private readonly IProcessLauncher _inner = LocalProcessRunner.Instance;
        private int _noIndexDiffs;
        public int NoIndexDiffs => _noIndexDiffs;
        public void Reset() => Interlocked.Exchange(ref _noIndexDiffs, 0);

        public bool IsSandboxed => _inner.IsSandboxed;
        public bool TargetIsWindows => _inner.TargetIsWindows;
        public IPathMapper Paths => _inner.Paths;
        public string ClaudeCliCommand => _inner.ClaudeCliCommand;
        public string HostTempDir => _inner.HostTempDir;
        public string? McpApiUrlOverride => _inner.McpApiUrlOverride;

        public Process Start(ProcessSpec spec)
        {
            if (spec.Args.Contains("--no-index"))
                Interlocked.Increment(ref _noIndexDiffs);
            return _inner.Start(spec);
        }

        public void Kill(Process process, string? turnId = null) => _inner.Kill(process, turnId);
    }

    private sealed class RecordingFactory(RecordingLauncher launcher) : ILauncherFactory
    {
        public IProcessLauncher Local => launcher;
        public IProcessLauncher ForOwner(string? ownerId) => launcher;
    }

    private readonly string _repo;
    private readonly RecordingLauncher _launcher = new();
    private readonly GitService _git;

    public FileServiceGitStatusTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "fs_git_status_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        _git = new GitService(new RecordingFactory(_launcher));
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        RawGit("config", "user.email", "test@test");
        RawGit("config", "user.name", "Тест");
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "строка\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
        _launcher.Reset(); // фикстуру в счётчик не включаем
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    // Прямой git для конфига фикстуры (без ассертов на сам GitService)
    private void RawGit(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(5000);
    }

    // (а) Листинг папки в репо с untracked-файлами не запускает per-file git.
    // 42 файла — как в проекте «ЦД и ЦО» из замера: раньше это были 42 процесса git.
    [Fact]
    public async Task List_РепоСUntracked_БезPerFileЗапусковGit()
    {
        for (var i = 0; i < 42; i++)
            await File.WriteAllTextAsync(Path.Combine(_repo, $"u{i:00}.txt"), "новый\nфайл\n");

        var svc = new FileService(_git);
        var entries = svc.List(_repo).ToList();

        _launcher.NoIndexDiffs.Should().Be(0,
            "листинг не должен замерять untracked по одному процессу git на файл");
        entries.Should().Contain(e => e.Name == "u00.txt" && e.IsNew);
    }

    // (в) Single-flight: пачка параллельных листингов по одному rootPath (монтирование
    // панели: корень + восстановленные раскрытые папки + полное дерево) даёт ОДНО
    // вычисление статуса, остальные ждут его результат.
    [Fact]
    public async Task GetGitStatus_ПараллельныеЛистинги_ОдноВычисление()
    {
        var svc = new FileService(); // без GitService — вычислитель подменён хуком
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.GitStatusComputer = _ =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            gate.Task.GetAwaiter().GetResult();
            return (new HashSet<string>(), new HashSet<string>());
        };

        var workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => svc.List(_repo).ToList()))
            .ToArray();

        // Ждём входа в вычислитель СОБЫТИЕМ, а не Task.Delay (CI на Linux голоден)
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        gate.SetResult();
        await Task.WhenAll(workers);

        calls.Should().Be(1,
            "single-flight: параллельные листинги ждут один запуск git status, а не считают каждый свой");
    }
}
