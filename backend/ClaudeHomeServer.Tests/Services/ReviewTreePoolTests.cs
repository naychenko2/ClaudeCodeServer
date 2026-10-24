using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Пул слотов ревью на настоящем git: временный репозиторий, дерево волны и слоты рядом.
[Trait("Category", "Slow")]
public class ReviewTreePoolTests : IAsyncLifetime, IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;
    }

    private readonly string _root;
    private readonly string _repo;
    private readonly string _slotBase;
    private readonly string _wave;
    private readonly GitService _git = new(new LocalOnlyFactory());
    private readonly Project _project;
    private Func<string?, string, string> _cwd = (_, p) => p;

    public ReviewTreePoolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rvpool_tests_" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _slotBase = Path.Combine(_root, ".worktrees", "demo");
        _wave = Path.Combine(_root, "wave");
        Directory.CreateDirectory(_repo);
        _project = new Project { Name = "Demo", RootPath = _repo };
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        await RawGit(_repo, "config", "user.email", "test@test");
        await RawGit(_repo, "config", "user.name", "Тест");
        Directory.CreateDirectory(Path.Combine(_repo, "frontend", "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_repo, ".gitignore"), "node_modules/\nobj/\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "один\n");
        await File.WriteAllTextAsync(Path.Combine(_repo, "frontend", "package.json"), "{}\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
        await _git.WorktreeAddAsync(null, _repo, _wave, "feat/wave");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* git держит readonly-объекты — не роняем прогон */ }
    }

    private ReviewTreePool NewPool() =>
        new(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance);

    [Fact]
    public async Task Acquire_Создаёт_Detached_Слот_На_Коммите_Волны_И_Ссылку_На_Node_Modules()
    {
        var dirtyFile = Path.Combine(_wave, "dirty.txt");
        await File.WriteAllTextAsync(dirtyFile, "грязное\n");
        var waveHead = await _git.LocalTipAsync(null, _wave, "HEAD");
        var pool = NewPool();

        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");

        lease.Should().NotBeNull();
        lease!.SlotPath.Should().Be(Path.Combine(_slotBase, "feat-wave-review-1"));
        lease.SlotPath.Should().StartWith(_slotBase + Path.DirectorySeparatorChar);
        (await File.ReadAllTextAsync(dirtyFile)).Should().Be("грязное\n");
        (await _git.LocalTipAsync(null, _wave, "HEAD")).Should().Be(waveHead);
        lease.Sha.Should().Be(await _git.LocalTipAsync(null, _wave, "HEAD"));
        lease.DirtyPaths.Should().Contain("dirty.txt");
        File.Exists(Path.Combine(lease.SlotPath, "dirty.txt")).Should().BeFalse();
        (await _git.WorktreeListAsync(null, _repo)).Should().Contain(w =>
            Path.GetFullPath(w.Path) == Path.GetFullPath(lease.SlotPath) && w.Branch == null);
        new DirectoryInfo(Path.Combine(lease.SlotPath, "frontend", "node_modules")).LinkTarget
            .Should().Be(Path.Combine(_repo, "frontend", "node_modules"));
    }

    [Fact]
    public async Task Acquire_Третий_Захват_Отказ_Release_Освобождает_И_Идемпотентен()
    {
        var pool = NewPool();
        var a = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        var b = await pool.TryAcquireAsync(null, _project, _wave, "t2");
        var c = await pool.TryAcquireAsync(null, _project, _wave, "t3");

        a!.SlotPath.Should().NotBe(b!.SlotPath);
        c.Should().BeNull();

        pool.Release("t1");
        pool.Release("t1");
        pool.Release("нет-такой");
        var d = await pool.TryAcquireAsync(null, _project, _wave, "t3");

        d.Should().NotBeNull();
        d!.SlotPath.Should().Be(a.SlotPath);
    }

    [Fact]
    public async Task Acquire_Повторный_Захват_Сбрасывает_Слот_На_Актуальный_Коммит_И_Хранит_Игнорируемое()
    {
        var pool = NewPool();
        var first = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        pool.Release("t1");
        var stray = Path.Combine(first!.SlotPath, "stray.txt");
        await File.WriteAllTextAsync(stray, "мусор\n");
        await File.WriteAllTextAsync(Path.Combine(first.SlotPath, "a.txt"), "испорчено\n");
        var objFile = Path.Combine(first.SlotPath, "obj", "keep.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(objFile)!);
        await File.WriteAllTextAsync(objFile, "сборка");
        await File.WriteAllTextAsync(Path.Combine(_wave, "next.txt"), "новое\n");
        await _git.StageAllAsync(null, _wave);
        await _git.CommitAsync(null, _wave, "второй коммит");

        var second = await pool.TryAcquireAsync(null, _project, _wave, "t2");

        second!.SlotPath.Should().Be(first.SlotPath);
        second.Sha.Should().NotBe(first.Sha);
        File.Exists(stray).Should().BeFalse();
        File.Exists(objFile).Should().BeTrue();
        (await _git.LocalTipAsync(null, second.SlotPath, "HEAD")).Should().Be(second.Sha)
            .And.Be(await _git.LocalTipAsync(null, _wave, "HEAD"));
        (await File.ReadAllTextAsync(Path.Combine(second.SlotPath, "a.txt"))).Should().Be("один\n");
        File.Exists(Path.Combine(second.SlotPath, "next.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Acquire_Ошибка_До_Создания_Дерева_Снимает_Резерв()
    {
        var pool = NewPool();
        _cwd = (_, _) => throw new InvalidOperationException("путь вне монтирований");
        var act = () => pool.TryAcquireAsync(null, _project, _wave, "t1");
        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.Exists(Path.Combine(_slotBase, "feat-wave-review-1")).Should().BeFalse();

        _cwd = (_, p) => p;
        var a = await pool.TryAcquireAsync(null, _project, _wave, "t2");
        var b = await pool.TryAcquireAsync(null, _project, _wave, "t3");

        a.Should().NotBeNull();
        b.Should().NotBeNull();
    }

    [Fact]
    public async Task Acquire_Параллельные_Захваты_Выдают_Ровно_Два_Слота()
    {
        var pool = NewPool();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => pool.TryAcquireAsync(null, _project, _wave, "t" + i)));

        var leases = results.Where(r => r is not null).ToList();
        leases.Should().HaveCount(2);
        leases.Select(l => l!.SlotPath).Distinct().Should().HaveCount(2);
    }

    private static async Task RawGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }
}
