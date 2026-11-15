using ClaudeHomeServer.Services.Files;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Ограниченный корень (папка «Личного», ADR-040): git там не запускается, ссылки наружу не обходятся
public class FileServiceRestrictedRootTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "restricted-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string _outside;

    public FileServiceRestrictedRootTests()
    {
        _root = Path.Combine(_dir, "root");
        _outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.conf"), "x");
        File.WriteAllText(Path.Combine(_root, "own.conf"), "x");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private FileService Service(Func<string, (HashSet<string>, HashSet<string>)>? git = null) =>
        new(null, null, NullLogger<FileService>.Instance) { GitStatusComputer = git };

    [Fact]
    public void Ограниченный_корень_git_не_запускает_даже_при_наличии_dot_git()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var calls = 0;
        var files = Service(_ => { calls++; return ([], []); });
        files.RestrictRoot(_root);

        files.List(_root).ToList();
        files.Tree(_root).ToList();
        files.Search(_root, "conf").ToList();

        calls.Should().Be(0, "статус git для ограниченного корня не считается: .git внутри мог подложить кто угодно");
    }

    [Fact]
    public void Обычный_корень_git_по_прежнему_считает()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var calls = 0;
        var files = Service(_ => { calls++; return ([], []); });

        files.List(_root).ToList();

        calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Ограниченный_корень_не_спускается_в_ссылку_наружу_в_дереве_и_поиске()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), _outside);
        var files = Service();
        files.RestrictRoot(_root);

        var tree = files.Tree(_root).Select(e => e.Path).ToList();
        var found = files.Search(_root, "conf").Select(e => e.Path).ToList();

        tree.Should().NotContain(p => p.Contains("secret.conf"));
        found.Should().Contain("own.conf").And.NotContain(p => p.Contains("secret.conf"));
    }
}
