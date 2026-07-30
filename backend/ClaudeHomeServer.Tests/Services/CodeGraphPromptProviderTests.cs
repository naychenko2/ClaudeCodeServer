using ClaudeHomeServer.Services.CodeGraph;
using ClaudeHomeServer.Services.CodeGraph.Core;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Per-ход slice top-10 god-nodes Code Graph в системный промпт (ADR вариант A).
/// Покрывает: формирование блока для построенного графа, null-гейты (нет графа / нет god-узлов),
/// метку устаревания при isStale, кэш по сигнатуре (mtime graph.json) — повторный ход не
/// перезагружает граф, а свежий isStale-чек досчитывается на попадании кэша.
/// Граф кладётся напрямую в стор (GraphPersistence), провайдер работает поверх реального
/// CodeGraphService — проверяется именно слой slice, а не построение Roslyn.
/// </summary>
public class CodeGraphPromptProviderTests
{
    private static string MkRootDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cgpp_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (CodeGraphPromptProvider Provider, CodeGraphService Graphs, GraphPersistence Persistence)
        MkProvider()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cgpp_data_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dataDir);
        var persistence = new GraphPersistence(dataDir, NullLogger<GraphPersistence>.Instance);
        // _projects провайдером не используется (GetSnapshotAsync/GetCacheSignature/IsStaleFor
        // работают по персистентности и статическому mtime-чеку) — null! допустим в тесте.
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, null!, persistence,
            new ConfigurationBuilder().Build());
        var provider = new CodeGraphPromptProvider(graphs, NullLogger<CodeGraphPromptProvider>.Instance);
        return (provider, graphs, persistence);
    }

    private static CodeGraphNode Node(string id, string file) => new()
    {
        Id = id,
        Label = id[(id.LastIndexOf('.') + 1)..],
        FullyQualifiedName = id,
        SourceFile = file,
        SourceLocation = "L1",
        Kind = NodeKind.Class,
    };

    // Хаб на 12 листьев (degree хаба = 12 ≥ порога god-узлов 10) + пара изолированных листьев.
    private static CodeGraph HubGraph(int leaves = 12)
    {
        var nodes = new Dictionary<string, CodeGraphNode>
        {
            ["Demo.Hub"] = Node("Demo.Hub", "Hub.cs"),
        };
        var edges = new List<CodeGraphEdge>();
        for (int i = 0; i < leaves; i++)
        {
            var id = $"Demo.Leaf{i}";
            nodes[id] = Node(id, $"Leaf{i}.cs");
            edges.Add(new() { Source = "Demo.Hub", Target = id, Relation = EdgeRelation.References, Confidence = EdgeConfidence.Extracted });
        }
        return new() { Nodes = nodes, Edges = edges };
    }

    private static void WriteCs(string dir)
    {
        // .cs нужны, чтобы IsStale было что сравнивать; пишем ДО построения → mtime < BuiltAt → не stale.
        File.WriteAllText(Path.Combine(dir, "Hub.cs"), "namespace Demo { public class Hub {} }");
    }

    [Fact]
    public async Task GetSliceAsync_ПостроенныйГраф_ВозвращаетSliceСGodУзлом()
    {
        var dir = MkRootDir();
        WriteCs(dir);
        var (provider, _, persistence) = MkProvider();
        await persistence.SaveAsync(dir, HubGraph(), CancellationToken.None);

        var slice = await provider.GetSliceAsync(dir);

        slice.Should().NotBeNull("построенный граф с god-узлом даёт блок");
        slice.Should().Contain("Code Graph");
        slice.Should().Contain("Demo.Hub");
        slice.Should().Contain("(Hub.cs)");
        slice.Should().Contain("12 связей");
        // Дверь к остальному графу — MCP-инструменты: REST и панель для агента недоступны
        slice.Should().Contain("codegraph_find");
        slice.Should().Contain("codegraph_neighbors");
        slice.Should().Contain("codegraph_hubs");
        slice.Should().NotContain("устаревшим", "исходники не менялись после построения");
    }

    [Fact]
    public async Task GetSliceAsync_ГрафНеПостроен_ВозвращаетNull()
    {
        var dir = MkRootDir();
        var (provider, _, _) = MkProvider();

        var slice = await provider.GetSliceAsync(dir);

        slice.Should().BeNull("граф для проекта ещё не строился");
    }

    [Fact]
    public async Task GetSliceAsync_ГрафБезGodУзлов_ВозвращаетNull()
    {
        var dir = MkRootDir();
        WriteCs(dir);
        var (provider, _, persistence) = MkProvider();
        // 2 узла, 1 ребро — degree максимум 1, порог god-узлов (10) не достигнут.
        var graph = new CodeGraph
        {
            Nodes = new() { ["Demo.A"] = Node("Demo.A", "A.cs"), ["Demo.B"] = Node("Demo.B", "B.cs") },
            Edges = new() { new() { Source = "Demo.A", Target = "Demo.B", Relation = EdgeRelation.References, Confidence = EdgeConfidence.Extracted } },
        };
        await persistence.SaveAsync(dir, graph, CancellationToken.None);

        var slice = await provider.GetSliceAsync(dir);

        slice.Should().BeNull("god-узлов нет — блока быть не должно");
    }

    [Fact]
    public async Task GetSliceAsync_ПустойRootPath_ВозвращаетNull()
    {
        var (provider, _, _) = MkProvider();
        (await provider.GetSliceAsync("")).Should().BeNull();
        (await provider.GetSliceAsync(null)).Should().BeNull();
        (await provider.GetSliceAsync("   ")).Should().BeNull();
    }

    [Fact]
    public async Task GetSliceAsync_ИсходникиИзменилисьПослеПостроения_ДобавляетМеткуStale()
    {
        var dir = MkRootDir();
        WriteCs(dir);
        var (provider, _, persistence) = MkProvider();
        await persistence.SaveAsync(dir, HubGraph(), CancellationToken.None);

        // Сдвигаем mtime .cs в будущее — детерминированно позже BuiltAt, без sleep.
        File.SetLastWriteTimeUtc(Path.Combine(dir, "Hub.cs"), DateTime.UtcNow.AddSeconds(30));

        var slice = await provider.GetSliceAsync(dir);

        slice.Should().NotBeNull();
        slice.Should().Contain("[может быть устаревшим — файлы изменились]");
    }

    [Fact]
    public async Task GetSliceAsync_ГрафаДереваНет_ОтдаётSliceГлавнойВеткиСПометкой()
    {
        // Чат в отдельном worktree: свой граф ещё не построен, граф корня проекта есть.
        var main = MkRootDir();
        WriteCs(main);
        var worktree = MkRootDir();
        var (provider, _, persistence) = MkProvider();
        await persistence.SaveAsync(main, HubGraph(), CancellationToken.None);

        var slice = await provider.GetSliceAsync(worktree, main);

        slice.Should().NotBeNull("пустой промпт хуже приблизительного — отдаём срез главной ветки");
        slice.Should().Contain("Demo.Hub");
        slice.Should().Contain("ГЛАВНОЙ ветки", "агент должен знать, что срез не от его дерева");
    }

    [Fact]
    public async Task GetSliceAsync_ГрафДереваЕсть_FallbackНеПрименяется()
    {
        var main = MkRootDir();
        WriteCs(main);
        var worktree = MkRootDir();
        WriteCs(worktree);
        var (provider, _, persistence) = MkProvider();
        // У дерева свой граф — пометка про главную ветку не нужна.
        await persistence.SaveAsync(main, HubGraph(leaves: 11), CancellationToken.None);
        await persistence.SaveAsync(worktree, HubGraph(leaves: 14), CancellationToken.None);

        var slice = await provider.GetSliceAsync(worktree, main);

        slice.Should().NotBeNull();
        slice.Should().Contain("14 связей", "slice построен по графу самого дерева");
        slice.Should().NotContain("ГЛАВНОЙ ветки");
    }

    [Fact]
    public async Task GetSliceAsync_ГрафовНетНиУДереваНиУПроекта_ВозвращаетNull()
    {
        var (provider, _, _) = MkProvider();
        (await provider.GetSliceAsync(MkRootDir(), MkRootDir())).Should().BeNull();
    }

    [Fact]
    public async Task GetSliceAsync_ТеЖеИзменения_ПереиспользуетКэшБезПерезагрузкиГрафа()
    {
        var dir = MkRootDir();
        WriteCs(dir);
        var (provider, _, persistence) = MkProvider();
        await persistence.SaveAsync(dir, HubGraph(), CancellationToken.None);

        var first = await provider.GetSliceAsync(dir);
        var loadsAfterFirst = provider.SnapshotLoads;
        var second = await provider.GetSliceAsync(dir);
        var loadsAfterSecond = provider.SnapshotLoads;

        first.Should().Be(second, "граф не перестраивался — slice берётся из кэша");
        loadsAfterFirst.Should().Be(1, "первый ход загружает снимок");
        loadsAfterSecond.Should().Be(1, "повторный ход не должен дёргать GetSnapshotAsync повторно");
    }

    [Fact]
    public async Task GetSliceAsync_StaleВоВремяКэша_ДосчитываетМеткуБезПерезагрузки()
    {
        // 1) свежий граф — slice без метки; кэш заполнен.
        var dir = MkRootDir();
        WriteCs(dir);
        var (provider, _, persistence) = MkProvider();
        await persistence.SaveAsync(dir, HubGraph(), CancellationToken.None);

        var fresh = await provider.GetSliceAsync(dir);
        fresh.Should().NotContain("устаревшим");
        provider.SnapshotLoads.Should().Be(1);

        // 2) файлы изменились, НО граф ещё не перестроен (сигнатура graph.json та же) →
        //    попадание в кэш; дешёвый isStale-чек должен добавить метку без перезагрузки графа.
        File.SetLastWriteTimeUtc(Path.Combine(dir, "Hub.cs"), DateTime.UtcNow.AddSeconds(30));

        var stale = await provider.GetSliceAsync(dir);
        stale.Should().Contain("[может быть устаревшим — файлы изменились]");
        provider.SnapshotLoads.Should().Be(1, "сигнатура та же — граф не перезагружается");
    }
}
