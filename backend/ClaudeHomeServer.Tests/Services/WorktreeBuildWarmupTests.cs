using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Прогрев сборки свежего worktree: ровно один запуск на дерево, с ожидаемой командой и в среде
// владельца; без тестового проекта, при уже собранном дереве и при выключенном тумблере — ни
// одного. Раннер поддельный: запуски только записываются, процесс не стартует (наблюдатель
// ловит исключение незапущенного Process и пишет его в лог — наружу ничего не выходит).
public class WorktreeBuildWarmupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-warmup-" + Guid.NewGuid().ToString("N")[..8]);

    public WorktreeBuildWarmupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* уборка best-effort */ }
    }

    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<ProcessSpec> Started { get; } = [];
        public bool IsSandboxed => false;
        public bool TargetIsWindows => false;
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;
        public Process Start(ProcessSpec spec)
        {
            lock (Started) Started.Add(spec);
            return new Process();
        }
        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    private static readonly Project P1 = new() { Id = "p-1", OwnerId = "owner-1" };
    private static readonly Project P = new() { Id = "p", OwnerId = "o" };

    private sealed class Factory(RecordingLauncher launcher) : ILauncherFactory
    {
        public List<string?> Projects { get; } = [];
        public IProcessLauncher Local => launcher;
        public IProcessLauncher ForOwner(string? ownerId) =>
            throw new InvalidOperationException("прогрев дерева проекта идёт в среде проекта, не владельца");
        public IProcessLauncher ForProject(Project project)
        {
            Projects.Add(project.Id);
            return launcher;
        }
    }

    // Потолок тяжёлых запусков — СВОЙ на каждый тест (по умолчанию выключен): статический
    // BuildConcurrencyGate.Instance общий на процесс, и занятый чужим тестом слот подвесил бы
    // прогрев в фоне.
    private (WorktreeBuildWarmup Warmup, RecordingLauncher Launcher, Factory Factory) Create(
        bool? enabled = null, BuildConcurrencyGate? gate = null, ILogger? log = null)
    {
        var launcher = new RecordingLauncher();
        var factory = new Factory(launcher);
        var values = new Dictionary<string, string?>();
        if (enabled is { } e) values["Execution:WarmupBuild"] = e ? "true" : "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var warmup = new WorktreeBuildWarmup(
            factory, config, log ?? NullLogger.Instance, gate ?? new BuildConcurrencyGate(0));
        return (warmup, launcher, factory);
    }

    private string Tree(string name, bool withTests = true)
    {
        var tree = Path.Combine(_root, name);
        Directory.CreateDirectory(tree);
        if (withTests) Directory.CreateDirectory(Path.Combine(tree, "backend", "ClaudeHomeServer.Tests"));
        return tree;
    }

    [Fact]
    public void НовоеДерево_ПрогревРовноОдинРаз_СОжидаемойКомандой()
    {
        var (warmup, launcher, factory) = Create();
        var tree = Tree("wt");

        warmup.TryStart(P1, tree).Should().BeTrue();
        // Повторная привязка того же дерева (в том числе путём с хвостовым разделителем) — не запускать
        warmup.TryStart(P1, tree).Should().BeFalse();
        warmup.TryStart(P1, tree + Path.DirectorySeparatorChar).Should().BeFalse();

        var spec = launcher.Started.Should().ContainSingle().Subject;
        spec.FileName.Should().Be("dotnet");
        spec.Args.Should().Equal("build", "backend/ClaudeHomeServer.Tests", "-m:4", "-v:q", "-nologo");
        spec.WorkingDirectory.Should().Be(Path.GetFullPath(tree));
        spec.RedirectStdin.Should().BeFalse();
        factory.Projects.Should().Equal(["p-1"], "прогрев идёт в среде проекта — та же изоляция, что у ходов");
    }

    [Fact]
    public void РазныеДеревья_КаждоеПрогреваетсяСвоим()
    {
        var (warmup, launcher, _) = Create();

        warmup.TryStart(P, Tree("a")).Should().BeTrue();
        warmup.TryStart(P, Tree("b")).Should().BeTrue();

        launcher.Started.Select(s => s.WorkingDirectory).Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void БезТестовогоПроекта_НеЗапускается()
    {
        var (warmup, launcher, _) = Create();

        warmup.TryStart(P, Tree("чужое", withTests: false)).Should().BeFalse();

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void ДеревоУжеСобиралось_НеЗапускается()
    {
        var (warmup, launcher, _) = Create();
        var tree = Tree("собранное");
        Directory.CreateDirectory(Path.Combine(tree, "backend", "ClaudeHomeServer.Tests", "obj"));

        warmup.TryStart(P, tree).Should().BeFalse("прогрев дрался бы с идущей сборкой агента за obj/bin");

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void ТумблерВыключен_НеЗапускается()
    {
        var (warmup, launcher, _) = Create(enabled: false);

        warmup.TryStart(P, Tree("wt")).Should().BeFalse();

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void СбойРаннера_НеВыходитНаружу()
    {
        var factory = new ThrowingFactory();
        var gate = new BuildConcurrencyGate(1);
        var warmup = new WorktreeBuildWarmup(
            factory, new ConfigurationBuilder().Build(), NullLogger.Instance, gate);

        var act = () => warmup.TryStart(P, Tree("wt"));

        act.Should().NotThrow();
        warmup.TryStart(P, Tree("wt2")).Should().BeFalse();
        gate.Available.Should().Be(1, "упавший старт возвращает слот, иначе потолок утекает");
    }

    [Fact]
    public async Task СлотЗанят_ХодНеЖдёт_ПрогревСтартуетПоОсвобождении()
    {
        var gate = new BuildConcurrencyGate(1);
        var (warmup, launcher, _) = Create(gate: gate);
        var busy = gate.TryAcquire(WorktreeBuildWarmup.BuildSpec("/чужое/дерево"))!;
        var tree = Tree("в-очереди");

        // Заведение дерева идёт в ходе чата/задачи — оно обязано вернуться немедленно
        warmup.TryStart(P, tree).Should().BeTrue("прогрев принят, хоть и ждёт слота");
        launcher.Started.Should().BeEmpty("слот занят — сборка ещё не стартовала");

        busy.Dispose();

        (await WaitForStartAsync(launcher)).Should().BeTrue("освобождённый слот пустил прогрев");
        launcher.Started.Should().ContainSingle().Which.WorkingDirectory.Should().Be(Path.GetFullPath(tree));
        // Главный путь возврата слота — выход процесса, и он же самый дорогой при поломке:
        // не отданный слот упавшей сборки опускает потолок навсегда, до рестарта. Поддельный
        // процесс не запускается, наблюдатель падает на чтении потоков — это и есть ветка
        // «прогрев кончился не по-хорошему», слот обязан вернуться и в ней
        (await WaitForSlotFreeAsync(gate, 1)).Should().BeTrue("завершившийся прогрев вернул слот");
    }

    [Fact]
    public async Task ПокаЖдалСлота_ДеревоНачалиСобирать_ПрогревОтменяется()
    {
        var gate = new BuildConcurrencyGate(1);
        var log = new RecordingLogger();
        var (warmup, launcher, _) = Create(gate: gate, log: log);
        var busy = gate.TryAcquire(WorktreeBuildWarmup.BuildSpec("/чужое/дерево"))!;
        var tree = Tree("перехваченное");

        warmup.TryStart(P, tree).Should().BeTrue();
        // Агент начал собирать это дерево сам, пока прогрев стоял в очереди
        Directory.CreateDirectory(Path.Combine(tree, "backend", "ClaudeHomeServer.Tests", "obj"));
        busy.Dispose();

        // Ждём РЕШЕНИЯ фоновой задачи по её следу в логе: «слот освободился» наступает и без
        // отмены (сорвавшийся прогрев вернёт слот сам), и на него проверку вешать нельзя
        (await WaitForAsync(() => log.Contains("отменён"))).Should().BeTrue("прогрев отменён");
        launcher.Started.Should().BeEmpty("прогрев дрался бы с идущей сборкой агента за obj/bin");
        (await WaitForSlotFreeAsync(gate, 1)).Should().BeTrue("отменённый прогрев вернул слот");
    }

    // Фоновый старт ловим опросом: паузой фиксированной длины CI на голодном раннере флейчит
    private static Task<bool> WaitForStartAsync(RecordingLauncher launcher) =>
        WaitForAsync(() => { lock (launcher.Started) return launcher.Started.Count > 0; });

    private static Task<bool> WaitForSlotFreeAsync(BuildConcurrencyGate gate, int expected) =>
        WaitForAsync(() => gate.Available >= expected);

    // Лог как наблюдаемая точка решения фоновой задачи
    private sealed class RecordingLogger : ILogger
    {
        private readonly List<string> _lines = [];

        public bool Contains(string fragment)
        {
            lock (_lines) return _lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));
        }

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, ex));
        }

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(15);
        }
        return condition();
    }

    private sealed class ThrowingFactory : ILauncherFactory
    {
        public IProcessLauncher Local => throw new InvalidOperationException("нет среды");
        public IProcessLauncher ForOwner(string? ownerId) => throw new InvalidOperationException("нет среды");
        public IProcessLauncher ForProject(Project project) => throw new InvalidOperationException("нет среды");
    }
}
