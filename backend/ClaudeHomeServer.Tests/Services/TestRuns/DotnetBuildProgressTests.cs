using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Разбор `dotnet build` на записанных образцах (живой вывод SDK 10 на решении из трёх
/// проектов: холодный с restore, no-op, с ошибкой компиляции), процент по весам прошлого
/// прогона и оценка M обходом ProjectReference.
/// </summary>
public class DotnetBuildProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-buildprogress-" + Guid.NewGuid().ToString("N"));

    public DotnetBuildProgressTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* занят */ }
        foreach (var dir in _cleanup)
            try { Directory.Delete(dir, recursive: true); } catch { /* занят */ }
        GC.SuppressFinalize(this);
    }

    private static string[] Sample(string name) => File.ReadAllLines(TestRunServiceTests.Fixture(name));

    private static DotnetBuildProgress Feed(DotnetBuildProgress progress, IEnumerable<string> lines)
    {
        var t = TimeSpan.Zero;
        foreach (var line in lines) progress.Feed(line, t += TimeSpan.FromSeconds(1));
        return progress;
    }

    [Theory]
    [InlineData("dotnet-build-cold.txt", 3)]
    [InlineData("dotnet-build-noop.txt", 3)]
    [InlineData("dotnet-build-error.txt", 1)]
    public void Образцы_СтрокиПроектовСчитаютсяТочно(string sample, int projects)
    {
        var progress = Feed(new DotnetBuildProgress(null, null), Sample(sample));

        progress.Done.Should().Be(projects, "строка `->` — на каждый готовый проект, restore и ошибки не в счёт");
        progress.Label().Should().Be($"{projects} {DotnetBuildProgress.Projects(projects)}");
        progress.Percent(TimeSpan.FromSeconds(5)).Should().BeNull("ни памяти, ни оценки — процент не выдумываем");
    }

    [Fact]
    public void Образец_СОшибкой_ПерваяОшибкаБезПовтораИзСводки()
    {
        var errors = VsTestConsoleParser.BuildErrors(Sample("dotnet-build-error.txt"));

        errors.Should().ContainSingle("MSBuild печатает ошибку дважды — по ходу и в сводке")
            .Which.Should().Contain("error CS0103").And.NotContain("Alpha.Data.csproj]");
    }

    [Fact]
    public void ParseProjectLine_ТолькоСтрокаГотовогоПроекта()
    {
        DotnetBuildProgress.ParseProjectLine(@"  Alpha.Core -> C:\src\alpha\Alpha.Core\bin\Debug\net10.0\Alpha.Core.dll")
            .Should().Be("Alpha.Core");
        DotnetBuildProgress.ParseProjectLine(@"  Restored C:\src\alpha\Alpha.Core\Alpha.Core.csproj (in 81 ms).").Should().BeNull();
        DotnetBuildProgress.ParseProjectLine(@"C:\src\x.cs(1,1): error CS0103: a -> b").Should().BeNull("ошибка — не проект");
        DotnetBuildProgress.ParseProjectLine("Build succeeded.").Should().BeNull();
    }

    // Хронология холодной сборки бэкенда из разведки: 39 проектов готовы к 50-й секунде, последний
    // (сам ClaudeHomeServer) — к 77,7-й
    private static BuildRunMemory ColdBackend()
    {
        var finished = Enumerable.Range(1, 39).ToDictionary(i => $"P{i}", i => 8.6 + (50.0 - 8.6) * i / 39);
        finished["ClaudeHomeServer"] = 77.7;
        return new BuildRunMemory(40, 77.7, finished);
    }

    private static DotnetBuildProgress ThirtyNineOfForty()
    {
        var progress = new DotnetBuildProgress(ColdBackend(), estimatedTotal: null);
        for (var i = 1; i <= 39; i++) progress.Feed($"  P{i} -> /src/P{i}.dll", TimeSpan.FromSeconds(50));
        return progress;
    }

    [Fact]
    public void ПоПамяти_39из40_НеВисятНа97Процентах()
    {
        var progress = ThirtyNineOfForty();

        progress.Label().Should().Be("39 из 40 проектов", "M из памяти — точный счётчик, без «≈»");
        progress.Percent(TimeSpan.FromSeconds(50)).Should().BeInRange(60, 66,
            "к 39-му проекту прошлый раз ушло 50 из 77,7 с ≈ 64%, а не 39/40 = 97%");
    }

    [Fact]
    public void ПоПамяти_НаПоследнемПроекте_ПолосаПолзётПоВремени()
    {
        var progress = ThirtyNineOfForty();

        var at50 = progress.Percent(TimeSpan.FromSeconds(50))!.Value;
        var at65 = progress.Percent(TimeSpan.FromSeconds(65))!.Value;
        var at75 = progress.Percent(TimeSpan.FromSeconds(75))!.Value;

        at65.Should().BeGreaterThan(at50, "последняя треть сборки — без строк, но полоса не стоит");
        at75.Should().BeGreaterThan(at65);
        progress.Percent(TimeSpan.FromSeconds(500)).Should().Be(99, "пока последний не готов — не 100");

        progress.Feed("  ClaudeHomeServer -> /src/ClaudeHomeServer.dll", TimeSpan.FromSeconds(78));
        progress.Percent(TimeSpan.FromSeconds(78)).Should().Be(99, "«готово» скажет результат, а не полоса");
    }

    [Fact]
    public void ПоПамяти_ВремяНеОбгоняетСледующийНеготовыйПроект()
    {
        var memory = new BuildRunMemory(3, 10, new Dictionary<string, double> { ["A"] = 2, ["B"] = 5, ["C"] = 10 });
        var progress = new DotnetBuildProgress(memory, null);
        progress.Feed("  A -> /a.dll", TimeSpan.FromSeconds(2));

        progress.Percent(TimeSpan.FromSeconds(9)).Should().Be(50,
            "медленная сборка: B в прошлый раз был готов к 50%, дальше без его строки не идём");
        progress.Percent(TimeSpan.FromSeconds(1)).Should().Be(20, "готовый A — уже 20%, даже если время отстаёт");
    }

    [Fact]
    public void БезПамяти_ОценкаСПотолком90()
    {
        var progress = new DotnetBuildProgress(null, estimatedTotal: 3);
        progress.Snapshot(TimeSpan.Zero).Should().Be(new TestRunProgress("build", "≈0 из 3 проектов", 0, Exact: false));

        Feed(progress, Sample("dotnet-build-cold.txt"));
        progress.Label().Should().Be("≈3 из 3 проектов");
        progress.Percent(TimeSpan.FromSeconds(10)).Should().Be(DotnetBuildProgress.EstimateCeiling,
            "оценка по XML — не повод рисовать 100%");

        progress.Feed("  Extra -> /x.dll", TimeSpan.FromSeconds(11));
        progress.Total.Should().Be(4, "сборка насчитала больше оценки — M растёт, а не «4 из 3»");
    }

    // Каталог памяти — как data/build-memory сервера: ВНЕ рабочего дерева
    private string MemoryDir()
    {
        var dir = Outside();
        return Path.Combine(dir, BuildRunMemory.DirName);
    }

    [Fact]
    public void Память_ТолькоУспешнойСборки_ЧитаетсяОбратно()
    {
        var progress = Feed(new DotnetBuildProgress(null, null), Sample("dotnet-build-noop.txt"));
        var memoryDir = MemoryDir();
        var path = BuildRunMemory.PathFor(memoryDir, _root, "backend/App/App.csproj");

        progress.ToMemory(TimeSpan.FromSeconds(7)).Save(path);
        var loaded = BuildRunMemory.Load(path);

        // Регистр префикса на Windows сведён к нижнему: цели одной записи — один файл
        Path.GetFileName(path).ToLowerInvariant().Should().StartWith("backend_app_app.csproj-").And.EndWith(".json");
        Path.GetDirectoryName(path).Should().Be(memoryDir, "память живёт в каталоге сервера, а не в дереве");
        loaded!.Total.Should().Be(3);
        loaded.Seconds.Should().Be(7);
        loaded.Finished.Keys.Should().BeEquivalentTo("Alpha.Core", "Alpha.Data", "Alpha.App");
        Directory.GetFiles(memoryDir).Should().ContainSingle("временный файл после записи убран");
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty("в рабочее дерево память не пишется");

        File.WriteAllText(path, "{ мусор");
        BuildRunMemory.Load(path).Should().BeNull("битая память — как будто её нет");
    }

    [Fact]
    public void Память_КлючХешДереваИЦели_НеСклеиваются()
    {
        var dir = MemoryDir();
        var other = Path.Combine(Path.GetTempPath(), "ccs-other-tree");

        BuildRunMemory.PathFor(dir, _root, "a/b").Should().NotBe(BuildRunMemory.PathFor(dir, _root, "a_b"));
        BuildRunMemory.PathFor(dir, _root, new string('x', 200) + "1").Should()
            .NotBe(BuildRunMemory.PathFor(dir, _root, new string('x', 200) + "2"), "обрезка имени не склеивает цели");
        BuildRunMemory.PathFor(dir, _root, @".\a\b\").Should().Be(BuildRunMemory.PathFor(dir, _root, "a/b"), "одна цель в разной записи");
        BuildRunMemory.PathFor(dir, _root, null).Should().Be(BuildRunMemory.PathFor(dir, _root, "."));
        BuildRunMemory.PathFor(dir, other, "a/b").Should().NotBe(BuildRunMemory.PathFor(dir, _root, "a/b"),
            "у разных деревьев одна и та же цель — разная память");
        BuildRunMemory.PathFor(dir, _root + Path.DirectorySeparatorChar, "a/b").Should()
            .Be(BuildRunMemory.PathFor(dir, _root, "a/b"), "дерево в разной записи — одно");
    }

    [Fact]
    public void Память_БольшеПотолка_НеЧитается()
    {
        var path = BuildRunMemory.PathFor(MemoryDir(), _root, "big");
        new BuildRunMemory(1, 1, new Dictionary<string, double> { ["A"] = 1 }).Save(path);
        File.AppendAllText(path, new string(' ', BuildRunMemory.MaxBytes));

        BuildRunMemory.Load(path).Should().BeNull("гигант не читается целиком — потолок до чтения");
    }

    [Fact]
    public void Память_КороткаяNoopНеЗатираетХолодную()
    {
        var cold = new BuildRunMemory(3, 100, new Dictionary<string, double>());

        new BuildRunMemory(3, 2, new Dictionary<string, double>()).Replaces(cold).Should().BeFalse("no-op за 2 с из 100");
        new BuildRunMemory(3, 40, new Dictionary<string, double>()).Replaces(cold).Should().BeTrue();
        new BuildRunMemory(4, 2, new Dictionary<string, double>()).Replaces(cold).Should().BeTrue("состав сменился — память устарела");
        new BuildRunMemory(3, 2, new Dictionary<string, double>()).Replaces(null).Should().BeTrue();
    }

    // Ссылка, которую агент мог подложить в рабочее дерево; не создать — false
    private static bool TryLink(string link, string target, bool directory) => TreeLinks.TryLink(link, target, directory);

    private string Outside()
    {
        var outside = TreeLinks.Outside("ccs-buildprogress-out-");
        _cleanup.Add(outside);
        return outside;
    }

    private readonly List<string> _cleanup = [];

    private void Project(string relative, params string[] references)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var refs = string.Concat(references.Select(r => $"<ProjectReference Include=\"{r}\" />"));
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{refs}</ItemGroup></Project>");
    }

    [Fact]
    public void Оценка_ЗамыканиеПроектаИРешения()
    {
        Project("Core/Core.csproj");
        Project("Data/Data.csproj", @"..\Core\Core.csproj");
        Project("App/App.csproj", @"..\Data\Data.csproj", @"..\Core\Core.csproj", @"..\Missing\Missing.csproj");
        Project("Tools/Tools.csproj", @"..\Core\Core.csproj");
        File.WriteAllText(Path.Combine(_root, "All.slnx"),
            "<Solution><Project Path=\"App/App.csproj\" /><Project Path=\"Tools/Tools.csproj\" /></Solution>");

        DotnetBuildProgress.EstimateTotal(_root, "App/App.csproj").Should().Be(3, "сам, Data, Core; несуществующая ссылка не в счёт");
        DotnetBuildProgress.EstimateTotal(_root, "App").Should().Be(3, "каталог с одним проектом — как у dotnet");
        DotnetBuildProgress.EstimateTotal(_root, "All.slnx").Should().Be(4, "объединение замыканий без повторов");
        DotnetBuildProgress.EstimateTotal(_root, null).Should().Be(4, "корень с одним решением");
        DotnetBuildProgress.EstimateTotal(_root, "../outside.csproj").Should().BeNull("за пределы дерева не ходим");
        DotnetBuildProgress.EstimateTotal(_root, "nope.csproj").Should().BeNull();

        // Классический .sln — тоже через чтение одним дескриптором
        File.WriteAllText(Path.Combine(_root, "Old.sln"),
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{1}\"\nEndProject\n");
        DotnetBuildProgress.EstimateTotal(_root, "Old.sln").Should().Be(3);
    }

    // Проект ВНЕ дерева с двумя своими ссылками: попади он в обход — оценка вырастет на 3
    private string OutsideProject()
    {
        var outside = Outside();
        foreach (var name in new[] { "Out1", "Out2" })
            File.WriteAllText(Path.Combine(outside, name + ".csproj"), "<Project />");
        var path = Path.Combine(outside, "Out.csproj");
        File.WriteAllText(path,
            "<Project><ItemGroup><ProjectReference Include=\"Out1.csproj\" /><ProjectReference Include=\"Out2.csproj\" /></ItemGroup></Project>");
        return path;
    }

    [Fact]
    public void Оценка_ССылкиЗаДерево_НеОбходятся()
    {
        var outside = OutsideProject();
        Project("Rel/Rel.csproj", Path.GetRelativePath(Path.Combine(_root, "Rel"), outside).Replace('/', '\\'));
        Project("Abs/Abs.csproj", outside);

        DotnetBuildProgress.EstimateTotal(_root, "Rel/Rel.csproj").Should().Be(1, "`..` за дерево не ходим");
        DotnetBuildProgress.EstimateTotal(_root, "Abs/Abs.csproj").Should().Be(1, "абсолютный путь вне дерева не ходим");
    }

    [Fact]
    public void Оценка_ПутиРешенияЗаДерево_НеОбходятся()
    {
        var outside = OutsideProject();
        Project("Core/Core.csproj");
        File.WriteAllText(Path.Combine(_root, "All.slnx"),
            $"<Solution><Project Path=\"Core/Core.csproj\" /><Project Path=\"{outside}\" /></Solution>");

        DotnetBuildProgress.EstimateTotal(_root, "All.slnx").Should().Be(1);
    }

    [Fact]
    public void Оценка_UNC_ОтсекаетсяДоДиска()
    {
        var from = Path.Combine(_root, "App", "App.csproj");

        DotnetBuildProgress.Resolve(_root, from, @"\\evil\share\x.csproj").Should().BeNull("File.Exists на UNC открыл бы SMB");
        DotnetBuildProgress.Resolve(_root, from, "//evil/share/x.csproj").Should().BeNull();
        DotnetBuildProgress.Resolve(_root, from, @"..\Core\Core.csproj").Should().Be(Path.Combine(_root, "Core", "Core.csproj"));
    }

    [Fact]
    public void Оценка_СимлинкНаружу_НеОбходится()
    {
        var outside = OutsideProject();
        Directory.CreateDirectory(Path.Combine(_root, "App"));
        if (!TryLink(Path.Combine(_root, "App", "Link.csproj"), outside, directory: false)) return; // CI на Linux
        if (!TryLink(Path.Combine(_root, "Dir"), Path.GetDirectoryName(outside)!, directory: true)) return;
        Project("App/App.csproj", "Link.csproj", @"..\Dir\Out.csproj");

        DotnetBuildProgress.EstimateTotal(_root, "App/App.csproj").Should().Be(1, "ни файл-ссылка, ни каталог-ссылка наружу");
        DotnetBuildProgress.EstimateTotal(_root, "App/Link.csproj").Should().BeNull("и цель-ссылка наружу");
    }

    [Fact]
    public void Оценка_DTD_ОтказРазбора()
    {
        const string dtd = "<?xml version=\"1.0\"?><!DOCTYPE Project [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;\">]>"
            + "<Project><PropertyGroup><X>&b;</X></PropertyGroup></Project>";
        File.WriteAllText(Path.Combine(_root, "Dtd.csproj"), dtd);
        Project("App/App.csproj", @"..\Dtd.csproj");

        DotnetBuildProgress.EstimateTotal(_root, "Dtd.csproj").Should().BeNull("DTD запрещён — оценки нет, сборка идёт без неё");
        DotnetBuildProgress.EstimateTotal(_root, "App/App.csproj").Should().BeNull("и в проекте из ссылок тоже");
    }

    [Fact]
    public void Оценка_ПустойИГигантскийФайл_НеЧитаются()
    {
        File.WriteAllText(Path.Combine(_root, "Empty.csproj"), "");
        File.WriteAllText(Path.Combine(_root, "Huge.csproj"),
            "<Project>" + new string(' ', DotnetBuildProgress.MaxProjectFileBytes) + "</Project>");

        DotnetBuildProgress.EstimateTotal(_root, "Empty.csproj").Should().BeNull("размер 0 — как у FIFO и устройства");
        DotnetBuildProgress.EstimateTotal(_root, "Huge.csproj").Should().BeNull();
    }

    [Fact]
    public void Оценка_ОтменаТокеном()
    {
        Project("App/App.csproj");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => DotnetBuildProgress.EstimateTotal(_root, "App/App.csproj", cts.Token);
        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void ParseProjectLine_КириллицаИПробелы()
    {
        DotnetBuildProgress.ParseProjectLine(@"  Мой Проект -> C:\src\Мой Проект\bin\Debug\net10.0\Мой Проект.dll")
            .Should().Be("Мой Проект");
        DotnetBuildProgress.ParseProjectLine(@"    C:\src\x.cs(1,1): error CS0103: a -> b [C:\src\A.csproj]")
            .Should().BeNull("строка ошибки из сводки — не проект");
        DotnetBuildProgress.ParseProjectLine("  A" + new string(' ', 10_000) + "B -> x").Should().BeNull("длиннее потолка");
    }

    [Theory]
    [InlineData(1, "проект")]
    [InlineData(3, "проекта")]
    [InlineData(11, "проектов")]
    [InlineData(21, "проект")]
    [InlineData(40, "проектов")]
    public void Projects_Склонение(int n, string word) => DotnetBuildProgress.Projects(n).Should().Be(word);

    [Theory]
    [InlineData(3, "проектов")]
    [InlineData(11, "проектов")]
    [InlineData(21, "проекта")]
    [InlineData(40, "проектов")]
    public void ProjectsOf_ПослеИз_Родительный(int n, string word) => DotnetBuildProgress.ProjectsOf(n).Should().Be(word);
}
