using System.Diagnostics;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.Services;

// Чтение состояния git дерева шага на настоящем временном репозитории (NC-P1b): чистота и набор
// изменённых файлов от основной ветки
public class GitWorktreeHeadReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "git_head_" + Guid.NewGuid().ToString("N"));
    private readonly GitWorktreeHeadReader _sut = new();

    public GitWorktreeHeadReaderTests()
    {
        Directory.CreateDirectory(_dir);
        Git("init", "-b", "master");
        Git("config", "user.email", "t@example.com");
        Git("config", "user.name", "t");
        Commit("README.md", "основа");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, "git " + string.Join(' ', args));
    }

    private void Commit(string file, string content)
    {
        var path = Path.Combine(_dir, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Git("add", "-A");
        Git("commit", "-m", "c");
    }

    [Fact]
    public void ЧистоеДерево_IsCleanTrue()
    {
        _sut.IsClean(_dir).Should().BeTrue();
    }

    [Fact]
    public void НеотслеживаемыйФайл_IsCleanFalse()
    {
        File.WriteAllText(Path.Combine(_dir, "new.cs"), "x");

        _sut.IsClean(_dir).Should().BeFalse();
    }

    [Fact]
    public void ПравкаОтслеживаемого_IsCleanFalse()
    {
        File.WriteAllText(Path.Combine(_dir, "README.md"), "правка");

        _sut.IsClean(_dir).Should().BeFalse();
    }

    [Fact]
    public void НеГитКаталог_IsCleanNull()
    {
        var plain = Path.Combine(Path.GetTempPath(), "plain_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try { _sut.IsClean(plain).Should().BeNull(); }
        finally { Directory.Delete(plain); }
    }

    [Fact]
    public void ChangedFiles_ТолькоФайлыВетки_ОтКорняЧерезСлеш()
    {
        Git("checkout", "-b", "step");
        Commit("frontend/src/App.tsx", "a");

        _sut.ChangedFiles(_dir).Should().Equal("frontend/src/App.tsx");
    }

    [Fact]
    public void ChangedFiles_БезИзменений_ПустойСписок()
    {
        _sut.ChangedFiles(_dir).Should().NotBeNull().And.BeEmpty();
    }
}
