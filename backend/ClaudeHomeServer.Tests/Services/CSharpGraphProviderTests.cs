using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using ClaudeHomeServer.Services.CodeGraph.Roslyn;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// CSharpGraphProvider (Roslyn): извлечение узлов-типов и рёбер Calls/Implements/References,
// фильтр BCL, partial/generics/циклы, инкремент. Платформонезависимо — пути через Path.Combine.
public class CSharpGraphProviderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CSharpGraphProvider _provider = new(NullLogger<CSharpGraphProvider>.Instance);

    public CSharpGraphProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cgraph_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private async Task<CodeGraph> BuildAsync(params (string rel, string code)[] files)
    {
        foreach (var (rel, code) in files)
        {
            var path = Path.Combine(_tempDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, code);
        }
        return await _provider.BuildAsync(_tempDir, CancellationToken.None);
    }

    private static string Id(CodeGraph g, string label) =>
        g.Nodes.Values.Single(n => n.Label == label).Id;

    private static bool HasEdge(CodeGraph g, string srcLabel, string tgtLabel, EdgeRelation rel)
    {
        var src = Id(g, srcLabel);
        var tgt = Id(g, tgtLabel);
        return g.Edges.Any(e => e.Source == src && e.Target == tgt && e.Relation == rel);
    }

    [Fact]
    public async Task Build_Extracts_AllFourKinds()
    {
        var graph = await BuildAsync(("All.cs", """
            namespace N;
            public class C { }
            public interface I { }
            public struct S { }
            public enum E { A, B }
        """));

        graph.Nodes.Values.Select(n => n.Kind).Should().BeEquivalentTo(
            new[] { NodeKind.Class, NodeKind.Interface, NodeKind.Struct, NodeKind.Enum });
        // SourceFile — relPath через '/'
        graph.Nodes.Values.Should().AllSatisfy(n => n.SourceFile.Should().Be("All.cs"));
        // Независимые типы — рёбер между ними нет
        graph.Edges.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_Implements_InterfaceAndBase()
    {
        var graph = await BuildAsync(("Impl.cs", """
            public interface IUse { }
            public class Base { }
            public class Derived : Base, IUse { }
        """));

        HasEdge(graph, "Derived", "Base", EdgeRelation.Implements).Should().BeTrue("Derived наследует Base");
        HasEdge(graph, "Derived", "IUse", EdgeRelation.Implements).Should().BeTrue("Derived реализует IUse");
        // Roslyn даёт точные символы — все рёбра с Confidence=Extracted
        graph.Edges.Should().AllSatisfy(e => e.Confidence.Should().Be(EdgeConfidence.Extracted));
    }

    [Fact]
    public async Task Build_References_FieldPropertyParamReturn()
    {
        var graph = await BuildAsync(("Refs.cs", """
            public class Target { }
            public class Holder
            {
                private Target _field;
                public Target Prop { get; set; }
                public Target Make(Target input) => input;
            }
        """));

        HasEdge(graph, "Holder", "Target", EdgeRelation.References).Should().BeTrue();
    }

    [Fact]
    public async Task Build_Calls_MethodInvocationAndCtor()
    {
        var graph = await BuildAsync(("Calls.cs", """
            public class Service
            {
                public int Compute() => 42;
            }
            public class Client
            {
                private readonly Service _svc = new Service();
                public int Run() => _svc.Compute();
            }
        """));

        // new Service() → Calls(Client, Service)
        HasEdge(graph, "Client", "Service", EdgeRelation.Calls).Should().BeTrue("конструктор Service");
        // _svc.Compute() → Calls(Client, Service)
        graph.Edges.Count(e => e.Relation == EdgeRelation.Calls
            && e.Source == Id(graph, "Client") && e.Target == Id(graph, "Service"))
            .Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Build_FiltersOutBclTypes()
    {
        // List<T> и string — из BCL: не должны стать узлами и не давать рёбер.
        // System.Collections не подключён как reference → ErrorType → отфильтрован.
        var graph = await BuildAsync(("Bcl.cs", """
            public class Local { }
            public class Uses
            {
                public Local Field = new Local();
                public string Name = "";
            }
        """));

        graph.Nodes.Values.Should().NotContain(n => n.Label == "List" || n.Label == "String");
        // Рёбер к BCL-типам быть не должно — все рёбра ведут в проектные типы
        foreach (var edge in graph.Edges)
        {
            graph.Nodes.Should().ContainKey(edge.Target, "цель ребра обязана быть проектным типом");
        }
    }

    [Fact]
    public async Task Build_SkipsGeneratedFiles()
    {
        await BuildAsync(
            ("Form1.designer.cs", "public class DesignerGen { }"),
            ("Real.cs", "public class Real { }"));

        // Designer-файл исключён по relPath
        var graph = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        graph.Nodes.Values.Should().NotContain(n => n.Label == "DesignerGen");
        graph.Nodes.Values.Should().Contain(n => n.Label == "Real");
    }

    [Fact]
    public async Task Build_MergesPartialClass_IntoSingleNode()
    {
        var graph = await BuildAsync(
            ("Foo.Part1.cs", "public partial class Foo { public int A; }"),
            ("Foo.Part2.cs", "public partial class Foo { public int B; }"));

        graph.Nodes.Values.Count(n => n.Label == "Foo").Should().Be(1, "partial-части сливаются по FQN");
    }

    [Fact]
    public async Task Build_PartialSourceFile_Детерминирован()
    {
        // Partial-merge выживает «первый встреченный» узел: его SourceFile обязан быть
        // детерминированным (первый relPath по Ordinal), а не зависеть от порядка словаря.
        await BuildAsync(
            ("Zeta.Part2.cs", "public partial class Split { public int B; }"),
            ("Alpha.Part1.cs", "public partial class Split { public int A; }"));

        var g1 = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        // Второй прогон — свежим провайдером (без кэша): порядок итерации деревьев иной.
        var g2 = await new CSharpGraphProvider(NullLogger<CSharpGraphProvider>.Instance)
            .BuildAsync(_tempDir, CancellationToken.None);

        var f1 = g1.Nodes.Values.Single(n => n.Label == "Split").SourceFile;
        var f2 = g2.Nodes.Values.Single(n => n.Label == "Split").SourceFile;
        f1.Should().Be(f2, "SourceFile не должен прыгать от прогона к прогону");
        f1.Should().Be("Alpha.Part1.cs", "основной partial — первый relPath по Ordinal");
    }

    [Fact]
    public void NodeExtractor_PartialMerge_НезависитОтПорядкаСловаря()
    {
        // Юнит-уровень: один Compilation, два словаря деревьев с разным порядком ключей.
        // До сортировки обхода первый словарь давал SourceFile=B.cs, второй — A.cs.
        var compilation = CompilationBuilder.BuildFromSources(new[]
        {
            ("B.cs", "public partial class P { public int B; }"),
            ("A.cs", "public partial class P { public int A; }"),
        }, includeBaseReferences: false);
        var byPath = compilation.SyntaxTrees.ToDictionary(t => t.FilePath);

        var forward = new Dictionary<string, SyntaxTree>
            { ["B.cs"] = byPath["B.cs"], ["A.cs"] = byPath["A.cs"] };
        var reverse = new Dictionary<string, SyntaxTree>
            { ["A.cs"] = byPath["A.cs"], ["B.cs"] = byPath["B.cs"] };

        var n1 = NodeExtractor.Extract(compilation, forward, new SymbolFilterOptions());
        var n2 = NodeExtractor.Extract(compilation, reverse, new SymbolFilterOptions());

        n1.Nodes.Values.Single(n => n.Label == "P").SourceFile.Should().Be("A.cs");
        n2.Nodes.Values.Single(n => n.Label == "P").SourceFile.Should().Be("A.cs");
    }

    [Fact]
    public async Task Build_HandlesGenerics()
    {
        var graph = await BuildAsync(("Gen.cs", """
            public class Repo<T> { }
            public class Consumer
            {
                public Repo<string> Strings = new Repo<string>();
            }
        """));

        graph.Nodes.Values.Should().Contain(n => n.Label == "Repo");
        // string — BCL, отсечён; Repo — проектный → References
        HasEdge(graph, "Consumer", "Repo", EdgeRelation.References).Should().BeTrue();
        HasEdge(graph, "Consumer", "Repo", EdgeRelation.Calls).Should().BeTrue("new Repo<>()");
    }

    [Fact]
    public async Task Build_HandlesCycles_BothDirections()
    {
        var graph = await BuildAsync(
            ("A.cs", "public class A { public B _b; public void Go() { _b.Run(); } }"),
            ("B.cs", "public class B { public A _a; public void Run() { } }"));

        HasEdge(graph, "A", "B", EdgeRelation.References).Should().BeTrue();
        HasEdge(graph, "B", "A", EdgeRelation.References).Should().BeTrue("цикл — ребро в обе стороны");
        HasEdge(graph, "A", "B", EdgeRelation.Calls).Should().BeTrue();
    }

    [Fact]
    public async Task Build_ResolvesCrossFileDependency()
    {
        // Два типа в разных файлах — resolution должен найти связь (ключевая проверка Wave 2).
        var graph = await BuildAsync(
            ("Session.cs", """
                public class LlmProviderRegistry { public int Count() => 0; }
                public class ClaudeSession
                {
                    private readonly LlmProviderRegistry _registry;
                    public ClaudeSession(LlmProviderRegistry registry) => _registry = registry;
                    public int Init() => _registry.Count();
                }
            """));

        graph.Nodes.Values.Select(n => n.Label)
            .Should().Contain(new[] { "ClaudeSession", "LlmProviderRegistry" });
        HasEdge(graph, "ClaudeSession", "LlmProviderRegistry", EdgeRelation.References).Should().BeTrue();
        HasEdge(graph, "ClaudeSession", "LlmProviderRegistry", EdgeRelation.Calls).Should().BeTrue();
    }

    [Fact]
    public async Task Update_ОтсекаетФайлыВнеRoot()
    {
        // Escape-гард: changedFile вне rootPath не должен читаться/попадать в перестроение
        // (SafeJoin-аналог; rel-путь с ".." отсекается до чтения с диска).
        await BuildAsync(("A.cs", "public class A { }"));
        await _provider.BuildAsync(_tempDir, CancellationToken.None);

        // «Чужой» файл рядом с проектом, но вне его корня.
        var outsideDir = Path.Combine(Path.GetTempPath(), "cgraph_outside_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        try
        {
            var outsideFile = Path.Combine(outsideDir, "Outside.cs");
            await File.WriteAllTextAsync(outsideFile, "public class Outside { }");

            var updated = await _provider.UpdateAsync(_tempDir,
                new[] { outsideFile }, CancellationToken.None);

            updated.Nodes.Values.Should().NotContain(n => n.Label == "Outside",
                "файл вне rootPath отсекается escape-гардом и не попадает в граф");
            updated.Nodes.Values.Should().Contain(n => n.Label == "A",
                "собственные узлы проекта на месте (пустой changed → полный rebuild)");
        }
        finally
        {
            Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public async Task Update_RefreshesChangedFile_AndInvalidatesEdges()
    {
        // Исходное состояние: Foo ссылается на Bar.
        await BuildAsync(
            ("Foo.cs", "public class Foo { public Bar _bar; }"),
            ("Bar.cs", "public class Bar { }"));
        var built = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        HasEdge(built, "Foo", "Bar", EdgeRelation.References).Should().BeTrue();

        // Меняем Foo.cs: больше не ссылается на Bar (поле убрано).
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Foo.cs"), "public class Foo { }");

        var updated = await _provider.UpdateAsync(_tempDir,
            new[] { Path.Combine(_tempDir, "Foo.cs") }, CancellationToken.None);

        HasEdge(updated, "Foo", "Bar", EdgeRelation.References).Should().BeFalse("ребро из изменённого файла инвалидировано");
        // Bar остаётся узлом (его файл не трогали)
        updated.Nodes.Values.Should().Contain(n => n.Label == "Bar");
    }

    [Fact]
    public async Task Update_AddsNewType_FromChangedFile()
    {
        await BuildAsync(("A.cs", "public class A { }"));
        await _provider.BuildAsync(_tempDir, CancellationToken.None);

        // Добавляем новый тип через изменение файла.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.cs"),
            "public class A { }\npublic class Added { }");

        var updated = await _provider.UpdateAsync(_tempDir,
            new[] { Path.Combine(_tempDir, "A.cs") }, CancellationToken.None);

        updated.Nodes.Values.Should().Contain(n => n.Label == "Added");
    }

    [Fact]
    public async Task Build_RelPathsNormalized_WithForwardSlash()
    {
        // Независимо от разделителя платформы — SourceFile через '/' (стабильно в стор).
        var sub = Path.Combine("Sub", "Deep.cs").Replace('\\', '/');
        await BuildAsync((sub, "public class Deep { }"));

        var graph = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        var node = graph.Nodes.Values.Single(n => n.Label == "Deep");
        node.SourceFile.Should().Be(sub);
        node.SourceLocation.Should().StartWith("line ");
    }

    [Fact]
    public async Task Build_FqnStableForNamespacedTypes()
    {
        var graph = await BuildAsync(("Ns.cs", """
            namespace App.Services;
            public class Widget { }
        """));

        var node = graph.Nodes.Values.Single(n => n.Label == "Widget");
        node.FullyQualifiedName.Should().Be("App.Services.Widget");
        node.Id.Should().Be(node.FullyQualifiedName, "Id = FQN");
    }

    // MAJOR 3: orphan-source. При удалении/переименовании типа рёбра из старого Id не должны
    // выживать с висящим Source. До фикса changed-детект шёл по lookup'у в НОВЫХ nodes:
    // исчезнувший Source давал null → "" → не попадал в changedSet → ребро оставалось,
    // а orphan-cleanup (только по target) его не трогал. Фикс берёт SourceFile из СТАРОГО кэша
    // и гасит orphan-концы с обеих сторон.

    [Fact]
    public async Task Update_ПереименовываетТип_ИУбираетРебраИзСтарогоИд()
    {
        await BuildAsync(
            ("A.cs", "public class A { public B _b; }"),
            ("B.cs", "public class B { }"));
        var built = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        var oldAId = Id(built, "A");
        HasEdge(built, "A", "B", EdgeRelation.References).Should().BeTrue();

        // Класс A переименован в A2 (файл изменился, старый Id исчез).
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.cs"), "public class A2 { public B _b; }");

        var updated = await _provider.UpdateAsync(_tempDir,
            new[] { Path.Combine(_tempDir, "A.cs") }, CancellationToken.None);

        updated.Nodes.Values.Should().NotContain(n => n.Label == "A", "старый тип A исчез");
        updated.Nodes.Values.Should().Contain(n => n.Label == "A2");
        // Ребро из старого Id A (висящий Source) не выжило — orphan-cleanup по source.
        updated.Edges.Should().NotContain(e => e.Source == oldAId,
            "ребро из переименованного Id A не должно висеть с отсутствующим Source");
        // Новое ребро A2 → B появилось (changed-файл перестроен).
        HasEdge(updated, "A2", "B", EdgeRelation.References).Should().BeTrue();
    }

    [Fact]
    public async Task Update_УдаляетТип_ИГаситРебраИзНего()
    {
        await BuildAsync(
            ("A.cs", "public class A { public B _b; }"),
            ("B.cs", "public class B { }"));
        var built = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        var aId = Id(built, "A");

        // Файл очищен — типа A больше нет.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "A.cs"), "// без типов");

        var updated = await _provider.UpdateAsync(_tempDir,
            new[] { Path.Combine(_tempDir, "A.cs") }, CancellationToken.None);

        updated.Nodes.Values.Should().NotContain(n => n.Label == "A");
        updated.Nodes.Values.Should().Contain(n => n.Label == "B", "B в untouched-файле, остаётся");
        updated.Edges.Should().NotContain(e => e.Source == aId,
            "ребро из удалённого типа A — висящий Source, гасим");
    }

    // MAJOR 4: _cache RMW под конкурентными GetAsync(Build)/Rebuild(Update). Сериалайзер
    // per-rootPath не даёт потерять обновление и не дедлокится на реентерабельном вызове.

    [Fact]
    public async Task ПараллельныеВызовы_НеБросаютИСогласованы()
    {
        // База: A↔B цикл + 10 «фоновых» типов, каждый Update крутит свой файл (без ФС-гонок).
        await BuildAsync(
            ("A.cs", "public class A { public B _b; }"),
            ("B.cs", "public class B { public A _a; }"));
        for (int i = 0; i < 10; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"W{i}.cs"),
                $"public class W{i} {{ public B _b; }}");
        await _provider.BuildAsync(_tempDir, CancellationToken.None);

        // 10 параллельных Build (как REST-чтение при пустом персистентном кэше) +
        // 10 параллельных Update на разные файлы (как фон по дебаунсу).
        var buildTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => _provider.BuildAsync(_tempDir, CancellationToken.None)));
        var updateTasks = Enumerable.Range(0, 10).Select(i => Task.Run(async () =>
        {
            var file = Path.Combine(_tempDir, $"W{i}.cs");
            await File.WriteAllTextAsync(file,
                i % 2 == 0 ? $"public class W{i} {{ }}" : $"public class W{i} {{ public A _a; }}");
            await _provider.UpdateAsync(_tempDir, new[] { file }, CancellationToken.None);
        }));

        var all = buildTasks.Concat(updateTasks).ToArray();
        var ex = await Record.ExceptionAsync(() => Task.WhenAll(all));
        ex.Should().BeNull("параллельные Build/Update одного проекта не должны бросать и не дедлокиться");

        // Финальная согласованность: после чистого rebuild ни одно ребро не висит на отсутствующем узле.
        var final = await _provider.BuildAsync(_tempDir, CancellationToken.None);
        foreach (var edge in final.Edges)
        {
            final.Nodes.Should().ContainKey(edge.Source, "висящий Source после rebuild");
            final.Nodes.Should().ContainKey(edge.Target, "висящий Target после rebuild");
        }
    }

    // HOTFIX прода: мусорные каталоги (.claude/bin/obj/node_modules/packages…) не должны
    // раздувать detect и валить граф в regex-fallback. Баг: на ClaudeCodeServer 7372 .cs
    // в .claude/ → 7878 > порога 5000 → Roslyn не звался, UI «Граф» висел на regex-сборке.

    [Fact]
    public void EnumerateCsFiles_ПропускаетМусорныеКаталоги()
    {
        // Мусорные подкаталоги с .cs на разной глубине + единственный реальный файл.
        foreach (var junk in new[]
        {
            ".claude/plugins/junk", "bin/Debug/net9.0", "obj/Debug/net9.0",
            "node_modules/pkg", "packages/Lib.1.0/lib", ".vs", "TestResults",
        })
        {
            var path = Path.Combine(_tempDir, junk.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "Junk.cs"), "public class Junk { }");
        }
        File.WriteAllText(Path.Combine(_tempDir, "Real.cs"), "public class Real { }");

        var cs = CompilationBuilder.EnumerateCsFiles(_tempDir)
            .Select(p => p.Replace('\\', '/'))
            .ToList();

        // Обнаружен только реальный файл — ни одного .cs из мусорных каталогов.
        cs.Should().ContainSingle("мусорные каталоги отсечены на обходе")
          .Which.Should().EndWith("Real.cs");
    }

    [Fact]
    public async Task Build_ИсключаетМусорныеКаталоги_ИДержитRoslynПорог()
    {
        // Мусор не считается в detect → проект не падает в regex-fallback, граф строится через Roslyn.
        await BuildAsync(
            // Кеш oh-my-claudecode в .claude — главный виновник бага прода.
            (".claude/plugins/junk/Cache.cs", "public class PluginJunk { }"),
            ("bin/Debug/net9.0/Artifact.cs", "public class BuildArtifact { }"),
            ("obj/Debug/net9.0/Generated.cs", "public class ObjGen { }"),
            ("node_modules/pkg/sample.cs", "public class NpmSample { }"),
            ("packages/Lib.1.0/lib/Foo.cs", "public class NugetLib { }"),
            // Реальный код проекта — survives
            ("Client.cs", "public class Client { public Service Svc = new Service(); }"),
            ("Service.cs", "public class Service { }"));

        var graph = await _provider.BuildAsync(_tempDir, CancellationToken.None);

        // Мусорные типы не попали в граф.
        graph.Nodes.Values.Select(n => n.Label)
            .Should().NotContain(new[] { "PluginJunk", "BuildArtifact", "ObjGen", "NpmSample", "NugetLib" });
        // Реальный код на месте.
        graph.Nodes.Values.Select(n => n.Label)
            .Should().Contain(new[] { "Client", "Service" });
        // Ребро построено — значит Roslyn-путь (regex-fallback рёбер не строит).
        HasEdge(graph, "Client", "Service", EdgeRelation.Calls).Should().BeTrue(
            "мусор не свалил detect в regex-fallback — граф построен через Roslyn с рёбрами");
    }
}
