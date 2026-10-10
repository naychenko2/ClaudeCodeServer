using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Движок run_tests: конвейер build → --list-tests → test, гейт тяжёлых сборок на весь
/// конвейер, свой TurnId на каждый процесс, отмена и потолок с гашением дерева через
/// launcher.Kill, блокировка «одно дерево — один прогон», прогресс и итог. Процессы подменяет
/// лаунчер-заглушка: по фазе он печатает настоящий вывод dotnet из фикстуры (или спит), в
/// фазе прогона кладёт фикстурные TRX в папку результатов, а Kill отмечает событие — тест
/// ждёт именно его, без Task.Delay.
/// </summary>
public class TestRunServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _root;
    private readonly List<Process> _processes = [];

    public TestRunServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccs-testruns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        lock (_processes)
            foreach (var p in _processes)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                p.Dispose();
            }
        try { Directory.Delete(_root, recursive: true); } catch { /* занят */ }
        GC.SuppressFinalize(this);
    }

    internal static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Services", "TestRuns", "Fixtures", name);

    // Что делает процесс фазы: печатает файл и выходит с кодом, спит или падает на старте
    private sealed record Script(string? Output = null, int Exit = 0, bool Sleep = false, bool Throw = false);

    private static Script Ok(string? output = null) => new(output);

    private static TestRunPhase PhaseOf(ProcessSpec spec) =>
        spec.Args[0] == "build" ? TestRunPhase.Build
        : spec.Args.Contains("--list-tests") || (spec.Args.Count > 1 && spec.Args[1] == "list") ? TestRunPhase.List
        : TestRunPhase.Test;

    private sealed class FakeLauncher(List<Process> processes, Func<TestRunPhase, Script> plan,
        params string[] trxToCopy) : IProcessLauncher, ILauncherFactory
    {
        // JSON-отчёт vitest/Playwright, который «процесс» кладёт туда, куда ему велели
        public string? JsonReport { get; init; }

        public TaskCompletionSource<ProcessSpec> SleepStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Killed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ProcessSpec> Specs { get; } = new();
        public int Starts => Specs.Count;

        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public IProcessLauncher Local => this;
        public IProcessLauncher ForOwner(string? ownerId) => this;
        public IProcessLauncher ForProject(Project project) => this;

        public Process Start(ProcessSpec spec)
        {
            Specs.Enqueue(spec);
            var phase = PhaseOf(spec);
            var script = plan(phase);
            if (script.Throw) throw new InvalidOperationException("dotnet не найден");
            if (phase == TestRunPhase.Test && trxToCopy.Length > 0)
            {
                // Как настоящий dotnet test: TRX в --results-directory относительно cwd
                var results = Path.Combine(spec.WorkingDirectory!, spec.Args[spec.Args.ToList().IndexOf("--results-directory") + 1]);
                Directory.CreateDirectory(results);
                foreach (var trx in trxToCopy) File.Copy(Fixture(trx), Path.Combine(results, "results_" + trx));
            }
            if (phase == TestRunPhase.Test && JsonReport is { } report)
            {
                // vitest — --outputFile.json=, Playwright — PLAYWRIGHT_JSON_OUTPUT_NAME; оба относительно cwd
                var target = spec.Args.FirstOrDefault(a => a.StartsWith("--outputFile.json=", StringComparison.Ordinal))?
                    .Split('=', 2)[1] ?? spec.Env!["PLAYWRIGHT_JSON_OUTPUT_NAME"];
                File.Copy(Fixture(report), Path.Combine(spec.WorkingDirectory!, target));
            }
            var win = OperatingSystem.IsWindows();
            var command = script.Sleep
                ? (win ? "ping -n 120 127.0.0.1 >nul" : "sleep 120")
                : script.Output is { } file
                    ? (win ? $"type {Fixture(file)} & exit {script.Exit}" : $"cat '{Fixture(file)}'; exit {script.Exit}")
                    : (win ? $"echo tail-marker-line& exit {script.Exit}" : $"echo tail-marker-line; exit {script.Exit}");
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = win ? "cmd.exe" : "/bin/sh",
                Args = win ? ["/c", command] : ["-c", command],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                RedirectStdin = false,
                Track = false,
            });
            lock (processes) processes.Add(process);
            if (script.Sleep) SleepStarted.TrySetResult(spec);
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            Killed.TrySetResult(turnId);
        }

        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    // Настоящий прогон из фикстур: сборка ок, 7 тестов в двух сборках, один упал, один пропущен
    private FakeLauncher RealRun(int testExit = 1) => new(_processes, phase => phase switch
    {
        TestRunPhase.Build => Ok(),
        TestRunPhase.List => Ok("list-tests.txt"),
        _ => new Script("console-normal.txt", testExit),
    }, "results-failed.trx", "results-passed.trx");

    private Project NewProject() => new() { Id = "p1", OwnerId = "u1", Name = "Тесты", RootPath = _root };

    private TestRunRequest Request(string? filter = null, bool noBuild = false, string? target = "backend/App.Tests") =>
        new(NewProject(), _root, "sess-1", Target: target, Filter: filter, NoBuild: noBuild);

    private static TestRunService Service(FakeLauncher launcher, BuildConcurrencyGate gate, int ceilingSeconds = 540,
        TimeSpan? progressInterval = null) =>
        new(launcher, new TestRunsOptions { CeilingSeconds = ceilingSeconds }, gate, exitGrace: TimeSpan.FromSeconds(5),
            progressInterval ?? TimeSpan.Zero);

    private sealed class ProgressLog
    {
        private readonly ConcurrentQueue<TestRunProgress> _items = new();
        public Action<TestRunProgress> Sink => _items.Enqueue;
        public IReadOnlyList<TestRunProgress> Items => [.. _items];
    }

    [Fact]
    public async Task Конвейер_СборкаПодсчётПрогон_ИтогИзTrx()
    {
        var launcher = RealRun();
        var gate = new BuildConcurrencyGate(1);
        var service = Service(launcher, gate);
        var progress = new ProgressLog();

        var result = await service.RunDotnetAsync(Request(), progress.Sink, CancellationToken.None).WaitAsync(Wait);

        launcher.Specs.Select(PhaseOf).Should().Equal(TestRunPhase.Build, TestRunPhase.List, TestRunPhase.Test);
        launcher.Specs.Select(s => s.TurnId).Should().OnlyHaveUniqueItems("свой TurnId на каждый процесс")
            .And.AllSatisfy(id => id.Should().HaveLength(12));
        launcher.Specs.Should().AllSatisfy(s => s.Heavy.Should().BeTrue());
        gate.Available.Should().Be(1, "слот освобождается по концу конвейера");

        result.Phase.Should().Be(TestRunPhase.Test);
        result.ExitCode.Should().Be(1);
        result.Total.Should().Be(7, "две сборки в --list-tests: 5 + 2");
        result.Counts.Should().Be(new TestCounts(Passed: 5, Failed: 1, Skipped: 1));
        result.FailedFromConsole.Should().Equal("A.Tests.CalcTests.Fails");
        result.Reports.Select(r => r.Assembly).Should().BeEquivalentTo("A.Tests.dll", "B.Tests.dll");

        var text = service.FormatResult(result);
        text.Should().Contain("есть упавшие тесты")
            .And.Contain("FAILED 1, passed 3, skipped 1 из 5")
            .And.Contain("OK: passed 2 из 2")
            .And.Contain("A.Tests.CalcTests.Fails")
            .And.Contain("Expected: \"ожидалось\"")
            .And.Contain(result.ArtifactsPath!);
        result.ArtifactsPath.Should().StartWith(".cc-attachments/test-runs/");

        var log = await File.ReadAllTextAsync(Path.Combine(_root, result.ArtifactsPath!, "console.log"));
        log.Should().Contain("=== dotnet build").And.Contain("--list-tests").And.Contain("Total tests: 5",
            "в console.log — полный вывод всех фаз");
    }

    [Fact]
    public async Task Артефакты_СсылкаВместоTestRuns_НаружуНичегоНеПишетсяПрогонИдёт()
    {
        var outside = TreeLinks.Outside("ccs-testruns-out-");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root, ".cc-attachments"));
            if (!TreeLinks.TryLink(Path.Combine(_root, ".cc-attachments", TestRunService.ArtifactsSubdir), outside,
                    directory: true))
                return; // проверка живёт в CI на Linux
            var launcher = new FakeLauncher(_processes, phase => phase switch
            {
                TestRunPhase.List => Ok("list-tests.txt"),
                TestRunPhase.Test => new Script("console-normal.txt", 1),
                _ => Ok(),
            });

            var result = await Service(launcher, new BuildConcurrencyGate(1))
                .RunDotnetAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

            result.Phase.Should().Be(TestRunPhase.Test, "ссылка в пути артефактов — не повод валить прогон");
            result.ExitCode.Should().Be(1);
            result.Reports.Should().BeEmpty("отчёты из папки за ссылкой хост не читает — сводка по выводу");
            result.ArtifactsPath.Should().BeNull();
            Directory.EnumerateFileSystemEntries(outside, "*", SearchOption.AllDirectories).Should()
                .BeEmpty("хост не создал за деревом ни каталога, ни console.log");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch { /* занят */ }
        }
    }

    [Fact]
    public async Task Прогресс_ФазыИСчётчик_ПроцентТочныйИНеВыше99()
    {
        var launcher = RealRun();
        var progress = new ProgressLog();

        await Service(launcher, new BuildConcurrencyGate(1)).RunDotnetAsync(Request(), progress.Sink, CancellationToken.None)
            .WaitAsync(Wait);

        var items = progress.Items;
        items.Select(p => p.Stage).Distinct().Should().Equal("build", "list", "running");
        var running = items.Where(p => p.Stage == "running").ToList();
        running.Should().AllSatisfy(p => p.Exact.Should().BeTrue("процент по настоящим тестам из --list-tests"));
        running.Select(p => p.Percent!.Value).Should().BeInAscendingOrder("процент не откатывается");
        running.Max(p => p.Percent).Should().Be(TestRunSummaryFormatter.MaxProgressPercent,
            "7 из 7 — это всё ещё 99: «готово» говорит только результат вызова");
        running.Last().Label.Should().Be("7 из 7 · упало 1");
    }

    [Fact]
    public async Task Прогресс_НеЧащеШага()
    {
        var launcher = RealRun();
        var progress = new ProgressLog();

        await Service(launcher, new BuildConcurrencyGate(1), progressInterval: TimeSpan.FromHours(1))
            .RunDotnetAsync(Request(), progress.Sink, CancellationToken.None).WaitAsync(Wait);

        progress.Items.Count(p => p.Stage == "running").Should().Be(1,
            "семь строк исходов за доли секунды — одно событие (стартовое «0 из 7»), а не семь");
    }

    [Fact]
    public async Task ОшибкаСборки_ПервыеОшибки_ТестыНеЗапускаются()
    {
        var launcher = new FakeLauncher(_processes, _ => new Script("build-error.txt", 1));
        var service = Service(launcher, new BuildConcurrencyGate(1));

        var result = await service.RunDotnetAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        launcher.Starts.Should().Be(1, "после неудачной сборки list и test не запускаются");
        result.Phase.Should().Be(TestRunPhase.Build);
        result.BuildErrors.Should().HaveCount(16, "32 строки ошибок — каждая дважды (по ходу и в сводке MSBuild)");
        var text = service.FormatResult(result);
        text.Should().StartWith("dotnet test: сборка не прошла").And.Contain("error CS0246")
            .And.NotContain("Restored", "лог MSBuild модели не нужен — только ошибки")
            .And.NotContain("*.trx", "на ошибке сборки TRX не бывает — и в итоге его нет");
    }

    [Fact]
    public async Task NoBuild_БезФазыСборки()
    {
        var launcher = RealRun();

        await Service(launcher, new BuildConcurrencyGate(1)).RunDotnetAsync(Request(noBuild: true), null,
            CancellationToken.None).WaitAsync(Wait);

        launcher.Specs.Select(PhaseOf).Should().Equal(TestRunPhase.List, TestRunPhase.Test);
    }

    [Fact]
    public async Task ПодсчётНеУдался_СчётчикБезПроцента()
    {
        var launcher = new FakeLauncher(_processes, phase => phase switch
        {
            TestRunPhase.List => new Script(Exit: 1),
            TestRunPhase.Test => new Script("console-normal.txt", 1),
            _ => Ok(),
        });
        var progress = new ProgressLog();

        var result = await Service(launcher, new BuildConcurrencyGate(1)).RunDotnetAsync(Request(), progress.Sink,
            CancellationToken.None).WaitAsync(Wait);

        result.Total.Should().BeNull();
        var running = progress.Items.Where(p => p.Stage == "running").ToList();
        running.Should().AllSatisfy(p =>
        {
            p.Percent.Should().BeNull("без общего числа процент был бы выдумкой");
            p.Exact.Should().BeNull();
        });
        running.Last().Label.Should().Be("7 · упало 1");
    }

    [Fact]
    public async Task Отмена_ГаситДеревоЧерезKill_СлотИБлокировкаСвободны()
    {
        var launcher = new FakeLauncher(_processes, phase => phase == TestRunPhase.Test ? new Script(Sleep: true) : Ok());
        var gate = new BuildConcurrencyGate(1);
        var service = Service(launcher, gate);
        using var cts = new CancellationTokenSource();

        var run = service.RunDotnetAsync(Request(), null, cts.Token);
        var spec = await launcher.SleepStarted.Task.WaitAsync(Wait);
        gate.Available.Should().Be(0, "прогон держит слот общего потолка сборок");

        cts.Cancel();
        var killedTurn = await launcher.Killed.Task.WaitAsync(Wait);
        var result = await run.WaitAsync(Wait);

        killedTurn.Should().Be(spec.TurnId, "в песочнице процесс добивается по своей метке");
        result.Cancelled.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.NeverStarted.Should().BeFalse();
        result.Phase.Should().Be(TestRunPhase.Test);
        gate.Available.Should().Be(1, "слот освобождается и при отмене");
        service.FormatResult(result).Should().Contain("остановлен").And.Contain("погашены");
    }

    [Fact]
    public async Task Потолок_ГаситПроцесс_ИтогСоветуетФильтр()
    {
        var launcher = new FakeLauncher(_processes, phase => phase == TestRunPhase.Test ? new Script(Sleep: true) : Ok());
        var service = Service(launcher, new BuildConcurrencyGate(1), ceilingSeconds: 1);

        var result = await service.RunDotnetAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        launcher.Killed.Task.IsCompleted.Should().BeTrue("на потолке процесс гасится, а не бросается");
        result.TimedOut.Should().BeTrue();
        result.Cancelled.Should().BeFalse();
        service.FormatResult(result).Should().Contain("filter").And.Contain("потолком");
    }

    [Fact]
    public async Task СбойStart_СлотИБлокировкаОсвобождены()
    {
        var launcher = new FakeLauncher(_processes, _ => new Script(Throw: true));
        var gate = new BuildConcurrencyGate(1);
        var service = Service(launcher, gate);

        var first = () => service.RunDotnetAsync(Request(), null, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();

        gate.Available.Should().Be(1, "слот не утекает при сбое запуска");
        await first.Should().ThrowAsync<InvalidOperationException>("дерево не осталось занятым — второй прогон снова дошёл до Start");
        launcher.Starts.Should().Be(2);
    }

    [Fact]
    public async Task ОдноДерево_ВторойПрогонПолучаетОтказ()
    {
        var launcher = new FakeLauncher(_processes, _ => new Script(Sleep: true));
        var service = Service(launcher, new BuildConcurrencyGate(2));
        using var cts = new CancellationTokenSource();

        var run = service.RunDotnetAsync(Request(), null, cts.Token);
        await launcher.SleepStarted.Task.WaitAsync(Wait);

        var second = await service.RunDotnetAsync(Request(), null, CancellationToken.None);

        second.Refusal.Should().Contain("уже идёт прогон");
        launcher.Starts.Should().Be(1, "второй процесс не стартует вовсе");
        cts.Cancel();
        await run.WaitAsync(Wait);
    }

    // L2 ревью этапа 1: оборвано в очереди — процесс не запускался, итог не врёт про гашение
    [Fact]
    public async Task ГейтЗанят_ОтменаВОчереди_ИтогНеГоворитПроГашение()
    {
        var launcher = RealRun();
        var gate = new BuildConcurrencyGate(1);
        using var busy = gate.TryAcquire(new ProcessSpec { FileName = "dotnet", Heavy = true });
        var service = Service(launcher, gate);
        var queued = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var run = service.RunDotnetAsync(Request(),
            p => { if (p.Stage == "queued") queued.TrySetResult(p.Label); }, cts.Token);
        var label = await queued.Task.WaitAsync(Wait);
        cts.Cancel();
        var result = await run.WaitAsync(Wait);

        label.Should().Contain("очереди сборок");
        result.Cancelled.Should().BeTrue();
        result.NeverStarted.Should().BeTrue();
        launcher.Starts.Should().Be(0, "без слота процесс не запускается");
        service.FormatResult(result).Should().Contain("dotnet не запускался").And.NotContain("погаш");
    }

    [Fact]
    public async Task ГейтЗанят_ПотолокВОчереди_НеДождалсяОчереди()
    {
        var launcher = RealRun();
        var gate = new BuildConcurrencyGate(1);
        using var busy = gate.TryAcquire(new ProcessSpec { FileName = "dotnet", Heavy = true });
        var service = Service(launcher, gate, ceilingSeconds: 1);

        var result = await service.RunDotnetAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.TimedOut.Should().BeTrue();
        result.NeverStarted.Should().BeTrue();
        launcher.Starts.Should().Be(0);
        service.FormatResult(result).Should().Contain("не дождался очереди").And.NotContain("погаш");
    }

    [Fact]
    public void Spec_ФазыКонвейера()
    {
        var request = Request("FullyQualifiedName~X");

        var build = TestRunService.BuildSpec(TestRunPhase.Build, request, "t1", "out");
        var list = TestRunService.BuildSpec(TestRunPhase.List, request, "t2", "out");
        var test = TestRunService.BuildSpec(TestRunPhase.Test, request, "t3", "out");

        build.Args.Should().Equal("build", "backend/App.Tests");
        list.Args.Should().Equal("test", "backend/App.Tests", "--no-build", "--list-tests",
            "--filter", "FullyQualifiedName~X");
        test.Args.Should().Equal("test", "backend/App.Tests", "--no-build", "--filter", "FullyQualifiedName~X",
            "--logger", "console;verbosity=normal", "--logger", "trx;LogFilePrefix=results",
            "--results-directory", "out");
        foreach (var spec in new[] { build, list, test })
        {
            spec.FileName.Should().Be("dotnet");
            spec.Heavy.Should().BeTrue();
            spec.PrivateBuildNodes.Should().BeTrue("узлы MSBuild прогона не переживают его и не общие с чужими");
            spec.Track.Should().BeTrue();
            spec.RedirectStdin.Should().BeFalse();
            spec.Env.Should().Contain("DOTNET_CLI_UI_LANGUAGE", "en");
            spec.SessionId.Should().Be("sess-1");
        }
        test.TurnId.Should().Be("t3");
    }

    // Бой 2026-10-02: корень проекта записан как «c:\GIT\…», и vitest с таким cwd грузит модуль
    // vitest дважды (c:/… и C:/…) — каждый файл «не загрузился». Каталог процесса — с заглавной
    // буквой диска у ЛЮБОГО вида прогона; строка без буквы диска (Linux, песочница) не трогается
    [Theory]
    [InlineData(TestRunKind.Vitest, @"c:\GIT\Repo", "frontend", @"C:\GIT\Repo")]
    [InlineData(TestRunKind.Playwright, @"d:\work", "e2e", @"D:\work")]
    [InlineData(TestRunKind.Dotnet, @"c:\GIT\Repo", "backend/App.Tests", @"C:\GIT\Repo")]
    [InlineData(TestRunKind.Vitest, "/home/u/repo", "frontend", "/home/u/repo")]
    public void Spec_КаталогПроцесса_ЗаглавнаяБукваДиска(TestRunKind kind, string root, string target, string expectedRoot)
    {
        var request = new TestRunRequest(NewProject(), root, "sess-1", Target: target) { Kind = kind };

        var spec = TestRunService.BuildSpec(TestRunPhase.Test, request, "t1", "out");

        spec.WorkingDirectory.Should().StartWith(expectedRoot);
        spec.WorkingDirectory.Should().NotStartWith(root[..1] + ":", "нижний регистр буквы диска ломает vitest");
    }

    [Fact]
    public async Task ДлинныйФильтр_ОтказДоЗапуска()
    {
        var launcher = RealRun();
        var service = Service(launcher, new BuildConcurrencyGate(1));

        var result = await service.RunDotnetAsync(Request(new string('x', TestRunService.MaxFilterLength + 1)),
            null, CancellationToken.None);

        result.Refusal.Should().NotBeNull();
        launcher.Starts.Should().Be(0);
    }

    [Fact]
    public async Task ЦельОпция_ОтказДвижкаДоЗапуска()
    {
        // Второй рубеж к проверке тулсета (M1): движок сам не пускает цель-опцию в dotnet
        var launcher = RealRun();
        var service = Service(launcher, new BuildConcurrencyGate(1));

        var result = await service.RunDotnetAsync(Request(target: "--diag:/tmp/x.log"), null, CancellationToken.None);

        result.Refusal.Should().Contain("«-»");
        launcher.Starts.Should().Be(0);
    }

    // M1-bis ревью этапа 2: «@файл» dotnet раскрывает в опции из response-файла — и в цели, и в
    // фильтре. Второй рубеж: движок не пускает их в процесс, даже если тулсет пропустил
    [Theory]
    [InlineData("@args", null)]
    [InlineData("  @args", null)]
    [InlineData(null, "@args2")]
    [InlineData(null, " @args2")]
    [InlineData(null, "-e:DOTNET_STARTUP_HOOKS=x")]
    public async Task ResponseФайлИлиОпция_ОтказДвижкаДоЗапуска(string? target, string? filter)
    {
        var launcher = RealRun();
        var service = Service(launcher, new BuildConcurrencyGate(1));

        var result = await service.RunDotnetAsync(Request(filter, target: target), null, CancellationToken.None);

        result.Refusal.Should().Contain("«@»");
        launcher.Starts.Should().Be(0);
    }

    [Fact]
    public async Task ВсеАргументыNode_ОпцияИлиResponseФайл_ОтказДоЗапуска()
    {
        var launcher = RealRun();
        var service = Service(launcher, new BuildConcurrencyGate(1));

        foreach (var request in new[]
                 {
                     VitestRequest(files: ["--config=/tmp/evil.ts"]),
                     VitestRequest(files: ["@args"]),
                     VitestRequest() with { Filter = "--root=/" },
                     PlaywrightRequest() with { Filter = "@x" },
                     PlaywrightRequest() with { Target = "-c" },
                     PlaywrightRequest() with { Env = new Dictionary<string, string> { ["NODE_OPTIONS"] = "--require x" } },
                 })
        {
            var result = await service.RunAsync(request, null, CancellationToken.None);
            result.Refusal.Should().NotBeNull(request.ToString());
        }
        launcher.Starts.Should().Be(0);
    }

    // --- vitest ---

    private TestRunRequest VitestRequest(IReadOnlyList<string>? files = null) =>
        new(NewProject(), _root, "sess-1", Target: "frontend") { Kind = TestRunKind.Vitest, Files = files ?? [] };

    private FakeLauncher VitestRun(string list = "vitest-list.txt") => new(_processes, phase => phase switch
    {
        TestRunPhase.List => Ok(list),
        _ => new Script("vitest-default.txt", 1),
    })
    { JsonReport = "vitest-report.json" };

    [Fact]
    public async Task Vitest_ПодсчётФайлов_ПрогонПоФайлам_ИтогИзJson()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        var launcher = VitestRun();
        var gate = new BuildConcurrencyGate(1);
        var service = Service(launcher, gate);
        var progress = new ProgressLog();

        var result = await service.RunAsync(VitestRequest(["src/lib"]) with { Filter = "проба" }, progress.Sink,
            CancellationToken.None).WaitAsync(Wait);

        var specs = launcher.Specs.ToList();
        specs.Select(PhaseOf).Should().Equal(TestRunPhase.List, TestRunPhase.Test);
        specs.Should().AllSatisfy(s =>
        {
            s.FileName.Should().Be("node", "не npx: на Windows это cmd.exe /c, и «&» в аргументе стал бы командой");
            s.Args[0].Should().Be(TestRunService.VitestBin);
            s.WorkingDirectory.Should().Be(Path.Combine(_root, "frontend"));
            s.Track.Should().BeTrue();
            s.TurnId.Should().HaveLength(12);
            s.Env.Should().Contain("CI", "1");
        });
        specs[0].Args.Should().Equal(TestRunService.VitestBin, "list", "--filesOnly", "src/lib");
        specs[0].Heavy.Should().BeFalse("подсчёт файлов лёгкий — в гейт сборок не встаёт");
        specs[1].Args.Take(5).Should().Equal(TestRunService.VitestBin, "run", "src/lib", "-t", "проба");
        specs[1].Args.Should().Contain(a => a.StartsWith("--outputFile.json=../.cc-attachments/test-runs/"),
            "папка отчёта — относительно каталога процесса");
        specs[1].Heavy.Should().BeTrue($"12 файлов ≥ {TestRunService.VitestHeavyFiles} — воркеров много, это тяжёлый прогон");

        result.Kind.Should().Be(TestRunKind.Vitest);
        result.Total.Should().Be(12, "12 путей в выводе list --filesOnly");
        var running = progress.Items.Where(p => p.Stage == "running").ToList();
        running.Last().Label.Should().Be("2 из 12 файлов · упало 2",
            "два файла: один не загрузился (одно падение), во втором упал один тест");
        running.Last().Exact.Should().BeTrue();
        result.Reports.Should().ContainSingle().Which.Failed.Should().Be(2);

        var text = service.FormatResult(result);
        text.Should().StartWith("vitest: есть упавшие тесты")
            .And.Contain("src/__vtprobe__/fail.test.ts > проба падает")
            .And.Contain("Cannot find module")
            .And.Contain("report.json — отчёт vitest")
            .And.NotContain("*.trx");
    }

    [Fact]
    public async Task Vitest_МалоФайлов_ВнеГейтаСборок()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        // В подсчёте два файла: воркеров два, гейт сборок не нужен — даже занятый он не держит
        var launcher = VitestRun(list: "vitest-list-two.txt");
        var gate = new BuildConcurrencyGate(1);
        using var busy = gate.TryAcquire(new ProcessSpec { FileName = "dotnet", Heavy = true });

        var result = await Service(launcher, gate).RunAsync(VitestRequest(), null, CancellationToken.None)
            .WaitAsync(Wait);

        result.NeverStarted.Should().BeFalse("лёгкий прогон не ждёт очереди сборок");
        result.Total.Should().Be(2);
        launcher.Specs.Should().HaveCount(2).And.AllSatisfy(s => s.Heavy.Should().BeFalse());
    }

    // --- Playwright ---

    private TestRunRequest PlaywrightRequest(IReadOnlyDictionary<string, string>? env = null) =>
        new(NewProject(), _root, "sess-1", Target: "frontend")
        {
            Kind = TestRunKind.Playwright,
            Env = env ?? new Dictionary<string, string>(),
        };

    private TestRunService PlaywrightService(FakeLauncher launcher, bool standUp, List<Uri>? probed = null,
        int ceilingSeconds = 540) =>
        new(launcher, new TestRunsOptions { CeilingSeconds = ceilingSeconds }, new BuildConcurrencyGate(1),
            TimeSpan.FromSeconds(5), TimeSpan.Zero,
            (url, _) =>
            {
                probed?.Add(url);
                return Task.FromResult(standUp);
            });

    [Fact]
    public async Task Playwright_СтендЛежит_ОтказДоЗапуска()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        var launcher = new FakeLauncher(_processes, _ => Ok("playwright-list.txt"));
        var probed = new List<Uri>();

        var result = await PlaywrightService(launcher, standUp: false, probed).RunAsync(
            PlaywrightRequest(new Dictionary<string, string> { ["PLAYWRIGHT_BASE_URL"] = "http://localhost:5123" }),
            null, CancellationToken.None);

        probed.Should().Equal(new Uri("http://localhost:5123"));
        result.Refusal.Should().Contain("не отвечает").And.Contain("PLAYWRIGHT_BASE_URL");
        launcher.Starts.Should().Be(0, "74 таймаута по 90 с вместо быстрого отказа — то, от чего защищаемся");
    }

    [Fact]
    public async Task Playwright_АдресСтендаИзКонфига()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "frontend")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "playwright.config.ts"),
            "use: { baseURL: process.env.PLAYWRIGHT_BASE_URL || 'http://localhost:5000' }");

        TestRunService.StandUrl(PlaywrightRequest()).Should().Be(new Uri("http://localhost:5000"));
        TestRunService.StandUrl(PlaywrightRequest(new Dictionary<string, string> { ["PLAYWRIGHT_BASE_URL"] = "http://127.0.0.1:5600" }))
            .Should().Be(new Uri("http://127.0.0.1:5600"), "env переопределяет адрес из конфига");
    }

    [Fact]
    public async Task Playwright_ЧислоСПервойСтроки_ПрогонПоТестам_ИтогИзJson()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        var launcher = new FakeLauncher(_processes, _ => new Script("playwright-list.txt", 1))
        {
            JsonReport = "playwright-report.json",
        };
        var progress = new ProgressLog();
        var request = PlaywrightRequest(new Dictionary<string, string> { ["E2E_USER"] = "admin" }) with
        {
            Files = ["e2e/zz-probe.spec.ts"],
            Filter = "проба",
        };

        var result = await PlaywrightService(launcher, standUp: true).RunAsync(request, progress.Sink,
            CancellationToken.None).WaitAsync(Wait);

        var spec = launcher.Specs.Should().ContainSingle("ни сборки, ни отдельного --list: число — из «Running N tests»").Subject;
        spec.FileName.Should().Be("node");
        spec.Args.Should().Equal(TestRunService.PlaywrightBin, "test", "e2e/zz-probe.spec.ts", "--grep", "проба",
            "--reporter=list,json");
        spec.Heavy.Should().BeFalse("Playwright с одним воркером — вне гейта сборок");
        spec.Env.Should().Contain("E2E_USER", "admin").And.Contain("FORCE_COLOR", "0")
            .And.ContainKey("PLAYWRIGHT_JSON_OUTPUT_NAME");

        result.Kind.Should().Be(TestRunKind.Playwright);
        result.Total.Should().Be(4);
        result.Counts.Should().Be(new TestCounts(Passed: 2, Failed: 1, Skipped: 1));
        var running = progress.Items.Where(p => p.Stage == "running").ToList();
        running.Should().Contain(p => p.Label == "0 из 4", "число тестов известно с первой строки — сразу в карточку");
        running.Last().Label.Should().Be("4 из 4 · упало 1");
        result.FailedFromConsole.Should().ContainSingle().Which.Should().Contain("проба › падает");

        var text = new TestRunService(launcher, new TestRunsOptions()).FormatResult(result);
        text.Should().StartWith("Playwright: есть упавшие тесты")
            .And.Contain("[chromium] zz-probe.spec.ts › проба › падает (строка 4)")
            .And.Contain("toEqual")
            .And.Contain("report.json — отчёт Playwright");
    }

    // webServer в конфиге: стенд поднимает сам Playwright — заранее не проверяем (иначе отказ
    // «стенд не отвечает» на том, что ещё не поднято), на карточке «запуск стенда» до числа тестов
    [Fact]
    public async Task Playwright_WebServerВКонфиге_БезПроверкиСтенда_ЭтапЗапускаСтенда()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "frontend")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "playwright.config.ts"),
            "export default { use: { baseURL: 'http://localhost:5173' }, webServer: { command: 'npm run dev' } }");
        var launcher = new FakeLauncher(_processes, _ => new Script("playwright-list.txt", 1))
        {
            JsonReport = "playwright-report.json",
        };
        var probed = new List<Uri>();
        var progress = new ProgressLog();

        var result = await PlaywrightService(launcher, standUp: false, probed).RunAsync(PlaywrightRequest(),
            progress.Sink, CancellationToken.None).WaitAsync(Wait);

        probed.Should().BeEmpty("стенд поднимет webServer — предварительная проверка не нужна");
        result.Refusal.Should().BeNull();
        launcher.Specs.Should().ContainSingle();
        progress.Items[0].Stage.Should().Be("stand");
        progress.Items[0].Label.Should().Be("запуск стенда");
        progress.Items.Skip(1).First().Label.Should().Be("0 из 4", "после «Running N tests» — сразу «0 из N»");
        progress.Items.Should().NotContain(p => p.Stage == "running" && p.Label == "0 из ?",
            "пока стенд поднимается, счётчика без общего числа не показываем");
    }

    // Без webServer — как раньше: проверка стенда по адресу из конфига, лежит — отказ
    [Fact]
    public async Task Playwright_БезWebServer_ПроверкаСтендаКакРаньше()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "frontend")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "playwright.config.ts"),
            "export default { use: { baseURL: 'http://localhost:5173' } }");
        var launcher = new FakeLauncher(_processes, _ => Ok("playwright-list.txt"));
        var probed = new List<Uri>();

        var result = await PlaywrightService(launcher, standUp: false, probed).RunAsync(PlaywrightRequest(),
            null, CancellationToken.None);

        probed.Should().Equal(new Uri("http://localhost:5173"));
        result.Refusal.Should().Contain("не отвечает");
        launcher.Starts.Should().Be(0);
    }

    // Монорепа: скрипт пакета из корня едет первым аргументом node, cwd — каталог цели
    [Fact]
    public void Playwright_NodeBinМонорепы_ВАргументахИКаталогЦели()
    {
        var request = PlaywrightRequest() with { NodeBin = "../node_modules/@playwright/test/cli.js" };

        var spec = TestRunService.BuildSpec(TestRunPhase.Test, request, "t", "r");

        spec.Args[0].Should().Be("../node_modules/@playwright/test/cli.js");
        spec.WorkingDirectory.Should().Be(TestRunService.ProcessDirectory(request));
        TestRunService.Validate(request with { NodeBin = "../../evil.js" }).Should().Contain("node_modules");
    }

    [Fact]
    public async Task Playwright_Отмена_ГаситДерево()
    {
        Directory.CreateDirectory(Path.Combine(_root, "frontend"));
        var launcher = new FakeLauncher(_processes, _ => new Script(Sleep: true));
        using var cts = new CancellationTokenSource();

        var run = PlaywrightService(launcher, standUp: true).RunAsync(PlaywrightRequest(), null, cts.Token);
        var spec = await launcher.SleepStarted.Task.WaitAsync(Wait);
        cts.Cancel();
        var killed = await launcher.Killed.Task.WaitAsync(Wait);
        var result = await run.WaitAsync(Wait);

        killed.Should().Be(spec.TurnId);
        result.Cancelled.Should().BeTrue();
    }

    // Стенд из webServer поднимается внутри процесса Playwright: и «Стоп», и потолок (он же
    // покрывает подъём стенда) обязаны гасить процесс по его метке — по ней песочница
    // добивает и стенд в его собственной группе (DockerProcessRunnerKillTests)
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playwright_WebServer_СтопИПотолок_ГасятПоМеткеПроцесса(bool stop)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "frontend")).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "playwright.config.ts"),
            "export default { webServer: { command: 'npm run dev', url: 'http://localhost:5173' } }");
        var launcher = new FakeLauncher(_processes, _ => new Script(Sleep: true));
        using var cts = new CancellationTokenSource();

        var run = PlaywrightService(launcher, standUp: false, ceilingSeconds: stop ? 540 : 1)
            .RunAsync(PlaywrightRequest(), null, cts.Token);
        var spec = await launcher.SleepStarted.Task.WaitAsync(Wait);
        if (stop) cts.Cancel();
        var killed = await launcher.Killed.Task.WaitAsync(Wait);
        var result = await run.WaitAsync(Wait);

        killed.Should().Be(spec.TurnId, "песочница добивает дерево хода по этой метке");
        result.Cancelled.Should().Be(stop);
        result.TimedOut.Should().Be(!stop);
    }
}
