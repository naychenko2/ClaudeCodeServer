using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Turn;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Потребители файлов постановки хвостом первого хода исполнителя (там, где нет хука PreToolUse).
/// </summary>
public class ConsumersTailContributorTests
{
    private sealed class OpenGate : IPersonaServerToolGate
    {
        public bool IsServerToolEnabled(string? ownerId, Persona? persona, string toolKey) => true;
    }

    private static CodeGraphNode Node(string id, string file) => new()
    {
        Id = id, Label = id[(id.LastIndexOf('.') + 1)..], FullyQualifiedName = id,
        SourceFile = file, SourceLocation = "line 1", Kind = NodeKind.Class,
    };

    private static CodeGraphEdge Edge(string from, string to) => new()
    {
        Source = from, Target = to, Relation = EdgeRelation.References, Confidence = EdgeConfidence.Extracted,
    };

    // Дерево с файлом src/Lib.cs и графом, где у него consumers потребителей (первые tests — тесты)
    private static async Task<(ConsumersTailContributor Sut, string Dir, string Data)> Prepare(
        int consumers, bool saveGraph = true, TimeSpan? timeout = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cgtail_" + Guid.NewGuid().ToString("N")[..10]);
        var data = Path.Combine(Path.GetTempPath(), "cgtail_data_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(dir, "src", "Lib.cs"), "class Lib {}");
        var persistence = new GraphPersistence(data, NullLogger<GraphPersistence>.Instance);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, null!, persistence,
            new ConfigurationBuilder().Build());
        if (saveGraph)
        {
            var nodes = new Dictionary<string, CodeGraphNode> { ["A.Lib"] = Node("A.Lib", "src/Lib.cs") };
            var edges = new List<CodeGraphEdge>();
            for (var i = 0; i < consumers; i++)
            {
                var file = i < 2 ? $"tests/C{i}Tests.cs" : $"src/C{i:D2}.cs";
                nodes[$"N.C{i}"] = Node($"N.C{i}", file);
                edges.Add(Edge($"N.C{i}", "A.Lib"));
            }
            await persistence.SaveAsync(dir, new CodeGraph { Nodes = nodes, Edges = edges }, CancellationToken.None);
        }
        var sut = new ConsumersTailContributor(graphs, new CodeGraphQueryService(graphs), new OpenGate(),
            NullLogger<ConsumersTailContributor>.Instance, timeout);
        return (sut, dir, data);
    }

    private static PromptSessionContext Ctx(string dir, bool hookActive = false, bool task = true, int messages = 1) =>
        new(new Session { TaskExecution = task, MessageCount = messages }, "owner", null, dir, dir,
            ConsumersHookActive: hookActive);

    private const string TaskText = "Правь `src/Lib.cs:12` и ещё src/Нет.cs, потом отчитайся.";

    private static void Cleanup(string dir, string data)
    {
        Directory.Delete(dir, true);
        Directory.Delete(data, true);
    }

    [Fact]
    public async Task БезХука_ПервыйХодИсполнителя_ХвостомСПотребителямиИЧислами()
    {
        var (sut, dir, data) = await Prepare(consumers: 5);
        try
        {
            var ctx = Ctx(dir);
            sut.IsEnabled(ctx).Should().BeTrue();

            var section = (await sut.BuildAsync(ctx, TaskText))!.Sections.Single();

            section.InTurnTail.Should().BeTrue("нестабильный текст едет хвостом хода, не системным блоком");
            section.Text.Should().Contain("Потребители src/Lib.cs (файлов: 5, тестов среди них: 2)");
            section.Text.Should().Contain("src/C04.cs").And.Contain("[тест]");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task Больше20Потребителей_ОбрезаетсяДо20СтрокСЕщёN()
    {
        var (sut, dir, data) = await Prepare(consumers: 60);
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), TaskText))!.Sections.Single().Text;

            var block = text.Split('\n').Skip(1).ToArray(); // без общего заголовка
            block.Length.Should().BeLessThanOrEqualTo(20);
            block[^1].Should().Be("ещё 42");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ХукЕсть_ИлиНеИсполнитель_ИлиНеПервыйХод_СекцияВыключена()
    {
        var (sut, dir, data) = await Prepare(consumers: 3);
        try
        {
            sut.IsEnabled(Ctx(dir, hookActive: true)).Should().BeFalse("хук работает — дубля нет");
            sut.IsEnabled(Ctx(dir, task: false)).Should().BeFalse();
            sut.IsEnabled(Ctx(dir, messages: 2)).Should().BeFalse();
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ФайлВнеГрафаИлиНетФайловВТексте_Молчание()
    {
        var (sut, dir, data) = await Prepare(consumers: 3);
        try
        {
            File.WriteAllText(Path.Combine(dir, "src", "Other.cs"), "class Other {}");
            (await sut.BuildAsync(Ctx(dir), "Правь src/Other.cs")).Should().BeNull("файл вне графа");
            (await sut.BuildAsync(Ctx(dir), "Правок по файлам нет")).Should().BeNull();
            (await sut.BuildAsync(Ctx(dir), "Правь ../../etc/Lib.cs и /etc/src/Lib.cs")).Should().BeNull("выход из дерева");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ГрафаНет_Молчание()
    {
        var (sut, dir, data) = await Prepare(consumers: 0, saveGraph: false);
        try
        {
            (await sut.BuildAsync(Ctx(dir), TaskText)).Should().BeNull();
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ОтветДольшеТаймаута_Молчание()
    {
        var (sut, dir, data) = await Prepare(consumers: 3, timeout: TimeSpan.Zero);
        try
        {
            (await sut.BuildAsync(Ctx(dir), TaskText)).Should().BeNull();
        }
        finally { Cleanup(dir, data); }
    }
}
