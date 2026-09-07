using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Сторож фильтра топа хабов: константы (чистые данные) не занимают строки топа — у них
/// нет исходящих путей, degree делает их «словарём» (токены дизайн-системы обгоняют
/// координаторов), а не точкой входа в код. Покрывает оба места топа: CodeGraph.GodNodes
/// (снимок + slice промпта) и CodeGraphQueryService (инструмент codegraph_hubs);
/// codegraph_find константы находит как раньше — фильтр только на топ.
/// </summary>
public class CodeGraphGodNodesTests
{
    private static CodeGraphNode Node(string id, string file, NodeKind kind) => new()
    {
        Id = id,
        Label = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id,
        FullyQualifiedName = id,
        SourceFile = file,
        SourceLocation = "L1",
        Kind = kind,
    };

    /// <summary>
    /// Константа с degree 20 обгоняет координатора (degree 12): без фильтра топ занят
    /// «словарём», с фильтром — точкой входа.
    /// </summary>
    private static CodeGraph GraphWithConstantHub()
    {
        var nodes = new Dictionary<string, CodeGraphNode>
        {
            ["Demo.C"] = Node("Demo.C", "design.ts", NodeKind.Constant),
            ["Demo.Hub"] = Node("Demo.Hub", "Hub.cs", NodeKind.Class),
        };
        var edges = new List<CodeGraphEdge>();
        for (var i = 0; i < 20; i++)
        {
            var id = $"Demo.Consumer{i}";
            nodes[id] = Node(id, $"Consumer{i}.cs", NodeKind.Class);
            edges.Add(new() { Source = id, Target = "Demo.C", Relation = EdgeRelation.References, Confidence = EdgeConfidence.Extracted });
        }
        for (var i = 0; i < 12; i++)
        {
            var id = $"Demo.Use{i}";
            nodes[id] = Node(id, $"Use{i}.cs", NodeKind.Class);
            edges.Add(new() { Source = "Demo.Hub", Target = id, Relation = EdgeRelation.References, Confidence = EdgeConfidence.Extracted });
        }
        return new() { Nodes = nodes, Edges = edges };
    }

    [Fact]
    public void GodNodes_ИсключаетКонстантыИзТопа()
    {
        var god = GraphWithConstantHub().GodNodes().ToList();

        god.Should().NotContain(n => n.Id == "Demo.C", "константа — словарь, не точка входа в код");
        god.Should().Contain(n => n.Id == "Demo.Hub");
    }

    [Fact]
    public async Task HubsAsync_ИсключаетКонстантыИзТопаНоFindИхНаходит()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cgnodes_" + Guid.NewGuid().ToString("N")[..10]);
        var dataDir = Path.Combine(Path.GetTempPath(), "cgnodes_data_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(dataDir);
        try
        {
            var persistence = new GraphPersistence(dataDir, NullLogger<GraphPersistence>.Instance);
            var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, null!, persistence,
                new ConfigurationBuilder().Build());
            await persistence.SaveAsync(dir, GraphWithConstantHub(), CancellationToken.None);
            var queries = new CodeGraphQueryService(graphs);

            var hubs = await queries.HubsAsync(dir, 10, CancellationToken.None);

            hubs.Should().NotBeNull();
            hubs!.Hubs.Select(h => h.Id).Should().NotContain("Demo.C");
            hubs.Hubs[0].Id.Should().Be("Demo.Hub", "первый хаб — координатор, а не константа с большим degree");

            var find = await queries.FindAsync(dir, "C", 20, CancellationToken.None);
            find!.Results.Select(r => r.Id).Should().Contain("Demo.C",
                "поиск по имени не фильтрует виды — константа находится");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
