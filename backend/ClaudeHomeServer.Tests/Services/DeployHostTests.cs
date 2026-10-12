using System.Diagnostics;
using ClaudeHomeServer.Services.Composition;
using ClaudeHomeServer.Services.Composition.Deploy;
using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Интеграционная проверка DeployHost: GitSnapshotAsync на реальном git CLI, argv Linux-побудки
// (systemd-run) поэлементно через подставной launcher, и файловый лок «агент жив».
//
// GitSnapshot — закрыть регрессию «git status упал → DeployHost молча вернул чистое дерево»:
// новая GitRepoSnapshot обязана пробрасывать Error в DeployGitSnapshot, иначе DeployService
// пропустит выкатку с непрочитанным состоянием.
//
// argv Linux-побудки — контракт ADR-010: фиксированный argv без полей заявки,
// --unit=ccs-deploy-<id> и --setenv=CCS_RELEASES_DIR=<ReleasesDir> из конфига.
//
// Лок «агент жив» — Windows: именованный мьютекс, Linux: файл deploy.lock (flock).
// Второй acquire на том же файле → null; после Dispose — снова удаётся.
// flock-тест (только Linux): пока bash держит flock -x, .NET-лок (FileShare.None) не возьмётся.
[Trait("Category", "Slow")]
public class DeployHostTests : IDisposable
{
    private readonly string _repo;
    private readonly DeployHost _host;

    public DeployHostTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "deployhost_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        var git = new GitService(new LocalOnlyFactory());
        git.InitAsync(null, _repo).GetAwaiter().GetResult();
        // Нужен хотя бы один коммит, чтобы rev-parse отдавал валидный sha
        _ = RunRawGit(_repo, "config", "user.email", "t@t");
        _ = RunRawGit(_repo, "config", "user.name", "T");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        git.StageAllAsync(null, _repo).GetAwaiter().GetResult();
        git.CommitAsync(null, _repo, "seed").GetAwaiter().GetResult();
        _host = new DeployHost(git, new LocalOnlyFactory(), new NoopDeployAgentLock(), NullLogger<DeployHost>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch { /* git на Windows держит readonly-объекты — не роняем прогон */ }
    }

    private sealed class LocalOnlyFactory : ILauncherFactory
    {
        public IProcessLauncher Local => LocalProcessRunner.Instance;
        public IProcessLauncher ForOwner(string? ownerId) => Local;
        public IProcessLauncher ForProject(ClaudeHomeServer.Models.Project project) => Local;
    }

    // Подставной launcher: записывает ProcessSpec и возвращает реальный тривиальный процесс
    // (true / exit 0), чтобы WakeAgentAsync прошёл ReadToEndAsync + WaitForExitAsync без
    // исключений. Тест читает Started и проверяет argv поэлементно.
    private sealed class RecordingLauncher : IProcessLauncher
    {
        public List<ProcessSpec> Started { get; } = [];
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            lock (Started) Started.Add(spec);
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (OperatingSystem.IsWindows())
            {
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("exit");
                psi.ArgumentList.Add("0");
            }
            else
            {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("true");
            }
            return Process.Start(psi)!;
        }

        public void Kill(Process process, string? turnId = null) { }
        public int EstimateCommandLineLength(ProcessSpec spec) => 0;
    }

    private sealed class RecordingFactory(RecordingLauncher launcher) : ILauncherFactory
    {
        public IProcessLauncher Local => launcher;
        public IProcessLauncher ForOwner(string? ownerId) => launcher;
        public IProcessLauncher ForProject(ClaudeHomeServer.Models.Project project) => launcher;
    }

    private static string RunRawGit(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
    }

    [Fact]
    public async Task GitSnapshot_ЧистоеДерево_ErrorNullИDirtyПуст()
    {
        var snap = await _host.GitSnapshotAsync(_repo);

        snap.Error.Should().BeNull();
        snap.DirtyFiles.Should().BeEmpty();
        snap.Sha.Should().NotBeNullOrEmpty();
    }

    // Ключевая регрессия: git status упал (имитация — core.bare=true) → DeployHost обязан
    // вернуть Error, а не молча посчитать дерево чистым. DeployService смотрит на Error
    // и блокирует выкатку гейтом GitFailed.
    [Fact]
    public async Task GitSnapshot_GitStatusУпал_ErrorПроброшен_ГрязныхНеДоверяем()
    {
        _ = RunRawGit(_repo, "config", "core.bare", "true");

        var snap = await _host.GitSnapshotAsync(_repo);

        snap.Error.Should().NotBeNullOrEmpty("битый git-конфиг → Error обязателен, иначе гейт грязного дерева пропустит выкатку");
        snap.DirtyFiles.Should().BeEmpty("пустой список при сбое status — это НЕ чистое дерево");
    }

    [Fact]
    public async Task GitSnapshot_НеРепозиторий_ErrorПонятный()
    {
        var notRepo = Path.Combine(Path.GetTempPath(), "deployhost_notrepo_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(notRepo);
            var snap = await _host.GitSnapshotAsync(notRepo);
            snap.Error.Should().NotBeNullOrEmpty();
            snap.DirtyFiles.Should().BeEmpty();
        }
        finally { try { Directory.Delete(notRepo, recursive: true); } catch { } }
    }

    // ---------- argv Linux-побудки (systemd-run) ----------

    // Контракт ADR-010: argv фиксированный, без shell, параметров заявки нет.
    // systemd-run --user --collect --quiet --unit=ccs-deploy-<id> -p ManagedOOMPreference=avoid
    //   --setenv=CCS_RELEASES_DIR=<ReleasesDir> <AgentDir>/scripts/ops/deploy-linux.sh
    // Тест фиксирует argv поэлементно через подставной launcher: нет полей заявки (ref/sha),
    // есть --unit=ccs-deploy-<id выкатки> (конкретное значение, не TaskName) и
    // --setenv=CCS_RELEASES_DIR=<ReleasesDir> из конфига.
    [Fact]
    public async Task WakeAgent_Linux_ArgvПоКонтракту_БезПолейЗаявки()
    {
        var launcher = new RecordingLauncher();
        var host = new DeployHost(
            new GitService(new LocalOnlyFactory()),
            new RecordingFactory(launcher),
            new NoopDeployAgentLock(),
            NullLogger<DeployHost>.Instance);

        var options = new DeployOptions
        {
            IsLinux = true,
            AgentDir = "/opt/ccs/agent",
            ReleasesDir = "/opt/ccs/releases",
            TaskName = "CCS-Deploy",
        };

        // id выкатки — UTC-штамп (формат NewDeployId), прошёл IsValidBuildId
        const string deployId = "20261012-123456";

        var error = await host.WakeAgentAsync(options, deployId);

        error.Should().BeNull("тривиальный процесс завершился с кодом 0 → побудка успешна");
        launcher.Started.Should().HaveCount(1, "один запуск systemd-run");
        var spec = launcher.Started[0];

        spec.FileName.Should().Be("systemd-run");
        spec.Args.Should().Equal(
            "--user",
            "--collect",
            "--quiet",
            $"--unit=ccs-deploy-{deployId}",
            "-p",
            "ManagedOOMPreference=avoid",
            $"--setenv=CCS_RELEASES_DIR={options.ReleasesDir}",
            Path.Combine(options.AgentDir, "scripts", "ops", "deploy-linux.sh"));
    }

    // id выкатки уходит в имя systemd-юнита — недопустимый (не прошёл IsValidBuildId) обязан
    // дать отказ ДО запуска, а не уехать в имя юнита. Мутация: убери guard IsValidBuildId —
    // launcher.Started станет 1 и тест краснеет.
    [Fact]
    public async Task WakeAgent_Linux_НедопустимыйId_ОтказДоЗапуска()
    {
        var launcher = new RecordingLauncher();
        var host = new DeployHost(
            new GitService(new LocalOnlyFactory()),
            new RecordingFactory(launcher),
            new NoopDeployAgentLock(),
            NullLogger<DeployHost>.Instance);

        var options = new DeployOptions
        {
            IsLinux = true,
            AgentDir = "/opt/ccs/agent",
            ReleasesDir = "/opt/ccs/releases",
        };

        // пробел не входит в белый список IsValidBuildId
        var error = await host.WakeAgentAsync(options, "2026 1012");

        error.Should().NotBeNullOrEmpty("недопустимый id → отказ до запуска");
        launcher.Started.Should().BeEmpty("процесс не запущен: id не прошёл IsValidBuildId");
    }

    // ---------- Лок «агент жив» ----------

    // Второй TryAcquireDeploy на том же файле → null (агент жив); после Dispose — снова удаётся.
    // Платформонезависимый: на Linux — файловый лок (flock), на Windows — именованный мьютекс.
    // Мутация: убери FileShare.None (→ FileShare.ReadWrite = LOCK_SH) — два общих лока
    // сосуществуют, второй acquire succeeds, тест краснеет.
    [Fact]
    public void Lock_ВторойAcquireNull_ПослеDisposeСноваУдаётся()
    {
        var releasesDir = Path.Combine(Path.GetTempPath(), "deploy_lock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releasesDir);
        try
        {
            var adapter = new DeployAgentLockAdapter();

            var first = adapter.TryAcquireDeploy(releasesDir);
            first.Should().NotBeNull("первый acquire должен занять лок");

            var second = adapter.TryAcquireDeploy(releasesDir);
            second.Should().BeNull("второй acquire на том же файле → null (агент жив)");

            first!.Dispose();

            var third = adapter.TryAcquireDeploy(releasesDir);
            third.Should().NotBeNull("после Dispose лок снова доступен");
            third!.Dispose();
        }
        finally
        {
            try { Directory.Delete(releasesDir, recursive: true); } catch { }
        }
    }

    // Только Linux: пока bash держит flock -x на deploy.lock, .NET-лок (FileShare.None →
    // flock LOCK_EX) не возьмётся и вернёт null. На Windows flock нет — честный skip.
    //
    // Ожидание события, а не времени: bash после взятия flock печатает маркер «locked» в
    // stdout; тест читает stdout до маркера (ReadLineAsync с таймаутом), а не спит фиксированную
    // паузу. sleep 30 — с запасом, чтобы лок держался на весь прогон (убиваем в finally).
    [Fact]
    public async Task Lock_FlockДержитBash_DotNetЛокNull()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "flock — только Linux");

        var releasesDir = Path.Combine(Path.GetTempPath(), "deploy_flock_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(releasesDir);
        var lockPath = Path.Combine(releasesDir, "deploy.lock");

        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"flock -x {lockPath} -c 'echo locked; sleep 30'");
        var bash = Process.Start(psi)!;

        try
        {
            // Маркер «locked» в stdout = доказательство, что flock уже взят. ReadLineAsync
            // с таймаутом — ожидание события: маркер либо придёт, либо тест падает по таймауту.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var line = await bash.StandardOutput.ReadLineAsync(cts.Token);
            line.Should().Be("locked", "bash напечатал маркер после взятия flock");

            // flock у bash → .NET-лок (FileShare.None → flock LOCK_EX) не возьмётся
            var adapter = new DeployAgentLockAdapter();
            var lease = adapter.TryAcquireDeploy(releasesDir);
            lease.Should().BeNull("bash держит flock -x → .NET-лок (FileShare.None) не возьмётся");
        }
        finally
        {
            try { bash.Kill(entireProcessTree: true); bash.WaitForExit(); } catch { }
            try { Directory.Delete(releasesDir, recursive: true); } catch { }
        }
    }
}

/// <summary>Тестовая заглушка: лок всегда свободен (null = не занят).</summary>
internal sealed class NoopDeployAgentLock : IDeployAgentLock
{
    public IDisposable? TryAcquireDeploy(string releasesDir) => null;
}
