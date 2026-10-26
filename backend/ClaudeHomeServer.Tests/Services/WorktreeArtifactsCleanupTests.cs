using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Files;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Файловая часть уборки на настоящих каталогах во временной папке: что удаляется, а что
// не должно удаляться никогда (исходники, чужое дерево без .git, симлинки).
public class WorktreeArtifactsCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wt_cleanup_tests_" + Guid.NewGuid().ToString("N"));

    public WorktreeArtifactsCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string MakeWorktree(string name)
    {
        var wt = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(wt, "backend", "Proj", "bin", "Debug"));
        Directory.CreateDirectory(Path.Combine(wt, "backend", "Proj", "obj"));
        Directory.CreateDirectory(Path.Combine(wt, "backend", "Proj", "Src"));
        Directory.CreateDirectory(Path.Combine(wt, "frontend", "dist"));
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: /nowhere");
        File.WriteAllText(Path.Combine(wt, "backend", "Proj", "bin", "Debug", "a.dll"), new string('x', 100));
        File.WriteAllText(Path.Combine(wt, "backend", "Proj", "Src", "Code.cs"), "class C {}");
        return wt;
    }

    [Fact]
    public void Clean_УдаляетАртефакты_ОставляетИсходники()
    {
        var wt = MakeWorktree("wt-a");

        var freed = WorktreeBuildArtifacts.Clean(wt, includeNodeModules: true, buildRoot: Path.Combine(_root, "ext"));

        freed.Should().BeGreaterOrEqualTo(100);
        Directory.Exists(Path.Combine(wt, "backend", "Proj", "bin")).Should().BeFalse();
        Directory.Exists(Path.Combine(wt, "backend", "Proj", "obj")).Should().BeFalse();
        Directory.Exists(Path.Combine(wt, "frontend", "dist")).Should().BeFalse();
        File.Exists(Path.Combine(wt, "backend", "Proj", "Src", "Code.cs")).Should().BeTrue();
    }

    [Fact]
    public void Clean_ОтказываетсяНаКаталогеБезGit()
    {
        var notWt = Path.Combine(_root, "plain");
        Directory.CreateDirectory(Path.Combine(notWt, "backend", "Proj", "bin"));

        WorktreeBuildArtifacts.Clean(notWt, true, Path.Combine(_root, "ext")).Should().Be(0);

        Directory.Exists(Path.Combine(notWt, "backend", "Proj", "bin")).Should().BeTrue();
    }

    [Fact]
    public void Clean_НеТрогаетСимлинкNodeModules()
    {
        var wt = MakeWorktree("wt-b");
        var primaryModules = Path.Combine(_root, "primary-modules");
        Directory.CreateDirectory(primaryModules);
        File.WriteAllText(Path.Combine(primaryModules, "keep.js"), "1");
        var link = Path.Combine(wt, "frontend", "node_modules");
        try { Directory.CreateSymbolicLink(link, primaryModules); }
        catch (Exception) { return; } // платформа без прав на симлинки

        WorktreeBuildArtifacts.Clean(wt, includeNodeModules: true, Path.Combine(_root, "ext"));

        File.Exists(Path.Combine(primaryModules, "keep.js")).Should().BeTrue();
    }

    [Fact]
    public void Clean_УдаляетВынесенныйКаталогПоИмениWorktree()
    {
        var wt = MakeWorktree("wt-c");
        var ext = Path.Combine(_root, "ext");
        var mine = Path.Combine(ext, "wt-c", "Proj", "bin");
        var other = Path.Combine(ext, "wt-other", "Proj", "bin");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(mine, "a.dll"), "x");

        WorktreeBuildArtifacts.Clean(wt, true, ext);

        Directory.Exists(Path.Combine(ext, "wt-c")).Should().BeFalse();
        Directory.Exists(other).Should().BeTrue();
    }

    [Fact]
    public void ВынесенныйКаталог_НеВыходитЗаКорень()
    {
        var ext = Path.Combine(_root, "ext");
        WorktreeBuildArtifacts.IsStrictlyInside(ext, Path.Combine(ext, "wt")).Should().BeTrue();
        WorktreeBuildArtifacts.IsStrictlyInside(ext, ext).Should().BeFalse();
        WorktreeBuildArtifacts.IsStrictlyInside(ext, ext + "-evil/wt").Should().BeFalse();
    }

    [Fact]
    public void ТмпУборка_ЧиститТолькоСтарыеПоПрефиксуИНеТрогаетЖивыеСокеты()
    {
        var tmp = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(tmp);
        var old = DateTime.UtcNow.AddHours(-5);
        var fresh = DateTime.UtcNow.AddMinutes(-10);

        string Dir(string n, DateTime t)
        {
            var p = Path.Combine(tmp, n);
            Directory.CreateDirectory(p);
            File.WriteAllText(Path.Combine(p, "f"), "12345");
            Directory.SetLastWriteTimeUtc(p, t);
            return p;
        }
        var oldMatch = Dir("task_stall_tests_abc", old);
        var freshMatch = Dir("task_stall_tests_new", fresh);
        var oldForeign = Dir("my-own-dir", old);
        var liveSock = Path.Combine(tmp, "dotnet-diagnostic-111-1-socket");
        var deadSock = Path.Combine(tmp, "dotnet-diagnostic-222-1-socket");
        File.WriteAllText(liveSock, ""); File.WriteAllText(deadSock, "");
        File.SetLastWriteTimeUtc(liveSock, old); File.SetLastWriteTimeUtc(deadSock, old);

        var r = TmpDirCleaner.Clean(tmp, DateTime.UtcNow, TimeSpan.FromHours(2), pid => pid == 111);

        Directory.Exists(oldMatch).Should().BeFalse();
        Directory.Exists(freshMatch).Should().BeTrue();
        Directory.Exists(oldForeign).Should().BeTrue();
        File.Exists(liveSock).Should().BeTrue();
        File.Exists(deadSock).Should().BeFalse();
        r.Removed.Should().Be(2);
    }
}
