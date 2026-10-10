using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Движок `npm run &lt;скрипт&gt;` на общем конвейере фаз: этапы по `&amp;&amp;` из package.json и
/// маркеры npm/vite на записанном образце нашего `npm run build` (frontend, vite 8), запуск
/// `node -e &lt;бутстрап&gt;` без npm.cmd, белый список имени скрипта, отказы при выходе за дерево
/// (target, package.json и node_modules ссылкой наружу), гашение по «Стоп». Процесс подменяет
/// лаунчер-заглушка из DotnetBuildServiceTests: печатает фикстуру или спит.
/// </summary>
public class NpmBuildServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    // Строка `build` нашего frontend/package.json
    private const string FrontendBuild = "tsc -b && vite build && npm run build:notes && npm run build:spend "
        + "&& npm run build:image-editor && npm run build:architecture";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-npmbuild-" + Guid.NewGuid().ToString("N"));
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ccs-npmbuild-data-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];
    private readonly List<string> _cleanup = [];

    public NpmBuildServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        lock (_processes)
            foreach (var p in _processes)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                p.Dispose();
            }
        foreach (var dir in new[] { _root, _data }.Concat(_cleanup))
            try { Directory.Delete(dir, recursive: true); } catch { /* занят */ }
        GC.SuppressFinalize(this);
    }

    private string MemoryDir => Path.Combine(_data, BuildRunMemory.DirName);

    private Project NewProject() => new() { Id = "p1", OwnerId = "u1", Name = "Фронт", RootPath = _root };

    private NpmBuildRequest Request(string? target = "frontend", string script = "build") =>
        new(NewProject(), _root, "sess-1", target, script);

    private NpmBuildService Service(DotnetBuildServiceTests.FakeLauncher launcher, BuildConcurrencyGate? gate = null) =>
        new(launcher, new TestRunsOptions { MemoryDirectory = MemoryDir },
            new PhasePipeline(gate ?? new BuildConcurrencyGate(1), TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(5));

    private DotnetBuildServiceTests.FakeLauncher Launcher(string? output, int exit = 0, bool sleep = false) =>
        new(_processes, output, exit, sleep);

    // Каталог фронта с package.json и node_modules, как в нашем репозитории
    private string SeedFrontend(string dir = "frontend", bool nodeModules = true)
    {
        var full = Path.Combine(_root, dir);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "package.json"), $$"""
            {
              "name": "frontend",
              "scripts": {
                "build": "{{FrontendBuild}}",
                "build:notes": "cd modules/notes && npx vite build && node -e \"console.log('a && b')\"",
                "lint": "eslint ."
              }
            }
            """);
        if (nodeModules) Directory.CreateDirectory(Path.Combine(full, "node_modules"));
        return full;
    }

    private sealed class ProgressLog
    {
        private readonly ConcurrentQueue<TestRunProgress> _items = new();
        public Action<TestRunProgress> Sink => _items.Enqueue;
        public IReadOnlyList<TestRunProgress> Items => [.. _items];
    }

    // --- Разбор ---

    [Fact]
    public void Этапы_СкриптНашегоФронта_ШестьЭтаповПоAmpAmp()
    {
        NpmBuildProgress.SplitStages(FrontendBuild).Should().Equal(
            "tsc -b", "vite build", "npm run build:notes", "npm run build:spend",
            "npm run build:image-editor", "npm run build:architecture");
    }

    [Fact]
    public void Этапы_AmpAmpВКавычках_НеРежется()
    {
        NpmBuildProgress.SplitStages("cd x && node -e \"a && b\" && echo 'c && d'")
            .Should().Equal("cd x", "node -e \"a && b\"", "echo 'c && d'");
        NpmBuildProgress.SplitStages("  ").Should().BeEmpty();
    }

    [Fact]
    public void Образец_НашNpmRunBuild_ЭтапыИдутПоМаркерам()
    {
        var progress = new NpmBuildProgress(NpmBuildProgress.SplitStages(FrontendBuild), previous: null);
        progress.Label().Should().Be("tsc -b · этап 1 из 6");

        var labels = new List<string>();
        var percents = new List<int>();
        var second = 0;
        foreach (var raw in File.ReadLines(TestRunServiceTests.Fixture("npm-build-frontend.txt")))
        {
            if (progress.Feed(NpmBuildProgress.StripAnsi(raw), TimeSpan.FromSeconds(++second))) labels.Add(progress.Label());
            percents.Add(progress.Percent(TimeSpan.FromSeconds(second)));
        }

        labels.Should().Contain([
            "vite build · этап 2 из 6",
            "vite build · этап 2 из 6 · трансформация модулей",
            "vite build · этап 2 из 6 · сборка чанков",
            "vite build · этап 2 из 6 · подсчёт gzip",
            "vite build · этап 2 из 6 · собрано",
            "npm run build:notes · этап 3 из 6",
            "npm run build:spend · этап 4 из 6",
            "npm run build:image-editor · этап 5 из 6",
            "npm run build:architecture · этап 6 из 6 · собрано",
        ]);
        labels.Should().NotContain(l => l.Contains("этап 7"), "вложенный vite модуля не двигает этап сверх цепочки");
        progress.Current.Should().Be(5);
        percents.Should().BeInAscendingOrder().And.OnlyContain(p => p <= 99);
        percents[^1].Should().Be(83, "без памяти — равные доли: пять из шести этапов позади");
    }

    [Fact]
    public void Ansi_РежетсяИзСтрокиRolldown()
    {
        NpmBuildProgress.StripAnsi("\u001b[2K\u001b[1Gtransforming...\u001b[32m✓\u001b[39m 5830 modules")
            .Should().Be("transforming...✓ 5830 modules");
    }

    [Fact]
    public void Процент_ПоПамяти_ПолзётДоКонцаТекущегоЭтапаИНеДальше()
    {
        var stages = NpmBuildProgress.SplitStages(FrontendBuild);
        // Прошлый раз: tsc 50 с, vite к 100 с, модули по 20 с — всего 180 с
        var memory = new BuildRunMemory(6, 180, new Dictionary<string, double>
        {
            ["1"] = 50, ["2"] = 100, ["3"] = 120, ["4"] = 140, ["5"] = 160, ["6"] = 180,
        });
        var progress = new NpmBuildProgress(stages, memory);

        progress.FromMemory.Should().BeTrue();
        progress.Percent(TimeSpan.FromSeconds(18)).Should().Be(10, "18 из 180 с прошлого времени");
        progress.Percent(TimeSpan.FromSeconds(120)).Should().Be(27, "tsc ещё идёт — полоса стоит на конце его прошлой доли");
        progress.Feed("vite v8.0.16 building client environment for production...", TimeSpan.FromSeconds(60));
        progress.Percent(TimeSpan.FromSeconds(60)).Should().Be(33);

        new NpmBuildProgress(stages, memory with { Total = 5 }).FromMemory
            .Should().BeFalse("память другой цепочки — не про эти веса");
    }

    // Этап из 40 флагов без имени скрипта: прежний шаблон `(?:\s+-{1,2}\S+)*` разбирал `--a`
    // двумя способами — 2^40 шагов бэктрекинга (ReDoS, ревью этапа 3)
    private static readonly string FlagsStage = "npm run" + string.Concat(Enumerable.Repeat(" --a", 40));

    [Fact]
    public void Разбор_ЭтапыАтакующейФормы_ЛинейныйИБыстрый()
    {
        var vites = string.Concat(Enumerable.Repeat("vite ", 8000));
        var watch = Stopwatch.StartNew();

        NpmBuildProgress.NestedScript(FlagsStage).Should().BeNull("имени скрипта нет — одни флаги");
        NpmBuildProgress.IsViteBuild(vites).Should().BeFalse("этап длиннее MaxStageLength с маркерами не сверяется");
        var progress = new NpmBuildProgress(["tsc -b", FlagsStage, vites, "vite build"], previous: null);
        progress.Feed("> x@1.0.0 y", TimeSpan.FromSeconds(1)).Should().BeFalse();
        progress.Feed("> " + new string('@', 8000) + " y", TimeSpan.FromSeconds(1)).Should().BeFalse();
        progress.Feed("vite v8.0.16 building client environment for production...", TimeSpan.FromSeconds(2))
            .Should().BeTrue();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
        progress.Label().Should().Be("vite build · этап 4 из 4");
    }

    [Theory]
    [InlineData("npm run --silent build:notes", "build:notes")]
    [InlineData("npm run -s build", "build")]
    [InlineData("pnpm --filter app", "app")]
    [InlineData("yarn run build", "build")]
    [InlineData("npm run -- --a", null)]
    public void ВложенныйСкрипт_ФлагиДоИмени(string stage, string? name) =>
        NpmBuildProgress.NestedScript(stage).Should().Be(name);

    // --- Движок ---

    [Fact]
    public async Task Стоп_ЭтапАтакующейФормы_RunAsyncВозвращается()
    {
        var frontend = SeedFrontend();
        File.WriteAllText(Path.Combine(frontend, "package.json"),
            $$$"""{"scripts":{"build":"tsc -b && {{{FlagsStage}}} && vite build"}}""");
        Directory.CreateDirectory(_data);
        // Эхо npm гоняет имя по этапам, потом старт vite; дальше процесс спит до «Стоп»
        var output = Path.Combine(_data, "heavy.txt");
        File.WriteAllLines(output, ["> x@1.0.0 y", "vite v8.0.16 building client environment for production..."]);
        var launcher = Launcher(output, sleep: true);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var running = Service(launcher).RunAsync(Request(), p =>
        {
            if (p.Label == "vite build · этап 3 из 3") reached.TrySetResult();
        }, cts.Token);
        await reached.Task.WaitAsync(Wait);
        await cts.CancelAsync();
        var result = await running.WaitAsync(Wait);

        result.Cancelled.Should().BeTrue();
        result.ProgressLabel.Should().Be("vite build · этап 3 из 3");
    }

    [Fact]
    public void DI_КонвейерОбщийСТестамиИDotnetСборкой()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILauncherFactory>(Launcher(null));
        new TestRunsSubsystem().Register(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NpmBuildService>().Pipeline
            .Should().BeSameAs(provider.GetRequiredService<TestRunService>().Pipeline)
            .And.BeSameAs(provider.GetRequiredService<DotnetBuildService>().Pipeline);
    }

    [Fact]
    public async Task Образец_СборкаИдётЧерезNodeБезCmdИПомнитВеса()
    {
        var frontend = SeedFrontend();
        var gate = new BuildConcurrencyGate(1);
        var launcher = Launcher("npm-build-frontend.txt");
        var log = new ProgressLog();

        var result = await Service(launcher, gate).RunAsync(Request(), log.Sink, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().BeNull();
        result.ExitCode.Should().Be(0);
        result.Done.Should().Be(6);
        result.ProgressLabel.Should().Be("6 из 6 этапов");
        result.BuildErrors.Should().BeEmpty("ошибка DTS Module Federation сборку не валит и в ошибки не идёт");
        log.Items.Should().NotBeEmpty().And.AllSatisfy(p =>
        {
            p.Stage.Should().Be("build");
            p.Exact.Should().BeFalse();
        });
        log.Items[0].Label.Should().Be("tsc -b · этап 1 из 6", "число этапов известно до старта");

        var spec = launcher.Specs.Should().ContainSingle().Subject;
        spec.FileName.Should().Be("node", "не npm.cmd: тот на Windows идёт через cmd.exe /c");
        spec.Args.Should().Equal("-e", NpmBuildService.NpmBootstrap, "--", "run", "build");
        spec.WorkingDirectory.Should().BeEquivalentTo(TestRunService.UpperDriveLetter(frontend));
        spec.Heavy.Should().BeTrue();
        spec.Track.Should().BeTrue();
        spec.TurnId!.Length.Should().Be(12);
        gate.Available.Should().Be(1);

        var console = Path.Combine(_root, result.ArtifactsPath!, PhasePipeline.LogName);
        File.ReadAllText(console).Should().Contain("=== npm run build").And.NotContain("\u001b", "ANSI в логе не нужен")
            .And.NotContain("require(c)", "бутстрап — константа, в лог не пишется");
        var memory = BuildRunMemory.PathFor(MemoryDir, _root, "frontend", "npm:build");
        BuildRunMemory.Load(memory).Should().NotBeNull().And.Match<BuildRunMemory>(m => m.Total == 6);
        Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
            .Should().ContainSingle("в дерево пишется только лог, память — в каталоге данных сервера")
            .Which.Should().EndWith("package.json");
    }

    [Fact]
    public async Task ОшибкаTsc_ПервыеОшибкиБезПовторовПамятьНеПишется()
    {
        SeedFrontend();
        var service = Service(Launcher("npm-build-tsc-error.txt", exit: 2));

        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(2);
        result.ProgressLabel.Should().Be("tsc -b · этап 1 из 6");
        result.BuildErrors.Should().HaveCount(2).And.AllSatisfy(e => e.Should().Contain("error TS"));
        service.FormatResult(result).Should().Contain("Сборка упала (код 2").And.Contain("error TS2339");
        Directory.Exists(MemoryDir).Should().BeFalse("упавшая сборка — не эталон весов");
    }

    [Theory]
    [InlineData("build&calc")]
    [InlineData("build & calc")]
    [InlineData("build x")]
    [InlineData("build;rm")]
    [InlineData("build|more")]
    [InlineData("\"build\"")]
    [InlineData("'build'")]
    [InlineData("-build")]
    [InlineData("--help")]
    [InlineData(":build")]
    [InlineData("build\nx")]
    [InlineData("сборка")]
    [InlineData("")]
    public async Task ИмяСкрипта_ВнеБелогоСписка_Отказ(string script)
    {
        SeedFrontend();
        var launcher = Launcher("npm-build-frontend.txt");

        var result = await Service(launcher).RunAsync(Request(script: script), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("Имя скрипта");
        launcher.Specs.Should().BeEmpty("до процесса дело не дошло");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("build:notes")]
    [InlineData("build.prod")]
    [InlineData("build_ci-2")]
    public void ИмяСкрипта_ДопустимоеИмя(string script) => NpmBuildService.IsScriptName(script).Should().BeTrue();

    [Fact]
    public async Task СкриптаНет_ОтказСоСпискомИмеющихся()
    {
        SeedFrontend();
        var result = await Service(Launcher(null)).RunAsync(Request(script: "deploy"), null, CancellationToken.None)
            .WaitAsync(Wait);

        result.Refusal.Should().Contain("нет скрипта «deploy»").And.Contain("build, build:notes, lint");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside")]
    [InlineData("frontend/../..")]
    [InlineData("frontend/../../x")]
    public async Task Цель_ЗаДеревом_Отказ(string target)
    {
        SeedFrontend();
        var launcher = Launcher("npm-build-frontend.txt");

        var result = await Service(launcher).RunAsync(Request(target), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("за пределы проекта");
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task Цель_АбсолютныйПуть_НеУходитИзДерева()
    {
        // SafePath отрезает ведущий разделитель: «/etc» — это каталог etc в дереве, а его нет
        var result = await Service(Launcher(null)).RunAsync(Request(OperatingSystem.IsWindows() ? "\\Windows" : "/etc"),
            null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().NotBeNull();
        result.Refusal.Should().Contain("нет");
    }

    [Fact]
    public async Task Цель_КаталогСсылкойНаружу_Отказ()
    {
        var outside = TreeLinks.Outside("ccs-npmbuild-out-");
        _cleanup.Add(outside);
        File.WriteAllText(Path.Combine(outside, "package.json"), """{"scripts":{"build":"tsc"}}""");
        Directory.CreateDirectory(Path.Combine(outside, "node_modules"));
        if (!TreeLinks.TryLink(Path.Combine(_root, "frontend"), outside, directory: true)) return;
        var launcher = Launcher("npm-build-frontend.txt");

        var result = await Service(launcher).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("за пределы проекта");
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task PackageJson_СсылкаНаФайлСнаружи_Отказ()
    {
        var outside = TreeLinks.Outside("ccs-npmbuild-out-");
        _cleanup.Add(outside);
        var victim = Path.Combine(outside, "package.json");
        File.WriteAllText(victim, """{"scripts":{"build":"tsc"}}""");
        var frontend = Path.Combine(_root, "frontend");
        Directory.CreateDirectory(Path.Combine(frontend, "node_modules"));
        if (!TreeLinks.TryLink(Path.Combine(frontend, "package.json"), victim, directory: false)) return; // CI на Linux
        var launcher = Launcher("npm-build-frontend.txt");

        var result = await Service(launcher).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("нет package.json");
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task PackageJson_БольшеПотолка_Отказ()
    {
        var frontend = SeedFrontend();
        File.WriteAllText(Path.Combine(frontend, "package.json"),
            """{"scripts":{"build":"tsc"},"x":" """ + new string('a', NpmBuildService.MaxPackageJsonBytes) + "\"}");

        var result = await Service(Launcher(null)).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("нет package.json");
    }

    [Fact]
    public async Task NodeModules_СсылкаНаружу_Отказ()
    {
        var outside = TreeLinks.Outside("ccs-npmbuild-out-");
        _cleanup.Add(outside);
        var frontend = SeedFrontend(nodeModules: false);
        if (!TreeLinks.TryLink(Path.Combine(frontend, "node_modules"), outside, directory: true)) return;
        var launcher = Launcher("npm-build-frontend.txt");

        var result = await Service(launcher).RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("node_modules ведёт символической ссылкой за пределы проекта");
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task NodeModules_Монорепа_ИщетсяВверхДоКорняНоНеВыше()
    {
        // Пакеты подняты в корень дерева — как у npm/pnpm workspaces
        SeedFrontend("packages/app", nodeModules: false);
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        var launcher = Launcher("npm-build-frontend.txt");

        var result = await Service(launcher).RunAsync(Request("packages/app"), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(0);
        launcher.Specs.Should().ContainSingle().Which.WorkingDirectory
            .Should().EndWith(Path.Combine("packages", "app"), "npm запускается в каталоге цели");

        // Выше корня дерева node_modules не ищется: дерево — подкаталог, зависимости — у родителя
        var nested = Path.Combine(_root, "tree");
        Directory.CreateDirectory(nested);
        var inner = new NpmBuildRequest(NewProject(), nested, "sess-1", null);
        File.WriteAllText(Path.Combine(nested, "package.json"), """{"scripts":{"build":"tsc"}}""");
        var refused = await Service(Launcher(null)).RunAsync(inner, null, CancellationToken.None).WaitAsync(Wait);
        refused.Refusal.Should().Contain("нет node_modules");
    }

    [Fact]
    public async Task Стоп_ГаситДеревоПоTurnIdСборки()
    {
        SeedFrontend();
        var launcher = Launcher(null, sleep: true);
        using var cts = new CancellationTokenSource();

        var running = Service(launcher).RunAsync(Request(), null, cts.Token);
        var spec = await launcher.SleepStarted.Task.WaitAsync(Wait);
        await cts.CancelAsync();
        var result = await running.WaitAsync(Wait);

        (await launcher.Killed.Task.WaitAsync(Wait)).Should().Be(spec.TurnId);
        result.Cancelled.Should().BeTrue();
        result.ProgressLabel.Should().Be("tsc -b · этап 1 из 6");
    }

    [Fact]
    public async Task ОбщаяБлокировка_СборкаНеИдётПоверхПрогонаВТомЖеДереве()
    {
        SeedFrontend();
        var service = Service(Launcher("npm-build-frontend.txt"));

        using (service.Pipeline.TryLockTree(_root))
        {
            var refused = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);
            refused.Refusal.Should().Contain("уже идёт сборка или прогон тестов");
        }
    }
}
