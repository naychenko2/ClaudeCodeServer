using ClaudeHomeServer.DeviceAgent.Exec;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Exec;

public class TurnWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ws-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..")]
    [InlineData("sub/file.json")]
    [InlineData("sub\\file.json")]
    [InlineData("C:file")]
    [InlineData("")]
    public void Имя_файла_spec_с_путём_отвергается(string name)
    {
        using var ws = TurnWorkspace.Create(_root, "t1", NullLogger.Instance);
        var act = () => ws.Materialize([new ExecFile("f1", name, "x")], "http://127.0.0.1:1/t/k");
        act.Should().Throw<ExecRefusedException>();
        Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Should().OnlyContain(p => p.StartsWith(ws.Directory));
    }

    [Theory]
    [InlineData("../t")]
    [InlineData("a/b")]
    public void Идентификатор_хода_с_путём_отвергается(string turnId)
    {
        var act = () => TurnWorkspace.Create(_root, turnId, NullLogger.Instance);
        act.Should().Throw<ExecRefusedException>();
    }

    [Fact]
    public void Аргумент_на_несуществующий_файл_spec_отвергается()
    {
        using var ws = TurnWorkspace.Create(_root, "t1", NullLogger.Instance);
        var act = () => ws.ResolveArgs(["--mcp-config", ExecSpawnRules.FilePlaceholder("nope")]);
        act.Should().Throw<ExecRefusedException>();
    }

    [Fact]
    public void Файлы_с_одинаковым_именем_не_затирают_друг_друга_и_уходят_с_каталогом()
    {
        string dir;
        using (var ws = TurnWorkspace.Create(_root, "t1", NullLogger.Instance))
        {
            dir = ws.Directory;
            ws.Materialize([new ExecFile("f1", "a.json", "1"), new ExecFile("f2", "a.json", "2")], "http://s");
            var args = ws.ResolveArgs([ExecSpawnRules.FilePlaceholder("f1"), ExecSpawnRules.FilePlaceholder("f2"), "-p"]);
            File.ReadAllText(args[0]).Should().Be("1");
            File.ReadAllText(args[1]).Should().Be("2");
            args[2].Should().Be("-p");
        }
        Directory.Exists(dir).Should().BeFalse();
    }
}
