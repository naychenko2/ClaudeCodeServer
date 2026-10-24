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
        lease!.SlotPath.Should().Be(Path.Combine(_slotBase, "feat~wave-review-1"));
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
        Directory.Exists(Path.Combine(_slotBase, "feat~wave-review-1")).Should().BeFalse();

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
    public async Task RemoveSlotsOfWave_Слот_Со_Старым_Кодированием_Имени_Сносится_Вместе_С_Новым()
    {
        var pool = NewPool();
        var fresh = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        var legacy = Path.Combine(_slotBase, "feat-wave-review-1");
        await _git.WorktreeAddDetachedAsync(null, _repo, legacy, await _git.LocalTipAsync(null, _repo, "HEAD") ?? "HEAD");

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(fresh!.SlotPath)).Should().BeFalse();
        (await InWorktreeList(legacy)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Ветка_Дерева_Не_Определена_Слоты_Волны_Сносятся_По_Имени_Каталога()
    {
        var pool = NewPool();
        var slot = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(_repo, "worktree", "remove", "--force", _wave); // регистрация дерева волны пропала
        var unregistered = Path.Combine(_root, "feat-wave");
        var foreign = Path.Combine(_slotBase, "other-review-1");
        await _git.WorktreeAddDetachedAsync(null, _repo, foreign, await _git.LocalTipAsync(null, _repo, "HEAD") ?? "HEAD");

        await pool.RemoveSlotsOfWaveAsync(null, _project, unregistered);

        (await InWorktreeList(slot!.SlotPath)).Should().BeFalse();
        (await InWorktreeList(foreign)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Старый_Слот_С_Живым_Ревью_Другой_Ветки_Не_Сносится()
    {
        // ревью ветки «feat/x» идёт в старом слоте «feat-x-review-1», после рестарта аренда восстановлена по пути
        var legacy = Path.Combine(_slotBase, "feat-x-review-1");
        await _git.WorktreeAddDetachedAsync(null, _repo, legacy, await _git.LocalTipAsync(null, _repo, "HEAD") ?? "HEAD");
        var xWave = Path.Combine(_root, "wave-x");
        await _git.WorktreeAddAsync(null, _repo, xWave, "feat-x");
        var tasks = new List<TaskItem>
        {
            new() { Id = "live", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s", Status = TaskItemStatus.InProgress },
        };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, _ => legacy, id => id == _project.Id ? _project : null);
        await pool.RestoreLeasesAsync();

        await pool.RemoveSlotsOfWaveAsync(null, _project, xWave);

        (await InWorktreeList(legacy)).Should().BeTrue("слот с живым ревью другой ветки не наш");
        // аренда цела: из двух слотов волны «feat-x» первый занят, второй один
        (await pool.TryAcquireAsync(null, _project, xWave, "n1"))!.SlotPath.Should().NotBe(legacy);
        (await pool.TryAcquireAsync(null, _project, xWave, "n2")).Should().BeNull();
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
        var slot1 = Path.Combine(_slotBase, "feat~wave-review-1");
        var slot2 = Path.Combine(_slotBase, "feat~wave-review-2");
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
    public async Task RestoreLeases_Не_Возвращает_Аренду_Остановленному_Ревью()
    {
        var slot1 = Path.Combine(_slotBase, "feat~wave-review-1");
        var tasks = new List<TaskItem>
        {
            new() { Id = "stopped", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s-stopped",
                    Status = TaskItemStatus.InProgress, ExecutorStoppedAt = DateTime.UtcNow },
        };
        var worktrees = new Dictionary<string, string> { ["s-stopped"] = slot1 };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, id => worktrees.GetValueOrDefault(id), id => id == _project.Id ? _project : null);

        await pool.RestoreLeasesAsync();

        var next = await pool.TryAcquireAsync(null, _project, _wave, "new");
        next!.SlotPath.Should().Be(slot1);
    }

    private ReviewTreePool PoolWithStoppedReview(string reason, string slot)
    {
        var tasks = new List<TaskItem>
        {
            new() { Id = "r", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s-r",
                    Status = TaskItemStatus.InProgress, ExecutorStoppedAt = DateTime.UtcNow, ExecutorStopReason = reason },
        };
        return new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, _ => slot, id => id == _project.Id ? _project : null);
    }

    [Fact]
    public async Task RestoreLeases_Пауза_Фичи_Держит_Слот_И_Второе_Ревью_Его_Не_Получает()
    {
        var slot1 = Path.Combine(_slotBase, "feat~wave-review-1");
        var pool = PoolWithStoppedReview(ExecutorStopClassifier.FeatureHeldReason, slot1);

        await pool.RestoreLeasesAsync();

        var next = await pool.TryAcquireAsync(null, _project, _wave, "new");
        next!.SlotPath.Should().Be(Path.Combine(_slotBase, "feat~wave-review-2"));
        var again = await pool.TryAcquireAsync(null, _project, _wave, "r");
        again!.SlotPath.Should().Be(slot1);
    }

    [Theory]
    [InlineData(ExecutorStopClassifier.InterruptedByUserReason)]
    [InlineData(ExecutorStopClassifier.FeatureHeldExpiredReason)]
    public async Task RestoreLeases_Настоящая_Остановка_Слот_Не_Держит(string reason)
    {
        var slot1 = Path.Combine(_slotBase, "feat~wave-review-1");
        var pool = PoolWithStoppedReview(reason, slot1);

        await pool.RestoreLeasesAsync();

        (await pool.TryAcquireAsync(null, _project, _wave, "new"))!.SlotPath.Should().Be(slot1);
    }

    [Fact]
    public async Task Acquire_Ссылка_Node_Modules_Строится_Переводчиком_Путей_Владельца()
    {
        _cwd = (_, p) => p.StartsWith(_repo, StringComparison.Ordinal) ? "/sandbox" + p[_repo.Length..] : p;
        var pool = NewPool();

        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");

        new DirectoryInfo(Path.Combine(lease!.SlotPath, "frontend", "node_modules")).LinkTarget
            .Should().Be("/sandbox/frontend/node_modules");
    }

    [Fact]
    public async Task RemoveSlotsOfProject_Дерево_Чата_На_Ветке_С_Именем_Слота_Не_Сносится()
    {
        var pool = NewPool();
        var chatTree = Path.Combine(_slotBase, "wt-x-review-1");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/x-review-1");

        await pool.RemoveSlotsOfProjectAsync(null, _project);

        (await InWorktreeList(chatTree)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Дерево_Чата_На_Ветке_С_Именем_Слота_Не_Сносится()
    {
        var pool = NewPool();
        var chatTree = Path.Combine(_slotBase, "feat~wave-review-1");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/clash");

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(chatTree)).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveSlotsOfProject_Слот_На_Ветке_Под_Арендой_Сносится_А_Дерево_Чата_На_Ветке_Нет()
    {
        var pool = NewPool();
        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        // ревьюер заветвился в своём слоте
        await RawGit(lease!.SlotPath, "checkout", "-b", "reviewer/work");
        var chatTree = Path.Combine(_slotBase, "wt-x-review-2");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/x-review-2");

        await pool.RemoveSlotsOfProjectAsync(null, _project);

        (await InWorktreeList(lease.SlotPath)).Should().BeFalse("путь из карты аренд — наш слот, даже на ветке");
        (await InWorktreeList(chatTree)).Should().BeTrue("не из аренд и на ветке — дерево чата");
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Слот_На_Ветке_Под_Арендой_Сносится()
    {
        var pool = NewPool();
        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(lease!.SlotPath, "checkout", "-b", "reviewer/work2");

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(lease.SlotPath)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Дерево_Чата_На_Ветке_Остаётся_А_Слот_Под_Арендой_Сносится()
    {
        // путь не слот (на ветке, не из аренд): снос его не трогает
        var pool = NewPool();
        var held = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        var chatTree = Path.Combine(_slotBase, "feat~wave-review-2");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/clash2");

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(chatTree)).Should().BeTrue();
        (await InWorktreeList(held!.SlotPath)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveSlotsOfProject_Слот_На_Ветке_После_Снятия_Аренды_Сносится()
    {
        var pool = NewPool();
        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(lease!.SlotPath, "checkout", "-b", "reviewer/after-stop");
        pool.Release("t1"); // «Стоп» исполнителя

        await pool.RemoveSlotsOfProjectAsync(null, _project);

        (await InWorktreeList(lease.SlotPath)).Should().BeFalse("слот заведён пулом — наш и без аренды");
    }

    [Fact]
    public async Task Acquire_Слот_На_Ветке_После_Снятия_Аренды_Возвращается_В_Detached()
    {
        var pool = NewPool();
        var first = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(first!.SlotPath, "checkout", "-b", "reviewer/poison");
        pool.Release("t1");

        var second = await pool.TryAcquireAsync(null, _project, _wave, "t2");

        second.Should().NotBeNull("отравленный слот чинится, ревью не уходит в дерево волны");
        second!.SlotPath.Should().Be(first.SlotPath);
        (await _git.WorktreeListAsync(null, _repo)).Should().Contain(w =>
            Path.GetFullPath(w.Path) == Path.GetFullPath(second.SlotPath) && w.Branch == null);
    }

    [Fact]
    public async Task Acquire_Чужое_Дерево_На_Ветке_С_Именем_Слота_Не_Чинится_И_Не_Роняет_Захват()
    {
        var pool = NewPool();
        var chatTree = Path.Combine(_slotBase, "feat~wave-review-1");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/clash3");

        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");

        lease!.SlotPath.Should().Be(Path.Combine(_slotBase, "feat~wave-review-2"), "первый слот чужой — берём второй");
        (await _git.WorktreeListAsync(null, _repo)).Should().Contain(w =>
            Path.GetFullPath(w.Path) == Path.GetFullPath(chatTree) && w.Branch == "feat/clash3");
        File.Exists(chatTree + ReviewTreePool.MarkerSuffix).Should().BeFalse("чужое дерево маркера не получает");
    }

    [Fact]
    public async Task Acquire_Слот_Без_Маркера_На_Ветке_Не_Занимает_Первый_Слот_Навсегда()
    {
        // слот создан до появления маркера, в нём заветвились: маркера нет, ветка есть
        var pool = NewPool();
        var first = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(first!.SlotPath, "checkout", "-b", "reviewer/premarker");
        pool.Release("t1");
        File.Delete(first.SlotPath + ReviewTreePool.MarkerSuffix);

        var a = await pool.TryAcquireAsync(null, _project, _wave, "t2");
        var b = await pool.TryAcquireAsync(null, _project, _wave, "t3");
        var c = await pool.TryAcquireAsync(null, _project, _wave, "t4");

        a!.SlotPath.Should().Be(Path.Combine(_slotBase, "feat~wave-review-2"), "Reserve доходит до второго слота");
        b.Should().BeNull("первый слот не наш и не отдаётся, второй занят");
        c.Should().BeNull();
        // пропущенный слот не остался под арендой: после занятия второго места он по-прежнему не наш
        pool.Release("t2");
        (await pool.TryAcquireAsync(null, _project, _wave, "t5"))!.SlotPath.Should().Be(a.SlotPath);
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Слот_Созданный_После_Снимка_Деревьев_Тоже_Сносится()
    {
        var pool = NewPool();
        string? slot = null;
        pool.AfterWaveSnapshot = async () => slot = (await pool.TryAcquireAsync(null, _project, _wave, "t1"))!.SlotPath;

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        slot.Should().NotBeNull();
        (await InWorktreeList(slot!)).Should().BeFalse("снимок устарел — список перечитывается");
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

    [Fact]
    public async Task Acquire_Ветки_Со_Слешем_И_Дефисом_Дают_Разные_Слоты_И_Снос_Одной_Волны_Не_Трогает_Другую()
    {
        var dashWave = Path.Combine(_root, "wave-dash");
        await _git.WorktreeAddAsync(null, _repo, dashWave, "feat-wave");
        var pool = NewPool();
        var slash = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        var dash = await pool.TryAcquireAsync(null, _project, dashWave, "t2");

        slash!.SlotPath.Should().NotBe(dash!.SlotPath);

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(slash.SlotPath)).Should().BeFalse();
        (await InWorktreeList(dash.SlotPath)).Should().BeTrue("слот чужой волны с похожим именем ветки не трогаем");
    }

    [Fact]
    public async Task RestoreLeases_Сбой_Чтения_Задач_Или_Базы_Деревьев_Не_Бросает_И_Остальные_Аренды_Живут()
    {
        var good = Path.Combine(_slotBase, "feat~wave-review-1");
        var tasks = new List<TaskItem>
        {
            new() { Id = "bad", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "bad", OwnerId = "boom", Status = TaskItemStatus.InProgress },
            new() { Id = "ok", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "ok", Status = TaskItemStatus.InProgress },
        };
        var pool = new ReviewTreePool(_git,
            (o, _) => o == "boom" ? throw new InvalidOperationException("нет базы") : _slotBase,
            (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, id => id == "ok" ? good : "/x/bad-review-1", id => id == _project.Id ? _project : null);

        var act = () => pool.RestoreLeasesAsync();
        await act.Should().NotThrowAsync();
        (await pool.TryAcquireAsync(null, _project, _wave, "n1"))!.SlotPath.Should().NotBe(good);

        var broken = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => throw new InvalidOperationException("стор недоступен"));
        var act2 = () => broken.RestoreLeasesAsync();
        await act2.Should().NotThrowAsync();
    }

    [Fact]
    public async Task После_Рестарта_Слот_На_Ветке_Остановленного_Ревью_Сносится_По_Маркеру()
    {
        var pool = NewPool();
        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(lease!.SlotPath, "checkout", "-b", "reviewer/restart");
        pool.Release("t1"); // «Стоп»
        var afterRestart = NewPool(); // память нового процесса пуста

        await afterRestart.RemoveSlotsOfProjectAsync(null, _project);

        (await InWorktreeList(lease.SlotPath)).Should().BeFalse("маркер на диске опознаёт слот и без памяти процесса");
        File.Exists(lease.SlotPath + ReviewTreePool.MarkerSuffix).Should().BeFalse("маркер снят после сноса");
    }

    [Fact]
    public async Task После_Рестарта_Acquire_Чинит_Слот_На_Ветке_По_Маркеру()
    {
        var pool = NewPool();
        var first = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(first!.SlotPath, "checkout", "-b", "reviewer/restart2");
        pool.Release("t1");
        var afterRestart = NewPool();

        var second = await afterRestart.TryAcquireAsync(null, _project, _wave, "t2");

        second!.SlotPath.Should().Be(first.SlotPath);
        (await _git.WorktreeListAsync(null, _repo)).Should().Contain(w =>
            Path.GetFullPath(w.Path) == Path.GetFullPath(second.SlotPath) && w.Branch == null);
    }

    [Fact]
    public async Task После_Рестарта_Дерево_Чата_На_Ветке_Без_Маркера_Не_Сносится()
    {
        var chatTree = Path.Combine(_slotBase, "feat~wave-review-1");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/clash-restart");

        await NewPool().RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(chatTree)).Should().BeTrue();
    }

    [Fact]
    public async Task Снос_Не_Сошёлся_Маркер_Остаётся_И_Следующая_Уборка_Доводит()
    {
        var pool = NewPool();
        var lease = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        await RawGit(lease!.SlotPath, "checkout", "-b", "reviewer/locked");
        await RawGit(_repo, "worktree", "lock", lease.SlotPath);

        await pool.RemoveSlotsOfProjectAsync(null, _project);

        (await InWorktreeList(lease.SlotPath)).Should().BeTrue("locked-дерево с одним --force не сносится");
        File.Exists(lease.SlotPath + ReviewTreePool.MarkerSuffix).Should().BeTrue("не снесён — признак «наш» не теряем");

        await RawGit(_repo, "worktree", "unlock", lease.SlotPath);
        await NewPool().RemoveSlotsOfProjectAsync(null, _project);
        (await InWorktreeList(lease.SlotPath)).Should().BeFalse();
    }

    [Fact]
    public async Task Окно_Между_Снятием_Аренды_И_Сносом_Путь_Другой_Задаче_Не_Отдаётся()
    {
        var pool = NewPool();
        var held = await pool.TryAcquireAsync(null, _project, _wave, "t1");
        ReviewTreeLease? other = null;
        var fired = false;
        pool.BeforeSlotRemove = async () =>
        {
            if (fired) return;
            fired = true;
            other = await pool.TryAcquireAsync(null, _project, _wave, "t2");
        };

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        other.Should().NotBeNull();
        other!.SlotPath.Should().NotBe(held!.SlotPath, "путь, который сносится, отдавать нельзя");
    }

    [Fact]
    public async Task RemoveSlotsOfProject_Слот_Взятый_После_Снимка_Деревьев_Тоже_Сносится()
    {
        var pool = NewPool();
        string? slot = null;
        pool.AfterProjectSnapshot = async () => slot = (await pool.TryAcquireAsync(null, _project, _wave, "t1"))!.SlotPath;

        await pool.RemoveSlotsOfProjectAsync(null, _project);

        slot.Should().NotBeNull();
        (await InWorktreeList(slot!)).Should().BeFalse("состав слотов проекта перечитывается");
        (await pool.TryAcquireAsync(null, _project, _wave, "t2")).Should().NotBeNull("аренда снята вместе со слотом");
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Фолбэк_Сносит_Старый_Слот_Волны_Со_Слешем_В_Ветке()
    {
        // слот до смены кодирования: «feat/wave» → «feat-wave-review-1», в маркере — имя ветки
        var legacy = Path.Combine(_slotBase, "feat-wave-review-1");
        await _git.WorktreeAddDetachedAsync(null, _repo, legacy, await _git.LocalTipAsync(null, _repo, "HEAD") ?? "HEAD");
        await File.WriteAllTextAsync(legacy + ReviewTreePool.MarkerSuffix, "feat/wave");
        await RawGit(_repo, "worktree", "remove", "--force", _wave);

        await NewPool().RemoveSlotsOfWaveAsync(null, _project, Path.Combine(_root, "feat-wave"));

        (await InWorktreeList(legacy)).Should().BeFalse("маркер «feat/wave» и каталог «feat-wave» — одна волна");
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Старый_Слот_Под_Арендой_При_Непустом_Маркере_Не_Сносится()
    {
        var legacy = Path.Combine(_slotBase, "feat-wave-review-1");
        await _git.WorktreeAddDetachedAsync(null, _repo, legacy, await _git.LocalTipAsync(null, _repo, "HEAD") ?? "HEAD");
        await File.WriteAllTextAsync(legacy + ReviewTreePool.MarkerSuffix, "feat/wave");
        var tasks = new List<TaskItem>
        {
            new() { Id = "live", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s", Status = TaskItemStatus.InProgress },
        };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, _ => legacy, id => id == _project.Id ? _project : null);
        await pool.RestoreLeasesAsync();

        await pool.RemoveSlotsOfWaveAsync(null, _project, _wave);

        (await InWorktreeList(legacy)).Should().BeTrue("старое имя держит живая аренда — слот чужой, маркер не отменяет аренду");
    }

    [Fact]
    public async Task RemoveSlotsOfWave_Фолбэк_Не_Трогает_Слот_Другой_Зарегистрированной_Волны()
    {
        // дерево «feat/wave» пропало из регистрации, а волна «feat-wave» жива и её слот называется так же, как искомый
        var dashWave = Path.Combine(_root, "wave-dash");
        await _git.WorktreeAddAsync(null, _repo, dashWave, "feat-wave");
        var pool = NewPool();
        var dash = await pool.TryAcquireAsync(null, _project, dashWave, "t1");
        await RawGit(_repo, "worktree", "remove", "--force", _wave);

        await pool.RemoveSlotsOfWaveAsync(null, _project, Path.Combine(_root, "feat-wave"));

        (await InWorktreeList(dash!.SlotPath)).Should().BeTrue("префикс слота принадлежит живой зарегистрированной волне");
    }

    [Fact]
    public async Task RestoreLeases_Дерево_Чата_Под_Именем_Слота_Маркера_Не_Получает()
    {
        var chatTree = Path.Combine(_slotBase, "feat~wave-review-1");
        await _git.WorktreeAddAsync(null, _repo, chatTree, "feat/chat-tree");
        var tasks = new List<TaskItem>
        {
            new() { Id = "r", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s", Status = TaskItemStatus.InProgress },
        };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, _ => chatTree, id => id == _project.Id ? _project : null);

        await pool.RestoreLeasesAsync();

        File.Exists(chatTree + ReviewTreePool.MarkerSuffix).Should().BeFalse("дерево на ветке — не слот пула");
        pool.Release("r");
        await pool.RemoveSlotsOfProjectAsync(null, _project);
        (await InWorktreeList(chatTree)).Should().BeTrue("без аренды, маркера и на ветке — дерево чата");
    }

    [Fact]
    public async Task RestoreLeases_Слот_Пула_Detached_Получает_Маркер()
    {
        var pool0 = NewPool();
        var slot = (await pool0.TryAcquireAsync(null, _project, _wave, "t0"))!.SlotPath;
        File.Delete(slot + ReviewTreePool.MarkerSuffix); // слот, созданный до появления маркера
        var tasks = new List<TaskItem>
        {
            new() { Id = "r", Role = TaskRoles.Review, ProjectId = _project.Id, LinkedSessionId = "s", Status = TaskItemStatus.InProgress },
        };
        var pool = new ReviewTreePool(_git, (_, _) => _slotBase, (o, p) => _cwd(o, p), NullLogger<ReviewTreePool>.Instance,
            () => tasks, _ => slot, id => id == _project.Id ? _project : null);

        await pool.RestoreLeasesAsync();

        File.Exists(slot + ReviewTreePool.MarkerSuffix).Should().BeTrue();
    }

    private static async Task RawGit(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }
}
