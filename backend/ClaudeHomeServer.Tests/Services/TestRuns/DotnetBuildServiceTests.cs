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
/// Движок сборки `dotnet build` на общем с run_tests конвейере фаз: один экземпляр
/// PhasePipeline из DI у обоих движков (блокировка «одно дерево» общая), прогресс «N из M»,
/// память прошлой успешной сборки, сводка ошибок, гашение по «Стоп». Процесс подменяет
/// лаунчер-заглушка: печатает записанный вывод dotnet из фикстуры или спит; Kill отмечает
/// событие — тест ждёт именно его, без Task.Delay.
/// </summary>
public class DotnetBuildServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-dotnetbuild-" + Guid.NewGuid().ToString("N"));
    // Каталог данных сервера — ВНЕ дерева, как data/ у живого бэкенда
    private readonly string _data = Path.Combine(Path.GetTempPath(), "ccs-dotnetbuild-data-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public DotnetBuildServiceTests() => Directory.CreateDirectory(_root);

    private string MemoryDir => Path.Combine(_data, BuildRunMemory.DirName);

    private TestRunsOptions Options() => new() { MemoryDirectory = MemoryDir };

    private string MemoryPath(string? target = "Alpha.App/Alpha.App.csproj") =>
        BuildRunMemory.PathFor(MemoryDir, _root, target);

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

    // Что делает «dotnet build» (и «npm run» в NpmBuildServiceTests): печатает фикстуру и выходит с кодом или спит
    internal sealed class FakeLauncher(List<Process> processes, string? output, int exit = 0, bool sleep = false)
        : IProcessLauncher, ILauncherFactory
    {
        public TaskCompletionSource<ProcessSpec> SleepStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Killed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<ProcessSpec> Specs { get; } = new();

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
            var win = OperatingSystem.IsWindows();
            var fixture = output is null ? null : TestRunServiceTests.Fixture(output);
            // sleep с фикстурой — сначала печатает её, потом спит (абсолютный путь фикстуры тоже годится)
            var print = fixture is null ? "" : win ? $"type {fixture} & " : $"cat '{fixture}'; ";
            var command = sleep
                ? print + (win ? "ping -n 120 127.0.0.1 >nul" : "sleep 120")
                : (win ? $"type {fixture} & exit {exit}" : $"cat '{fixture}'; exit {exit}");
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
            if (sleep) SleepStarted.TrySetResult(spec);
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            Killed.TrySetResult(turnId);
        }

        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    private sealed class ProgressLog
    {
        private readonly ConcurrentQueue<TestRunProgress> _items = new();
        public Action<TestRunProgress> Sink => _items.Enqueue;
        public IReadOnlyList<TestRunProgress> Items => [.. _items];
    }

    private Project NewProject() => new() { Id = "p1", OwnerId = "u1", Name = "Сборка", RootPath = _root };

    private DotnetBuildRequest Request(string? target = "Alpha.App/Alpha.App.csproj") => new(NewProject(), _root, "sess-1", target);

    private readonly List<string> _cleanup = [];

    private DotnetBuildService Service(FakeLauncher launcher, BuildConcurrencyGate gate) =>
        new(launcher, Options(), new PhasePipeline(gate, TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(5));

    // Три проекта на диске — оценка M по XML для первого прогона
    private void SeedProjects()
    {
        void Project(string name, params string[] refs)
        {
            Directory.CreateDirectory(Path.Combine(_root, name));
            var items = string.Concat(refs.Select(r => $"<ProjectReference Include=\"..\\{r}\\{r}.csproj\" />"));
            File.WriteAllText(Path.Combine(_root, name, name + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>");
        }
        Project("Alpha.Core");
        Project("Alpha.Data", "Alpha.Core");
        Project("Alpha.App", "Alpha.Data");
    }

    [Fact]
    public void DI_ОбаДвижкаПолучаютОдинКонвейер()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILauncherFactory>(new FakeLauncher(_processes, null));
        new TestRunsSubsystem().Register(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var tests = provider.GetRequiredService<TestRunService>();
        var build = provider.GetRequiredService<DotnetBuildService>();

        build.Pipeline.Should().BeSameAs(tests.Pipeline, "блокировка «одно дерево» обязана быть общей");
        tests.Pipeline.Should().BeSameAs(provider.GetRequiredService<PhasePipeline>());
    }

    [Fact]
    public async Task ОбщаяБлокировка_СборкаИТестыВОдномДеревеНеИдутВместе()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILauncherFactory>(new FakeLauncher(_processes, "dotnet-build-noop.txt"));
        new TestRunsSubsystem().Register(services, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var tests = provider.GetRequiredService<TestRunService>();
        var build = provider.GetRequiredService<DotnetBuildService>();

        using (tests.Pipeline.TryLockTree(_root))
        {
            var refused = await build.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);
            refused.Refusal.Should().Contain("уже идёт сборка или прогон тестов");
        }
        using (build.Pipeline.TryLockTree(_root))
        {
            var refused = await tests.RunDotnetAsync(new TestRunRequest(NewProject(), _root, "sess-1"), null,
                CancellationToken.None).WaitAsync(Wait);
            refused.Refusal.Should().Contain("сборка");
        }
    }

    // Ждать занятое дерево агенту надо сторожем: маркер виден снаружи ровно пока идёт прогон,
    // а отказ подсказывает, чего ждать
    [Fact]
    public async Task ЗанятоеДерево_МаркерДляСторожа_ИПодсказкаВОтказе()
    {
        var service = Service(new FakeLauncher(_processes, "dotnet-build-noop.txt"), new BuildConcurrencyGate(1));
        var marker = Path.Combine(_root, PhasePipeline.BusyMarker);
        // Маркер от упавшего хоста не мешает новой блокировке
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "stale");

        using (service.Pipeline.TryLockTree(_root))
        {
            File.Exists(marker).Should().BeTrue();
            var refused = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);
            refused.Refusal.Should().Contain("watch_start").And.Contain(PhasePipeline.BusyMarker);
        }
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task ПервыйПрогон_ОценкаПоXml_ВторойПоПамяти()
    {
        SeedProjects();
        var gate = new BuildConcurrencyGate(1);
        var launcher = new FakeLauncher(_processes, "dotnet-build-cold.txt");
        var service = Service(launcher, gate);
        var first = new ProgressLog();

        var cold = await service.RunAsync(Request(), first.Sink, CancellationToken.None).WaitAsync(Wait);

        cold.ExitCode.Should().Be(0);
        cold.Done.Should().Be(3);
        cold.ProgressLabel.Should().Be("≈3 из 3 проектов", "первый раз M — оценка обходом ProjectReference");
        cold.BuildErrors.Should().BeEmpty();
        first.Items.Should().NotBeEmpty().And.AllSatisfy(p =>
        {
            p.Stage.Should().Be("build");
            p.Exact.Should().BeFalse("процент сборки — оценка, рисуется пунктиром");
            p.Percent.Should().BeLessThanOrEqualTo(DotnetBuildProgress.EstimateCeiling);
        });
        launcher.Specs.Should().ContainSingle().Which.Should().Match<ProcessSpec>(s =>
            s.Heavy && s.PrivateBuildNodes && s.Track && s.TurnId!.Length == 12 && s.Args[0] == "build" && s.Args[1] == "Alpha.App/Alpha.App.csproj"
            && s.Env!["DOTNET_CLI_UI_LANGUAGE"] == "en");
        gate.Available.Should().Be(1, "слот освобождается по концу сборки");
        File.Exists(Path.Combine(_root, cold.ArtifactsPath!, "console.log")).Should().BeTrue();
        File.Exists(MemoryPath()).Should().BeTrue("успешная сборка помнит M и веса");

        var second = new ProgressLog();
        var noop = await Service(new FakeLauncher(_processes, "dotnet-build-noop.txt"), gate)
            .RunAsync(Request(), second.Sink, CancellationToken.None).WaitAsync(Wait);

        noop.ProgressLabel.Should().Be("3 из 3 проектов", "M из памяти — без «≈»");
        second.Items[0].Label.Should().Be("0 из 3 проектов", "M известно с первого события");
        service.FormatResult(noop).Should().StartWith("Собрано за").And.Contain("3 из 3 проектов, 0 ошибок");
    }

    [Fact]
    public async Task СОшибкой_СводкаОшибокИПамятьНеПишется()
    {
        var service = Service(new FakeLauncher(_processes, "dotnet-build-error.txt", exit: 1), new BuildConcurrencyGate(1));

        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(1);
        result.Done.Should().Be(1);
        result.BuildErrors.Should().ContainSingle().Which.Should().Contain("error CS0103");
        File.Exists(MemoryPath()).Should().BeFalse("упавшая сборка — не эталон весов");
        service.FormatResult(result).Should().Contain("Сборка упала (код 1").And.Contain("Первые ошибки сборки (1)");
    }

    [Fact]
    public async Task Память_ВнеДерева_СтараяПамятьВДеревеНеЧитается()
    {
        SeedProjects();
        // Подлог в старом месте памяти: M=99 — если бы её прочли, подпись была бы «0 из 99»
        var legacy = Path.Combine(_root, ".cc-attachments", DotnetBuildService.ArtifactsSubdir, "memory");
        Directory.CreateDirectory(legacy);
        var forged = """{"total":99,"seconds":50,"finished":{"Alpha.App":1}}""";
        foreach (var name in new[] { "alpha.app_alpha.app.csproj.json", Path.GetFileName(MemoryPath()) })
            File.WriteAllText(Path.Combine(legacy, name), forged);

        var cold = await Service(new FakeLauncher(_processes, "dotnet-build-cold.txt"), new BuildConcurrencyGate(1))
            .RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        cold.ProgressLabel.Should().Be("≈3 из 3 проектов", "память в дереве не читается — первый прогон по оценке");
        File.Exists(MemoryPath()).Should().BeTrue();
        MemoryPath().Should().StartWith(MemoryDir, "память — в каталоге данных сервера");
        Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(legacy, StringComparison.Ordinal))
            .Should().BeEmpty("в рабочее дерево память не пишется");
        Directory.GetFiles(legacy).Should().HaveCount(2, "и подлог не тронут");
    }

    [Fact]
    public async Task Память_БезКаталогаДанных_СборкаИдётБезНеё()
    {
        SeedProjects();
        var service = new DotnetBuildService(new FakeLauncher(_processes, "dotnet-build-cold.txt"), new TestRunsOptions(),
            new PhasePipeline(new BuildConcurrencyGate(1), TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(5));

        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(0);
        result.ProgressLabel.Should().Be("≈3 из 3 проектов");
        Directory.Exists(_data).Should().BeFalse();
    }

    [Fact]
    public async Task Артефакты_СсылкаВместоBuildRuns_НаружуНичегоНеПишетсяСборкаИдёт()
    {
        var outside = TreeLinks.Outside("ccs-dotnetbuild-out-");
        _cleanup.Add(outside);
        Directory.CreateDirectory(Path.Combine(_root, ".cc-attachments"));
        if (!TreeLinks.TryLink(Path.Combine(_root, ".cc-attachments", DotnetBuildService.ArtifactsSubdir), outside,
                directory: true))
            return; // проверка живёт в CI на Linux

        var service = Service(new FakeLauncher(_processes, "dotnet-build-noop.txt"), new BuildConcurrencyGate(1));
        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(0, "ссылка в пути артефактов — не повод валить сборку");
        result.ArtifactsPath.Should().BeNull("лога нет — и ссылки на него в итоге нет");
        service.FormatResult(result).Should().NotContain("console.log");
        Directory.EnumerateFileSystemEntries(outside, "*", SearchOption.AllDirectories).Should()
            .BeEmpty("хост не создал за деревом ни каталога, ни лога");
    }

    [Fact]
    public async Task Стоп_ГаситДеревоПоTurnIdСборки()
    {
        var launcher = new FakeLauncher(_processes, null, sleep: true);
        var service = Service(launcher, new BuildConcurrencyGate(1));
        using var cts = new CancellationTokenSource();

        var running = service.RunAsync(Request(), null, cts.Token);
        var spec = await launcher.SleepStarted.Task.WaitAsync(Wait);
        await cts.CancelAsync();
        var result = await running.WaitAsync(Wait);

        (await launcher.Killed.Task.WaitAsync(Wait)).Should().Be(spec.TurnId, "без метки kill не тронет процесс в песочнице");
        result.Cancelled.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
        result.NeverStarted.Should().BeFalse();
    }

    [Fact]
    public async Task ОценкаЗависла_СборкаБезОценкиИБлокировкаНеЗалипает()
    {
        using var never = new ManualResetEventSlim();
        var service = new DotnetBuildService(new FakeLauncher(_processes, "dotnet-build-noop.txt"), Options(),
            new PhasePipeline(new BuildConcurrencyGate(1), TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(5),
            estimate: (_, _, _) => { never.Wait(Wait); return 3; },
            estimateTimeout: TimeSpan.FromMilliseconds(100));

        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(0);
        result.ProgressLabel.Should().Be("3 проекта", "оценка не уложилась в потолок — без «из M»");
        using var again = service.Pipeline.TryLockTree(_root);
        again.Should().NotBeNull("блокировка дерева отпущена");
        never.Set();
    }

    [Fact]
    public async Task ОценкаУпала_СборкаБезОценки()
    {
        var service = new DotnetBuildService(new FakeLauncher(_processes, "dotnet-build-noop.txt"), Options(),
            new PhasePipeline(new BuildConcurrencyGate(1), TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(5),
            estimate: (_, _, _) => throw new InvalidOperationException("сбой"));

        var result = await service.RunAsync(Request(), null, CancellationToken.None).WaitAsync(Wait);

        result.ExitCode.Should().Be(0);
        result.ProgressLabel.Should().Be("3 проекта");
    }

    [Fact]
    public async Task ЦельОпция_Отказ()
    {
        var launcher = new FakeLauncher(_processes, "dotnet-build-noop.txt");
        var result = await Service(launcher, new BuildConcurrencyGate(1))
            .RunAsync(Request("@evil.rsp"), null, CancellationToken.None).WaitAsync(Wait);

        result.Refusal.Should().Contain("response-файл");
        launcher.Specs.Should().BeEmpty();
    }
}
