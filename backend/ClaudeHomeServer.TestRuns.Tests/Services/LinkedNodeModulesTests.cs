using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// node_modules worktree — ссылка на зависимости основного дерева того же репозитория
/// (scripts/worktree-setup.sh): run_tests (FindNodeBin) и npm build (FindNodeModules) её принимают,
/// ссылку наружу репозитория или на node_modules без package.json рядом — нет.
/// </summary>
public class LinkedNodeModulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-linkedmodules-" + Guid.NewGuid().ToString("N"));

    public LinkedNodeModulesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* занят */ }
        GC.SuppressFinalize(this);
    }

    // Основной checkout с зависимостями и worktree с файлом .git (раскладка `git worktree add`)
    private (string Worktree, string Modules) Worktree(bool packageJsonBesideModules = true)
    {
        var primary = Path.Combine(_root, "primary");
        var modules = Path.Combine(primary, "frontend", "node_modules");
        Directory.CreateDirectory(Path.Combine(modules, "vitest"));
        File.WriteAllText(Path.Combine(modules, "vitest", "vitest.mjs"), "");
        if (packageJsonBesideModules) File.WriteAllText(Path.Combine(primary, "frontend", "package.json"), "{}");
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(Path.Combine(worktree, "frontend"));
        var common = Path.Combine(primary, ".git");
        Directory.CreateDirectory(Path.Combine(common, "worktrees", "wt"));
        File.WriteAllText(Path.Combine(common, "worktrees", "wt", "gitdir"), Path.Combine(worktree, ".git") + "\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(common, "worktrees", "wt") + "\n");
        return (worktree, modules);
    }

    private string LinkInWorktree(string worktree, string target)
    {
        var link = Path.Combine(worktree, "frontend", "node_modules");
        TreeLinks.TryLink(link, target, directory: true).Should().BeTrue();
        return link;
    }

    private string Outside()
    {
        var outside = Path.Combine(_root, "outside", "node_modules");
        Directory.CreateDirectory(Path.Combine(outside, "vitest"));
        File.WriteAllText(Path.Combine(outside, "vitest", "vitest.mjs"), "");
        File.WriteAllText(Path.Combine(_root, "outside", "package.json"), "{}");
        return outside;
    }

    private static bool CanLink(string probeRoot)
    {
        var target = Path.Combine(probeRoot, "probe-target");
        Directory.CreateDirectory(target);
        var ok = TreeLinks.TryLink(Path.Combine(probeRoot, "probe-link"), target, directory: true);
        return ok;
    }

    [Fact]
    public void FindNodeBin_СсылкаНаОсновноеДерево_Принимает()
    {
        if (!CanLink(_root)) return;
        var (worktree, modules) = Worktree();
        LinkInWorktree(worktree, modules);

        var bin = TestRunService.FindNodeBin(worktree, "frontend", TestRunKind.Vitest, out var error);

        bin.Should().Be(TestRunService.VitestBin, error);
    }

    [Fact]
    public void FindNodeBin_СсылкаНаКаталогВнеРепозитория_Отказ()
    {
        if (!CanLink(_root)) return;
        var (worktree, _) = Worktree();
        LinkInWorktree(worktree, Outside());

        TestRunService.FindNodeBin(worktree, "frontend", TestRunKind.Vitest, out var error).Should().BeNull();
        error.Should().Contain("node_modules/vitest/vitest.mjs ведёт символической ссылкой за пределы проекта");
    }

    [Fact]
    public void FindNodeBin_СсылкаНаNodeModulesБезPackageJsonРядом_Отказ()
    {
        if (!CanLink(_root)) return;
        var (worktree, modules) = Worktree(packageJsonBesideModules: false);
        LinkInWorktree(worktree, modules);

        TestRunService.FindNodeBin(worktree, "frontend", TestRunKind.Vitest, out var error).Should().BeNull();
        error.Should().Contain("за пределы проекта");
    }

    [Fact]
    public void FindNodeBin_БинарьВОбычномNodeModulesСсылкойНаружу_Отказ()
    {
        if (!CanLink(_root)) return;
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(Path.Combine(plain, "node_modules", "vitest"));
        var foreign = Path.Combine(_root, "foreign.mjs");
        File.WriteAllText(foreign, "");
        if (!TreeLinks.TryLink(Path.Combine(plain, "node_modules", "vitest", "vitest.mjs"), foreign, directory: false)) return;

        TestRunService.FindNodeBin(plain, ".", TestRunKind.Vitest, out var error).Should().BeNull();
        error.Should().Contain("за пределы проекта");
    }

    [Fact]
    public void FindNodeModules_СсылкаНаОсновноеДерево_Принимает()
    {
        if (!CanLink(_root)) return;
        var (worktree, modules) = Worktree();
        var link = LinkInWorktree(worktree, modules);

        NpmBuildService.FindNodeModules(worktree, Path.Combine(worktree, "frontend"), out var error)
            .Should().Be(link, error);
    }

    [Fact]
    public void FindNodeModules_СсылкаНаКаталогВнеРепозитория_Отказ()
    {
        if (!CanLink(_root)) return;
        var (worktree, _) = Worktree();
        LinkInWorktree(worktree, Outside());

        NpmBuildService.FindNodeModules(worktree, Path.Combine(worktree, "frontend"), out var error).Should().BeNull();
        error.Should().Contain("node_modules ведёт символической ссылкой за пределы проекта");
    }

    [Fact]
    public void FindNodeModules_СсылкаНаNodeModulesБезPackageJsonРядом_Отказ()
    {
        if (!CanLink(_root)) return;
        var (worktree, modules) = Worktree(packageJsonBesideModules: false);
        LinkInWorktree(worktree, modules);

        NpmBuildService.FindNodeModules(worktree, Path.Combine(worktree, "frontend"), out var error).Should().BeNull();
        error.Should().Contain("node_modules ведёт символической ссылкой за пределы проекта");
    }
}
