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

    // location — «line N» у C#-узлов; у TS-узлов экстрактор SourceLocation не заполняет (пустая строка)
    private static CodeGraphNode Node(string id, string file, string location = "line 1") => new()
    {
        Id = id, Label = id[(id.LastIndexOf('.') + 1)..], FullyQualifiedName = id,
        SourceFile = file, SourceLocation = location, Kind = NodeKind.Class,
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
        DeleteWithRetry(dir);
        DeleteWithRetry(data);
    }

    // Фоновая перестройка графа (StartRebuildIfIdle) может ещё писать в каталог данных:
    // повторяем удаление, пока она не отпустит файлы
    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(100);
            }
        }
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

    private static CodeGraphEdge Edge(string from, string to, EdgeRelation relation) => new()
    {
        Source = from, Target = to, Relation = relation, Confidence = EdgeConfidence.Extracted,
    };

    // Дерево из заданных файлов; у каждого — deps исходящих целей в чужих файлах и consumers потребителей
    private static async Task<(ConsumersTailContributor Sut, string Dir, string Data)> PrepareMany(
        string[] files, int deps, int consumers, Func<string, string, CancellationToken, Task<CodeGraphNeighborsOutcome>>? dependencies = null,
        string depLocation = "line 1")
    {
        var dir = Path.Combine(Path.GetTempPath(), "cgtail_" + Guid.NewGuid().ToString("N")[..10]);
        var data = Path.Combine(Path.GetTempPath(), "cgtail_data_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(data);
        var nodes = new Dictionary<string, CodeGraphNode>();
        var edges = new List<CodeGraphEdge>();
        for (var f = 0; f < files.Length; f++)
        {
            var full = Path.Combine(dir, files[f]);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
            var owner = $"A.F{f}";
            nodes[owner] = Node(owner, files[f]);
            for (var i = 0; i < deps; i++)
            {
                var id = $"D.F{f}D{i:D2}";
                nodes[id] = Node(id, $"dep/F{f}D{i:D2}.cs", depLocation);
                edges.Add(Edge(owner, id, i % 2 == 0 ? EdgeRelation.Calls : EdgeRelation.References));
            }
            for (var i = 0; i < consumers; i++)
            {
                var id = $"C.F{f}C{i:D2}";
                nodes[id] = Node(id, $"use/F{f}C{i:D2}.cs");
                edges.Add(Edge(id, owner));
            }
        }
        var persistence = new GraphPersistence(data, NullLogger<GraphPersistence>.Instance);
        await persistence.SaveAsync(dir, new CodeGraph { Nodes = nodes, Edges = edges }, CancellationToken.None);
        var graphs = new CodeGraphService(NullLogger<CodeGraphService>.Instance, null!, persistence,
            new ConfigurationBuilder().Build());
        var sut = new ConsumersTailContributor(graphs, new CodeGraphQueryService(graphs), new OpenGate(),
            NullLogger<ConsumersTailContributor>.Instance, null, null, dependencies);
        return (sut, dir, data);
    }

    [Fact]
    public async Task Зависимости_ИсходящиеСвязиСОбъявлениемПутьСтрока()
    {
        var (sut, dir, data) = await PrepareMany(["src/Lib.cs"], deps: 3, consumers: 0);
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), "Правь src/Lib.cs"))!.Sections.Single().Text;

            text.Should().Contain("Зависимости src/Lib.cs");
            text.Should().Contain("- D.F0D00 — dep/F0D00.cs:1 (Calls)");
            text.Should().Contain("- D.F0D01 — dep/F0D01.cs:1 (References)");
            text.Should().NotContain("Потребители", "входящих связей нет — подраздел не заводится");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task Зависимости_CSharpFormа_СтрокаИзLineN_ФайлДвоеточиеСтрока()
    {
        var (sut, dir, data) = await PrepareMany(["src/Lib.cs"], deps: 1, consumers: 0, depLocation: "line 42");
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), "Правь src/Lib.cs"))!.Sections.Single().Text;

            text.Should().Contain("- D.F0D00 — dep/F0D00.cs:42 (Calls)");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task Зависимости_TsФорма_БезSourceLocation_ТолькоФайл()
    {
        // реальный фронтовый файл: TS-экстрактор строки не отдаёт, контракт честно сужен до «файл»
        var (sut, dir, data) = await PrepareMany(["frontend/src/lib/api.ts"], deps: 1, consumers: 0, depLocation: "");
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), "Правь frontend/src/lib/api.ts"))!.Sections.Single().Text;

            text.Should().Contain("Зависимости frontend/src/lib/api.ts");
            text.Should().Contain("- D.F0D00 — dep/F0D00.cs (Calls)", "строки нет — только файл, без «:»");
            text.Should().NotContain("dep/F0D00.cs:");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public void Сторожа_ТаблицаУкладываетсяВПотолокПодраздела()
    {
        foreach (var e in ContractGuardMap.Entries)
            (1 + ContractGuardMap.For(e.Prefix + "x.cs").Count)
                .Should().BeLessThanOrEqualTo(ConsumersTailContributor.GuardLineLimit,
                    $"подраздел «Сторожа по пути» для {e.Prefix} не должен упираться в потолок молча");
        var rendered = ConsumersTailContributor.RenderGuards("backend/ClaudeHomeServer.Core/Services/Foo.cs")!;
        rendered.Split('\n').Length.Should().BeLessThanOrEqualTo(ConsumersTailContributor.GuardLineLimit);
    }

    [Fact]
    public void Сторожа_ПревышениеПотолка_РежетсяДоПотолкаСЕщёN()
    {
        // у Core четыре сторожа; потолок 3 строки → заголовок, один сторож и «ещё 3»
        var lines = ConsumersTailContributor.RenderGuards("backend/ClaudeHomeServer.Core/Services/Foo.cs", maxLines: 3)!
            .Split('\n');

        lines.Length.Should().Be(3);
        lines[^1].Should().StartWith("ещё ");
    }

    [Fact]
    public async Task Зависимости_НазванныеВПостановкеСимволы_ИдутПервыми()
    {
        var (sut, dir, data) = await PrepareMany(["src/Lib.cs"], deps: 25, consumers: 0);
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), "Правь src/Lib.cs: нужен вызов F0D24"))!.Sections.Single().Text;

            var lines = text.Split('\n');
            lines.Length.Should().BeLessThanOrEqualTo(1 + 20, "потолок подраздела — 20 строк");
            lines[2].Should().Contain("D.F0D24", "первая строка после заголовка подраздела — названный символ");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task Сторожа_ПоПутиФайла_ДляFrontendИCore()
    {
        var (sut, dir, data) = await PrepareMany(
            ["frontend/src/lib/offline.ts", "backend/ClaudeHomeServer.Core/Services/Foo.cs"], deps: 0, consumers: 1);
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir),
                "Правь frontend/src/lib/offline.ts и backend/ClaudeHomeServer.Core/Services/Foo.cs"))!.Sections.Single().Text;

            text.Should().Contain("Сторожа по пути frontend/src/lib/offline.ts");
            text.Should().Contain("- frontend/ → npm run lint:design").And.Contain("ui_probe");
            text.Should().Contain("SubsystemBoundaryTests.CoreDll_СодержитТолькоРазрешённыеНеймспейсы");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task Потолки_ТриФайлаПо30Зависимостей_СекцияВ120Строк_СторожаНеРежутся()
    {
        // четыре файла в тексте — берутся три; на каждом 30 зависимостей, 30 потребителей и сторожа frontend/
        var files = new[] { "frontend/src/a.ts", "frontend/src/b.ts", "frontend/src/c.ts", "frontend/src/d.ts" };
        var (sut, dir, data) = await PrepareMany(files, deps: 30, consumers: 30);
        try
        {
            var text = (await sut.BuildAsync(Ctx(dir), string.Join(" ", files.Select(f => $"Правь {f}"))))!
                .Sections.Single().Text;

            var lines = text.Split('\n');
            lines.Length.Should().BeLessThanOrEqualTo(ConsumersTailContributor.SectionLineLimit);
            text.Should().NotContain("frontend/src/d.ts", "четвёртый файл сверх потолка файлов");
            foreach (var f in files.Take(3))
                text.Should().Contain($"Сторожа по пути {f}");
            lines.Count(l => l.Contains("npm run lint:design")).Should().Be(3, "сторожа не режутся никогда");
            var depLines = lines.Count(l => l.StartsWith("- D.F"));
            var consLines = lines.Count(l => l.StartsWith("- use/"));
            depLines.Should().BeLessThan(consLines, "зависимости режутся первыми");
            text.Should().Contain("ещё ");
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public async Task ОшибкаГрафаЗависимостей_СекцииНет_ИсключенияНет()
    {
        var (sut, dir, data) = await PrepareMany(["src/Lib.cs"], deps: 2, consumers: 2,
            dependencies: (_, _, _) => throw new InvalidOperationException("граф упал"));
        try
        {
            (await sut.BuildAsync(Ctx(dir), "Правь src/Lib.cs")).Should().BeNull();
        }
        finally { Cleanup(dir, data); }
    }

    [Fact]
    public void КлассыИМетодыИзContractGuardMap_СуществуютВИсходникахСвоихПроектов()
    {
        // Корень репозитория — вверх от каталога тестовой сборки до backend/ClaudeHomeServer.slnx
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "backend", "ClaudeHomeServer.slnx")))
            root = root.Parent;
        root.Should().NotBeNull("тест ищет сторожей по исходникам репозитория");
        var guards = ContractGuardMap.Entries.SelectMany(e => e.Guards).Where(g => g.TestClass is not null).ToList();
        guards.Should().NotBeEmpty();
        // Класс ищется только в проекте, к которому он привязан в таблице; исходники проекта читаем один раз
        var sourcesByProject = new Dictionary<string, List<string>>();
        foreach (var g in guards)
        {
            g.TestProject.Should().NotBeNullOrEmpty($"у сторожа {g.TestClass} в таблице не указан его тестовый проект");
            if (sourcesByProject.ContainsKey(g.TestProject!)) continue;
            sourcesByProject[g.TestProject!] = Directory
                .EnumerateFiles(Path.Combine(root!.FullName, "backend", g.TestProject!), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText).ToList();
        }

        foreach (var g in guards)
        {
            var cls = new System.Text.RegularExpressions.Regex($@"\bclass\s+{g.TestClass}\b");
            var body = sourcesByProject[g.TestProject!].Where(src => cls.IsMatch(src)).ToList();
            body.Should().NotBeEmpty($"класс сторожа {g.TestClass} должен существовать в проекте {g.TestProject}");
            if (g.TestMethod is not null)
            {
                // объявление метода: возвращаемый тип, имя и открывающая скобка, а не любое упоминание
                var method = new System.Text.RegularExpressions.Regex(
                    $@"\b(?:void|Task(?:<[^>]+>)?)\s+{g.TestMethod}\s*\(");
                body.Should().Contain(src => method.IsMatch(src),
                    $"метод сторожа {g.TestClass}.{g.TestMethod} должен быть объявлен");
            }
        }
    }
}
