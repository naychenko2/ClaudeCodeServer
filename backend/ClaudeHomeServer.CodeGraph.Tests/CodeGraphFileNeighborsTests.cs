using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Режим «по файлу» у CodeGraphQueryService: входящие связи типов, объявленных в файле,
/// сгруппированные по файлу-потребителю (хук PreToolUse «потребители правленого файла»).
/// </summary>
public class CodeGraphFileNeighborsTests
{
    private static CodeGraphNode Node(string id, string file) => new()
    {
        Id = id, Label = id[(id.LastIndexOf('.') + 1)..], FullyQualifiedName = id,
        SourceFile = file, SourceLocation = "line 1", Kind = NodeKind.Class,
    };

    private static CodeGraphEdge Edge(string from, string to, EdgeRelation rel = EdgeRelation.References) => new()
    {
        Source = from, Target = to, Relation = rel, Confidence = EdgeConfidence.Extracted,
    };

    private static async Task<(CodeGraphQueryService Queries, string Dir, string Data)> Prepare(CodeGraph graph)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cgfile_" + Guid.NewGuid().ToString("N")[..10]);
        var data = Path.Combine(Path.GetTempPath(), "cgfile_data_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(data);
        var persistence = new GraphPersistence(data, NullLogger<GraphPersistence>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, null!, persistence,
            new ConfigurationBuilder().Build());
        await persistence.SaveAsync(dir, graph, CancellationToken.None);
        return (new CodeGraphQueryService(graphs), dir, data);
    }

    [Fact]
    public async Task ГруппируетСвязиДвухТиповФайлаПоФайлуПотребителю()
    {
        var graph = new CodeGraph
        {
            Nodes = new Dictionary<string, CodeGraphNode>
            {
                ["A.One"] = Node("A.One", "src/Lib.cs"),
                ["A.Two"] = Node("A.Two", "src/Lib.cs"),
                ["B.User"] = Node("B.User", "src/User.cs"),
                ["T.UserTests"] = Node("T.UserTests", "tests/UserTests.cs"),
            },
            Edges =
            [
                Edge("B.User", "A.One"), Edge("B.User", "A.Two", EdgeRelation.Calls),
                Edge("T.UserTests", "A.One"),
                Edge("A.Two", "A.One"), // внутри файла — не потребитель
            ],
        };
        var (queries, dir, data) = await Prepare(graph);
        try
        {
            var outcome = await queries.NeighborsByFileAsync(dir, @".\src\Lib.cs", 100, CancellationToken.None);

            outcome.HasGraph.Should().BeTrue();
            var result = outcome.Result!;
            result.Total.Should().Be(2);
            result.Neighbors.Select(n => n.Fqn).Should().Equal("src/User.cs", "tests/UserTests.cs");
            result.Neighbors[0].Relation.Should().Be("Calls+References");
            result.Neighbors.Should().OnlyContain(n => n.Direction == "in");
        }
        finally { Directory.Delete(dir, true); Directory.Delete(data, true); }
    }

    [Fact]
    public async Task ФайлВнеГрафа_РезультатаНет()
    {
        var graph = new CodeGraph
        {
            Nodes = new Dictionary<string, CodeGraphNode> { ["A.One"] = Node("A.One", "src/Lib.cs") },
            Edges = [],
        };
        var (queries, dir, data) = await Prepare(graph);
        try
        {
            var outcome = await queries.NeighborsByFileAsync(dir, "src/Nope.cs", 100, CancellationToken.None);

            outcome.HasGraph.Should().BeTrue();
            outcome.Result.Should().BeNull();
        }
        finally { Directory.Delete(dir, true); Directory.Delete(data, true); }
    }
}
