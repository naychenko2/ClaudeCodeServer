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

    private async Task<bool> InWorktreeList(string path) =>
        (await _git.WorktreeListAsync(null, _repo)).Any(w => Path.GetFullPath(w.Path) == Path.GetFullPath(path));

    [Fact]
    public async Task RemoveSlotsOfWave_Сносит_Оба_Слота_Волны_Даже_Под_Арендой_И_Не_Трогает_Чужие()
    {
        var pool = NewPool();
        var a = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        var b = await pool.TryAcquireAsync(null, _project, _wave, "t2");
        var foreign = Path.Combine(_slotBase, "other-review-1");
        await _git.WorktreeAddDetachedAsync(null, _repo, foreign, await _git.LocalTipAsync(null, _repo, "HEAD") ?? "HEAD");

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(a!.SlotPath)).Should().BeFalse();
        (await InWorktreeList(b!.SlotPath)).Should().BeFalse();
        Directory.Exists(a.SlotPath).Should().BeFalse();
        (await InWorktreeList(foreign)).Should().BeTrue();
        // аренда снята вместе со слотом: оба слота снова свободны
        (await pool.TryAcquireAsync(null, _project, _wave, "t3")).Should().NotBeNull();
        (await pool.TryAcquireAsync(null, _project, _wave, "t4")).Should().NotBeNull();
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Без_Слотов_Ничего_Не_Делает()
    {
        var pool = NewPool();

        var act = () => pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        await act.Should().NotThrowAsync();
        (await InWorktreeList(_wave)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSlotsOfProject_Сносит_Слоты_Всех_Волн_И_Оставляет_Деревья_Чатов()
    {
        var pool = NewPool();
        var other = Path.Combine(_root, "wave2");
        await _git.WorktreeAddAsync(null, _repo, other, "feat/other");
        var chatTree = Path.Combine(_slotBase, "chat-tree");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/chat");
        var a = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        var b = await pool.TryAcquireAsync(null, _project, other, "t2");

        await pool.RemoveSlotsOfProjectAsync(null, _project);

        (await InWorktreeList(a!.SlotPath)).Should().BeFalse();
        (await InWorktreeList(b!.SlotPath)).Should().BeFalse();
        (await InWorktreeList(chatTree)).Should().BeTrue();
        (await InWorktreeList(_wave)).Should().BeTrue();
        (await pool.TryAcquireAsync(null, _project, _wave, "t3")).Should().NotBeNull();
    }

    [Fact]
    public async Task RestoreLeases_Возвращает_Аренду_Открытого_Ревью_И_Не_Возвращает_Закрытому()
    {
        var slot1 = Path.Combine(_slotBase, "feat-wave-review-1");
        var slot2 = Path.Combine(_slotBase, "feat-wave-review-2");
        var tasks = new List<TaskItem>
        {
            new() { Id = "open", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s-open", Status = TaskItemStatus.InProgress },
            new() { Id = "done", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s-done", Status = TaskItemStatus.Done },
            new() { Id = "step", Role = TaskRoles.Step, ProjectId = _project.Id, LinkedSessionId = "s-step", Status = TaskItemStatus.InProgress },
        };
        var worktrees = new Dictionary<string, string> { ["s-open"] = slot1, ["s-done"] = slot2, ["s-step"] = slot2 };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, id => worktrees.GetValueOrDefault(id), id => id == _project.Id ? _project : null);

        await pool.RestoreLeasesAsync();

        // slot1 занят восстановленной арендой, slot2 свободен ровно один раз
        var first = await pool.TryAcquireAsync(null, _project, _wave, "new1");
        var second = await pool.TryAcquireAsync(null, _project, _wave, "new2");
        first!.SlotPath.Should().Be(slot2);
        second.Should().BeNull();
        // повторный вход той же задачи отдаёт её же восстановленный слот
        var again = await pool.TryAcquireAsync(null, _project, _wave, "open");
        again!.SlotPath.Should().Be(slot1);
    }

    [Fact]
    public async Task RestoreLeases_Игнорирует_Чат_Вне_Слота_Проекта()
    {
        var tasks = new List<TaskItem>
        {
            new() { Id = "open", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s", Status = TaskItemStatus.InProgress },
        };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, _ => _wave, id => id == _project.Id ? _project : null);

        await pool.RestoreLeasesAsync();

        (await pool.TryAcquireAsync(null, _project, _wave, "a")).Should().NotBeNull();
        (await pool.TryAcquireAsync(null, _project, _wave, "b")).Should().NotBeNull();
    }

    private static async Task RawGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }
}
