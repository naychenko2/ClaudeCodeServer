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
        int consumers, bool saveGraph = true, TimeSpan? timeout = null,
        Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>>? neighbors = null)
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
            NullLogger<ConsumersTailContributor>.Instance, timeout, neighbors);
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
    public async Task НеИсполнительИлиНеПервыйХод_СекцияВыключена_ХукНеВлияет()
    {
        var (sut, dir, data) = await Prepare(consumers: 3);
        try
        {
            sut.IsEnabled(Ctx(dir, hookActive: true)).Should().BeTrue(
                "у local-владельца на Linux хук есть, но правки через Bash он не видит — хвост едет всем");
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
    public async Task ОтветДольшеТаймаута_Молчание_ИОтменаДоходитДоИсточника()
    {
        // Граф «молчит», пока тест его не освободит: потолок проверяется без гонки с таймером
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (sut, dir, data) = await Prepare(consumers: 3, timeout: TimeSpan.FromMilliseconds(50),
            neighbors: async (_, _, ct) =>
            {
                ct.Register(() => cancelled.TrySetResult());
                await release.Task;
                return new CodeGraphNeighborsOutcome(false, null, []);
            });
        try
        {
            // страховка от зависания при сломанном потолке: тест краснеет, а не висит
            (await sut.BuildAsync(Ctx(dir), TaskText).WaitAsync(TimeSpan.FromSeconds(10))).Should().BeNull();

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.TrySetResult();
            Cleanup(dir, data);
        }
    }

    [Fact]
    public async Task ОтветВоВремя_СекцияВозвращается()
    {
        var (sut, dir, data) = await Prepare(consumers: 3, timeout: TimeSpan.FromSeconds(30));
        try
        {
            (await sut.BuildAsync(Ctx(dir), TaskText)).Should().NotBeNull();
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task WindowsПутьИИмяСДопРасширением_BareНеСчитается()
    {
        var (_, dir, data) = await Prepare(consumers: 1);
        try
        {
            ConsumersTailContributor.ExtractFiles(@"Правь C:\src\Lib.cs и src\Lib.cs", dir)
                .Should().BeEmpty("после обратной косой имя не bare");
            ConsumersTailContributor.ExtractFiles("Смотри Lib.cs.bak и Lib.cs.old", dir)
                .Should().BeEmpty("после расширения идёт ещё одно");
            ConsumersTailContributor.ExtractFiles("Правь Lib.cs.", dir)
                .Should().Equal("src/Lib.cs");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ОбходДерева_ОтменяетсяПоТокену()
    {
        var (_, dir, data) = await Prepare(consumers: 1);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = () => ConsumersTailContributor.ExtractFiles("Читать: Lib.cs", dir, cts.Token);

            act.Should().Throw<OperationCanceledException>();
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task BareИмена_ДляОбходаБерутсяНеБольшеЛимита()
    {
        var (_, dir, data) = await Prepare(consumers: 1);
        try
        {
            // три первых имени не находятся, четвёртое — за лимитом: ради него дерево не обходим
            ConsumersTailContributor.ExtractFiles("A1.cs A2.cs A3.cs Lib.cs", dir).Should().BeEmpty();
            ConsumersTailContributor.ExtractFiles("A1.cs A2.cs Lib.cs", dir).Should().Equal("src/Lib.cs");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task БюджетBareИмён_ОбщийСПутями_ИмяСверхБюджетаНеИщется()
    {
        var (_, dir, data) = await Prepare(consumers: 1);
        try
        {
            File.WriteAllText(Path.Combine(dir, "src", "Lib2.cs"), "class Lib2 {}");

            // A1, A2 и src/Lib.cs уже набрали три: Lib2.cs за бюджетом, обход дерева ради него не нужен
            ConsumersTailContributor.ExtractFiles("A1.cs A2.cs src/Lib.cs Lib2.cs", dir).Should().Equal("src/Lib.cs");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ИмяФайлаСКириллицейПослеРасширения_Распознаётся()
    {
        var (_, dir, data) = await Prepare(consumers: 1);
        try
        {
            ConsumersTailContributor.ExtractFiles("Смотри Lib.cs.Текст", dir).Should().Equal("src/Lib.cs");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ПокинутаяПослеПотолкаЗадача_ЗавершаетсяБезИсключения()
    {
        var (sut, dir, data) = await Prepare(consumers: 1);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var text = await sut.BuildTextAsync("Правь Lib.cs", dir, null, cts.Token);

            text.Should().BeNull("отмена после потолка — молчание, а не OperationCanceledException из покинутой задачи");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ИмяФайлаБезКаталога_НаходитсяОбходомДерева()
    {
        var (sut, dir, data) = await Prepare(consumers: 3);
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), "Читать: Lib.cs:12"))!.Sections.Single().Text;

            text.Should().Contain("Потребители src/Lib.cs ");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ИмяФайлаБезКаталога_НеоднозначноеИлиВСлужебномКаталоге_Молчание()
    {
        var (sut, dir, data) = await Prepare(consumers: 3);
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "other"));
            File.WriteAllText(Path.Combine(dir, "other", "Lib.cs"), "class Lib {}");
            (await sut.BuildAsync(Ctx(dir), "Читать: Lib.cs")).Should().BeNull("два файла с таким именем");

            Directory.CreateDirectory(Path.Combine(dir, "node_modules", "pkg"));
            File.WriteAllText(Path.Combine(dir, "node_modules", "pkg", "Dep.ts"), "x");
            ConsumersTailContributor.ExtractFiles("Dep.ts", dir).Should().BeEmpty("служебные каталоги не обходим");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ФайлыИзТекста_БерутсяПоПорядкуПоявленияНеБольшеЛимита()
    {
        var (sut, dir, data) = await Prepare(consumers: 1);
        try
        {
            foreach (var n in new[] { "A", "B", "C", "D" })
                File.WriteAllText(Path.Combine(dir, "src", n + ".cs"), "class X {}");

            var files = ConsumersTailContributor.ExtractFiles(
                "Описание: src/B.cs, src/A.cs, src/Lib.cs. Правила: src/D.cs, src/C.cs", dir);

            files.Should().Equal("src/B.cs", "src/A.cs", "src/Lib.cs");
            files.Count.Should().Be(ConsumersTailContributor.MaxFiles);
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ГрафДерева_НеПостроен_ОтвечаетПоОсновномуДеревуСПометкой()
    {
        var (sut, mainDir, data) = await Prepare(consumers: 3);
        var worktree = Path.Combine(Path.GetTempPath(), "cgtail_wt_" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            Directory.CreateDirectory(Path.Combine(worktree, "src"));
            File.WriteAllText(Path.Combine(worktree, "src", "Lib.cs"), "class Lib {}");
            var ctx = new PromptSessionContext(new Session { TaskExecution = true, MessageCount = 1 }, "owner", null,
                worktree, mainDir, ConsumersHookActive: false);

            var text = (await sut.BuildAsync(ctx, TaskText))!.Sections.Single().Text;

            text.Should().Contain(ConsumersTailContributor.BuildingNote);
            text.Should().Contain("Потребители src/Lib.cs (файлов: 3");
        }
        finally
        {
            Cleanup(mainDir, data);
            if (Directory.Exists(worktree)) Directory.Delete(worktree, true);
        }
    }
}
