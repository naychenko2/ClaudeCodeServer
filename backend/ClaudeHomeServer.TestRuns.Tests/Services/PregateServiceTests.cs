using System.Collections.Concurrent;
using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.TestRuns;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeHomeServer.Tests.Services.TestRuns;

/// <summary>
/// Предгейт шага (ITaskPregate): проверки идут только из конфига Pregate:*, красная — по коду
/// выхода с текстом предупреждения компилятора, пропуск по WhenChanged, занятое дерево, шов
/// не регистрируется у выключенной подсистемы. Процесс подменяет лаунчер-заглушка: её вывод и
/// код выхода решает функция от spec (в частности — от наличия -warnaserror в аргументах).
/// </summary>
public class PregateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-pregate-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public PregateServiceTests() => Directory.CreateDirectory(_root);

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

    private sealed class FakeLauncher(List<Process> processes, string root, Func<ProcessSpec, (string Output, int Exit)> behave)
        : IProcessLauncher, ILauncherFactory
    {
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
            var (output, exit) = behave(spec);
            var file = Path.Combine(root, Guid.NewGuid().ToString("N") + ".out");
            File.WriteAllText(file, output);
            var win = OperatingSystem.IsWindows();
            var command = win ? $"type \"{file}\" & exit {exit}" : $"cat '{file}'; exit {exit}";
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = win ? "cmd.exe" : "/bin/sh",
                Args = win ? ["/c", command] : ["-c", command],
                WorkingDirectory = root,
                StdioEncoding = spec.StdioEncoding,
                RedirectStdin = false,
                Track = false,
            });
            lock (processes) processes.Add(process);
            return process;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }

        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    private Project NewProject() => new() { Id = "p1", OwnerId = "u1", Name = "Предгейт", RootPath = _root };

    private PregateRequest Request(IReadOnlyCollection<string>? changed = null) =>
        new(NewProject(), _root, "sess-1", changed);

    private static PregateCheckOptions Check(string name, string command = "dotnet", string[]? args = null,
        string[]? whenChanged = null, string workingDirectory = ".") =>
        new()
        {
            Name = name,
            Command = command,
            Args = [.. args ?? []],
            WhenChanged = [.. whenChanged ?? []],
            WorkingDirectory = workingDirectory,
        };

    private PregateService Service(FakeLauncher launcher, params PregateCheckOptions[] checks) =>
        new(launcher, new TestRunsOptions(), new PregateOptions { Checks = [.. checks] },
            new PhasePipeline(new BuildConcurrencyGate(2), TimeSpan.FromSeconds(5)));

    private FakeLauncher Launcher(Func<ProcessSpec, (string, int)> behave) => new(_processes, _root, behave);

    [Fact]
    public async Task AllChecksExitZero_VerdictGreen()
    {
        var launcher = Launcher(_ => ("всё хорошо", 0));
        var service = Service(launcher, Check("tsc", "npx", ["tsc", "-b"]), Check("build"));

        var verdict = await service.RunAsync(Request(), CancellationToken.None);

        verdict.IsGreen.Should().BeTrue();
        verdict.Checks.Select(c => c.Status).Should().OnlyContain(s => s == PregateStatus.Green);
        launcher.Specs.Select(s => s.FileName).Should().Equal("npx", "dotnet");
        launcher.Specs.Should().OnlyContain(s => s.Heavy, "проверки идут слотом очереди сборок");
    }

    [Fact]
    public async Task BuildWithWarning_VerdictRedWithCompilerWarningText()
    {
        // Сборка падает на предупреждении только при -warnaserror: без флага вердикт был бы зелёным
        const string warning = "Foo.cs(12,9): warning CS8618: Non-nullable property 'Bar' must contain a non-null value";
        var launcher = Launcher(spec => spec.Args.Contains("-warnaserror") ? (warning + "\nBuild FAILED.", 1) : ("Build succeeded.", 0));
        var service = Service(launcher, Check("build -warnaserror", args: ["build", "-warnaserror"]));

        var verdict = await service.RunAsync(Request(), CancellationToken.None);

        verdict.IsGreen.Should().BeFalse();
        var check = verdict.Checks.Should().ContainSingle().Subject;
        check.Status.Should().Be(PregateStatus.Red);
        check.Detail.Should().Contain("warning CS8618").And.Contain("код выхода 1");
    }

    [Fact]
    public async Task WhenChangedDoesNotMatch_CheckSkippedAndNotStarted()
    {
        var launcher = Launcher(_ => ("", 1));
        var service = Service(launcher, Check("tsc", whenChanged: ["frontend/"]));

        var verdict = await service.RunAsync(Request(["backend/ClaudeHomeServer/Program.cs"]), CancellationToken.None);

        verdict.IsGreen.Should().BeTrue();
        verdict.Checks.Should().ContainSingle().Which.Status.Should().Be(PregateStatus.Skipped);
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenChangedMatches_CheckRuns()
    {
        var launcher = Launcher(_ => ("", 0));
        var service = Service(launcher, Check("tsc", whenChanged: ["frontend/"]));

        var verdict = await service.RunAsync(Request(["frontend\\src\\App.tsx"]), CancellationToken.None);

        verdict.Checks.Should().ContainSingle().Which.Status.Should().Be(PregateStatus.Green);
        launcher.Specs.Should().ContainSingle();
    }

    [Fact]
    public async Task NoChecksConfigured_GreenWithoutLaunching()
    {
        var launcher = Launcher(_ => ("", 1));

        var verdict = await Service(launcher).RunAsync(Request(), CancellationToken.None);

        verdict.IsGreen.Should().BeTrue();
        verdict.Checks.Should().BeEmpty();
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkingDirectoryEscapesTree_CheckRedWithoutLaunching()
    {
        var launcher = Launcher(_ => ("", 0));
        var service = Service(launcher, Check("evil", workingDirectory: "../outside"));

        var verdict = await service.RunAsync(Request(), CancellationToken.None);

        verdict.IsGreen.Should().BeFalse();
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task СуммаПроверокДлиннееПотолка_ОдинПотолокНаПрогон_RefusedСВыполненными()
    {
        // Потолок 4 с, каждая проверка 1,5 с: первая укладывается с запасом 2,5 с даже под нагрузкой, а четыре вместе
        // (6 с) не помещаются с запасом 2 с — потолок обязан быть общим. Пропущенная проверка выполненной не считается
        var launcher = Launcher(_ => { Thread.Sleep(1500); return ("ok", 0); });
        var service = new PregateService(launcher, new TestRunsOptions { CeilingSeconds = 4 },
            new PregateOptions { Checks = [Check("skip", whenChanged: ["nope/"]), Check("c1"), Check("c2"), Check("c3"), Check("c4")] },
            new PhasePipeline(new BuildConcurrencyGate(2), TimeSpan.FromSeconds(5)));

        var verdict = await service.RunAsync(Request(["src/a.cs"]), CancellationToken.None);

        verdict.IsGreen.Should().BeFalse();
        verdict.Refusal.Should().Contain("общий потолок").And.Contain("Уже выполнено").And.Contain("c1");
        verdict.Refusal.Should().NotContain("skip — Skipped", "пропущенная проверка не выполнялась").And.Contain("Пропущено");
        verdict.Checks.Should().NotBeEmpty().And.HaveCountLessThan(5);
        verdict.Checks.Select(c => c.Name).Should().StartWith(["skip", "c1"]);
    }

    [Fact]
    public async Task TreeBusy_RefusedAndRed()
    {
        var launcher = Launcher(_ => ("", 0));
        var pipeline = new PhasePipeline(new BuildConcurrencyGate(2), TimeSpan.FromSeconds(5));
        var service = new PregateService(launcher, new TestRunsOptions(),
            new PregateOptions { Checks = [Check("build")] }, pipeline);

        using (pipeline.TryLockTree(_root))
        {
            var verdict = await service.RunAsync(Request(), CancellationToken.None);

            verdict.IsGreen.Should().BeFalse();
            verdict.Refusal.Should().Contain("уже идёт сборка");
            launcher.Specs.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task ChangedFilesOutsideEveryZone_AllSkipped_NothingApplicable()
    {
        var launcher = Launcher(_ => ("", 0));
        var service = Service(launcher, Check("tsc", whenChanged: ["frontend/"]), Check("build", whenChanged: ["backend/"]));

        var verdict = await service.RunAsync(Request(["docs/заметка.md"]), CancellationToken.None);

        verdict.Checks.Should().OnlyContain(c => c.Status == PregateStatus.Skipped);
        verdict.NothingApplicable.Should().BeTrue();
        verdict.Unmeasured.Should().BeNull();
        launcher.Specs.Should().BeEmpty();
    }

    [Fact]
    public async Task OneZoneHit_VerdictConclusive()
    {
        var service = Service(Launcher(_ => ("", 0)), Check("tsc", whenChanged: ["frontend/"]), Check("build", whenChanged: ["backend/"]));

        var verdict = await service.RunAsync(Request(["backend/x.cs"]), CancellationToken.None);

        verdict.NothingApplicable.Should().BeFalse();
        verdict.Unmeasured.Should().BeNull();
        verdict.IsGreen.Should().BeTrue();
    }

    [Fact]
    public async Task UiProbeWithoutMeasurement_IsNotRun_AndMakesVerdictUnmeasured()
    {
        var service = Service(Launcher(_ => ("", 0)),
            Check("tsc"), new PregateCheckOptions { Name = "ui", Kind = PregateCheckOptions.KindUiProbe });

        var verdict = await service.RunAsync(Request(["frontend/a.tsx"]), CancellationToken.None);

        verdict.Checks.Single(c => c.Name == "ui").Status.Should().Be(PregateStatus.NotRun);
        verdict.Unmeasured.Should().Contain("ui").And.Contain("маршрут не задан");
        verdict.NothingApplicable.Should().BeFalse();
    }

    [Fact]
    public async Task UiProbeWithRoutesButNoProbeService_IsNotRun()
    {
        var service = Service(Launcher(_ => ("", 0)), new PregateCheckOptions { Name = "ui", Kind = PregateCheckOptions.KindUiProbe });

        var verdict = await service.RunAsync(Request(["frontend/a.tsx"]) with { UiRoutes = ["/#/ui-kit"] }, CancellationToken.None);

        verdict.Checks.Single().Should().Match<PregateCheckResult>(c => c.Status == PregateStatus.NotRun && c.Detail.Contains("недоступен"));
    }

    [Fact]
    public void Verdict_RedWinsOverNotRun_AndEmptySetStaysConclusive()
    {
        new PregateVerdict([new("a", PregateStatus.Red, "x"), new("b", PregateStatus.NotRun, "y")]).Unmeasured.Should().BeNull();
        new PregateVerdict([]).Unmeasured.Should().BeNull();
        new PregateVerdict([]).NothingApplicable.Should().BeFalse();
    }

    [Fact]
    public void Options_ReadOnlyFromPregateSection_NoBuiltInChecks()
    {
        PregateOptions.From(new ConfigurationBuilder().Build()).Checks.Should().BeEmpty("в коде зашитых проверок нет");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Набор проверок — объект по имени проверки: имя = ключ (не индекс!)
            ["Pregate:Checks:tsc:Command"] = "npx",
            ["Pregate:Checks:tsc:Args:0"] = "tsc",
            ["Pregate:Checks:tsc:Args:1"] = "-b",
            ["Pregate:Checks:tsc:WorkingDirectory"] = "frontend",
            ["Pregate:Checks:tsc:WhenChanged:0"] = "frontend/",
            ["Pregate:Checks:ui:Kind"] = "ui-probe",
            ["Pregate:Checks:ui:Origin"] = "http://127.0.0.1:5555",
            ["Pregate:Checks:ui:Viewports:0"] = "360",
            ["Pregate:Checks:ui:Viewports:1"] = "1280",
        }).Build();

        var options = PregateOptions.From(config);

        options.Problem.Should().BeNull();
        options.Checks.Should().HaveCount(2);
        options.Checks[0].Should().BeEquivalentTo(new PregateCheckOptions
        {
            Name = "tsc", Command = "npx", Args = ["tsc", "-b"], WorkingDirectory = "frontend", WhenChanged = ["frontend/"],
        });
        options.Checks[1].Kind.Should().Be(PregateCheckOptions.KindUiProbe);
        options.Checks[1].Viewports.Should().Equal(360, 1280);
    }

    private static ServiceCollection Services(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSubsystems(config, new TestRunsSubsystem());
        return services;
    }

    [Fact]
    public async Task DefaultSet_FrontendStep_ClosesAfterGreenTscAndLintDesign()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ClaudeHomeServer", "appsettings.json")))
            dir = dir.Parent;
        dir.Should().NotBeNull("appsettings.json основного проекта ищем вверх от каталога тестов");
        var config = new ConfigurationBuilder().AddJsonFile(Path.Combine(dir!.FullName, "ClaudeHomeServer", "appsettings.json")).Build();
        var options = PregateOptions.From(config);
        options.Checks.Should().NotContain(c => c.Kind == PregateCheckOptions.KindUiProbe, "ui_probe мерит стенд чужого checkout и не входит в набор по умолчанию");

        var launcher = Launcher(_ => ("", 0));
        var service = new PregateService(launcher, new TestRunsOptions(), options,
            new PhasePipeline(new BuildConcurrencyGate(2), TimeSpan.FromSeconds(5)));

        var verdict = await service.RunAsync(Request(["frontend/src/a.tsx"]), CancellationToken.None);

        verdict.IsGreen.Should().BeTrue();
        verdict.Unmeasured.Should().BeNull();
        verdict.Checks.Where(c => c.Status == PregateStatus.Green).Select(c => c.Name).Should().Equal("tsc", "lint:design");
    }

    [Fact]
    public void SubsystemEnabled_RegistersPregateSeam()
    {
        var services = Services(new ConfigurationBuilder().Build());

        services.Should().Contain(s => s.ServiceType == typeof(ITaskPregate) && s.ImplementationType == typeof(PregateService));
    }

    [Fact]
    public void SubsystemDisabled_PregateSeamNotRegistered()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"Subsystems:{TestRunsSubsystem.SubsystemKey}:Enabled"] = "false" }).Build();

        var services = Services(config);

        services.Should().NotContain(s => s.ServiceType == typeof(ITaskPregate));
        services.Should().NotContain(s => s.ServiceType == typeof(PregateOptions));
    }
}
