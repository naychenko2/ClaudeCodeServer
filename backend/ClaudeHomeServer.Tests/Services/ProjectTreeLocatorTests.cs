using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Локатор корней (шаг B1b фичы «Резолвинг ссылок по worktree») на настоящем git: временный
// репозиторий, worktree-деревья рядом и случаи порченной ссылки .git.
[Trait("Category", "Slow")]
public class ProjectTreeLocatorTests : IAsyncLifetime, IDisposable
{
    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(Project project) => Local;
    }

    private readonly string _root;
    private readonly string _repo;
    private readonly GitService _git = new(new LocalOnlyFactory());
    private readonly Project _project;

    private ProjectTreeLocator Locator() =>
        new(_git, new LocalOnlyFactory(), NullLogger<ProjectTreeLocator>.Instance);

    public ProjectTreeLocatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "tree_loc_tests_" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        _project = new Project { Name = "Demo", RootPath = _repo };
    }

    public async Task InitializeAsync()
    {
        await _git.InitAsync(null, _repo);
        await RawGit("config", "user.email", "test@test");
        await RawGit("config", "user.name", "Тест");
        await File.WriteAllTextAsync(Path.Combine(_repo, "a.txt"), "главное\n");
        await _git.StageAllAsync(null, _repo);
        await _git.CommitAsync(null, _repo, "начальный коммит");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* git держит readonly-объекты — не роняем прогон */ }
    }

    private Task RawGit(params string[] args) => RawGitIn(_repo, args);

    private static async Task RawGitIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
    }

    private string Wt(string name) => Path.Combine(_root, "wt_" + name);

    // Безопасность списка worktree: список — данные, которыми правит агент. Дерево,
    // чья .git-ссылка ведёт не под <main>/.git/worktrees/, корнем не считается.
    // Мутация: убрать проверку IsLinkedWorktree в ProjectTreeLocator — тест краснеет.
    [Fact]
    public async Task Roots_Chuzhoi_Gitdir_Otvorgnaut()
    {
        var wt = Wt("chuzhoi");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/chuzhoi");
        await File.WriteAllTextAsync(Path.Combine(wt, "report.md"), "из дерева с порченной ссылкой\n");
        // Портим ссылку так, что git всё равно перечисляет дерево, но его .git ведёт
        // в произвольную папку — приёмить её как корень нельзя.
        await File.WriteAllTextAsync(Path.Combine(wt, ".git"),
            "gitdir: " + Path.Combine(_root, "not-a-worktrees", "chuzhoi") + "\n");

        var roots = await Locator().RootsAsync(null, _project);
        roots.Should().NotContain(r => PathNormalizer.NormalizePath(r.Path) == PathNormalizer.NormalizePath(wt));
        (await Locator().LocateAsync(null, _project, "report.md")).Should().BeNull();

        // Убирать «руками»: git worktree remove сам проверяет .git-ссылку и на порченной
        // отказывает — в этом тесте он не нужен.
        Directory.Delete(wt, recursive: true);
    }

    // Абсолютный rel отклоняется до цикла: SafePath.Join на Linux молча сделал бы его
    // относительным, и «ссылка наружу» превратилась бы в путь внутри проекта.
    [Fact]
    public async Task Locate_Absolutnyy_Rel_Otvergaetsya()
    {
        (await Locator().LocateAsync(null, _project, "/a.txt")).Should().BeNull();
    }

    // Локальный проект: файлы на устройстве, серверный git его путей не знает — один корень.
    [Fact]
    public async Task Roots_Lokalnyy_Proekt_Daet_Tolko_RootPath()
    {
        var local = new Project { Name = "Local", RootPath = _repo, DeviceId = "device-1" };

        var roots = await Locator().RootsAsync(null, local, Wt("ignored"));

        roots.Should().HaveCount(1);
        roots[0].Path.Should().Be(_repo);
        roots[0].IsAnchor.Should().BeFalse();
    }

    // Приоритет: анкер (контекст автора ссылки) → main → остальные. Без анкера — main,
    // даже когда файл есть в чужом дереве свежее.
    [Fact]
    public async Task Locate_Prioritet_Anker_Main_Ostaltie()
    {
        var wtA = Wt("anchor");
        var wtO = Wt("other");
        await _git.WorktreeAddAsync(null, _repo, wtA, "wt/anchor");
        await _git.WorktreeAddAsync(null, _repo, wtO, "wt/other");
        await File.WriteAllTextAsync(Path.Combine(_repo, "shared.txt"), "из main\n");
        await File.WriteAllTextAsync(Path.Combine(wtA, "shared.txt"), "из анкера\n");
        await File.WriteAllTextAsync(Path.Combine(wtO, "shared.txt"), "из чужого\n");

        var withAnchor = await Locator().LocateAsync(null, _project, "shared.txt", wtA);
        withAnchor.Should().NotBeNull();
        withAnchor!.IsAnchor.Should().BeTrue();
        withAnchor!.Root.Should().Be(wtA);
        withAnchor!.Branch.Should().Be("wt/anchor");

        var withoutAnchor = await Locator().LocateAsync(null, _project, "shared.txt");
        withoutAnchor.Should().NotBeNull();
        withoutAnchor!.IsAnchor.Should().BeFalse();
        withoutAnchor!.Root.Should().Be(_repo);

        await _git.WorktreeRemoveAsync(null, _repo, wtA, force: true);
        await _git.WorktreeRemoveAsync(null, _repo, wtO, force: true);
    }

    // Среди чужих деревьев коллизия редка, но если она есть — берётся свежайший mtime.
    // Мутация: заменить выбор mtime на первый совпавший — тест краснеет.
    [Fact]
    public async Task Locate_SrediChuzh_Beretsya_Svezhiy_Mtime()
    {
        var wtOld = Wt("old");
        var wtNew = Wt("new");
        await _git.WorktreeAddAsync(null, _repo, wtOld, "wt/old");
        await _git.WorktreeAddAsync(null, _repo, wtNew, "wt/new");

        var now = DateTime.UtcNow;
        await File.WriteAllTextAsync(Path.Combine(wtOld, "only-in-others.txt"), "старое\n");
        await File.WriteAllTextAsync(Path.Combine(wtNew, "only-in-others.txt"), "новое\n");
        File.SetLastWriteTimeUtc(Path.Combine(wtOld, "only-in-others.txt"), now - TimeSpan.FromMinutes(5));
        File.SetLastWriteTimeUtc(Path.Combine(wtNew, "only-in-others.txt"), now + TimeSpan.FromMinutes(5));

        // Без анкера: файл есть только в чужих деревьях — среди них решает свежий mtime.
        var hit = await Locator().LocateAsync(null, _project, "only-in-others.txt");
        hit.Should().NotBeNull();
        hit!.IsAnchor.Should().BeFalse();
        hit!.Root.Should().Be(wtNew);
        hit!.Branch.Should().Be("wt/new");

        await _git.WorktreeRemoveAsync(null, _repo, wtOld, force: true);
        await _git.WorktreeRemoveAsync(null, _repo, wtNew, force: true);
    }

    // Осознанная смена W4-LINK-B: дерево задачи снесли после мержа — не 404, а поиск
    // по main с меткой isAnchor=false (главный сценарий фичи — чат задачи после мержа).
    [Fact]
    public async Task Locate_Otсутствующiy_Anker_Ne_404_No_Poisk_S_Metkoy()
    {
        var wt = Wt("removed");
        await _git.WorktreeAddAsync(null, _repo, wt, "wt/removed");
        await File.WriteAllTextAsync(Path.Combine(_repo, "report.md"), "отчёт в main после мержа\n");
        Directory.Delete(wt, recursive: true); // «руками» мимо git: запись остаётся, папки нет

        var hit = await Locator().LocateAsync(null, _project, "report.md", wt);
        hit.Should().NotBeNull();
        hit!.IsAnchor.Should().BeFalse();
        hit!.Root.Should().Be(_repo);
    }
}
