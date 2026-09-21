using System.Diagnostics;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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

    private sealed class Factory(RecordingLauncher launcher) : ILauncherFactory
    {
        public List<string?> Owners { get; } = [];
        public IProcessLauncher Local => launcher;
        public IProcessLauncher ForOwner(string? ownerId)
        {
            Owners.Add(ownerId);
            return launcher;
        }
    }

    private (WorktreeBuildWarmup Warmup, RecordingLauncher Launcher, Factory Factory) Create(bool? enabled = null)
    {
        var launcher = new RecordingLauncher();
        var factory = new Factory(launcher);
        var values = new Dictionary<string, string?>();
        if (enabled is { } e) values["Execution:WarmupBuild"] = e ? "true" : "false";
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return (new WorktreeBuildWarmup(factory, config, NullLogger.Instance), launcher, factory);
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

        warmup.TryStart("owner-1", tree).Should().BeTrue();
        // Повторная привязка того же дерева (в том числе путём с хвостовым разделителем) — не запускать
        warmup.TryStart("owner-1", tree).Should().BeFalse();
        warmup.TryStart("owner-1", tree + Path.DirectorySeparatorChar).Should().BeFalse();

        var spec = launcher.Started.Should().ContainSingle().Subject;
        spec.FileName.Should().Be("dotnet");
        spec.Args.Should().Equal("build", "backend/ClaudeHomeServer.Tests", "-m:4", "-v:q", "-nologo");
        spec.WorkingDirectory.Should().Be(Path.GetFullPath(tree));
        spec.RedirectStdin.Should().BeFalse();
        factory.Owners.Should().Equal(["owner-1"], "прогрев идёт в среде владельца — та же изоляция, что у ходов");
    }

    [Fact]
    public void РазныеДеревья_КаждоеПрогреваетсяСвоим()
    {
        var (warmup, launcher, _) = Create();

        warmup.TryStart("o", Tree("a")).Should().BeTrue();
        warmup.TryStart("o", Tree("b")).Should().BeTrue();

        launcher.Started.Select(s => s.WorkingDirectory).Should().HaveCount(2).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void БезТестовогоПроекта_НеЗапускается()
    {
        var (warmup, launcher, _) = Create();

        warmup.TryStart("o", Tree("чужое", withTests: false)).Should().BeFalse();

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void ДеревоУжеСобиралось_НеЗапускается()
    {
        var (warmup, launcher, _) = Create();
        var tree = Tree("собранное");
        Directory.CreateDirectory(Path.Combine(tree, "backend", "ClaudeHomeServer.Tests", "obj"));

        warmup.TryStart("o", tree).Should().BeFalse("прогрев дрался бы с идущей сборкой агента за obj/bin");

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void ТумблерВыключен_НеЗапускается()
    {
        var (warmup, launcher, _) = Create(enabled: false);

        warmup.TryStart("o", Tree("wt")).Should().BeFalse();

        launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public void СбойРаннера_НеВыходитНаружу()
    {
        var factory = new ThrowingFactory();
        var warmup = new WorktreeBuildWarmup(factory, new ConfigurationBuilder().Build(), NullLogger.Instance);

        var act = () => warmup.TryStart("o", Tree("wt"));

        act.Should().NotThrow();
        warmup.TryStart("o", Tree("wt2")).Should().BeFalse();
    }

    private sealed class ThrowingFactory : ILauncherFactory
    {
        public IProcessLauncher Local => throw new InvalidOperationException("нет среды");
        public IProcessLauncher ForOwner(string? ownerId) => throw new InvalidOperationException("нет среды");
    }
}
