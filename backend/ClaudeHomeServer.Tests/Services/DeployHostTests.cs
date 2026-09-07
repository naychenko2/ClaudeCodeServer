using System.Diagnostics;
using ClaudeHomeServer.Services.Deploy;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Git;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.Tests.Services;

// Интеграционная проверка DeployHost.GitSnapshotAsync на реальном git CLI.
// Цель — закрыть регрессию «git status упал → DeployHost молча вернул чистое дерево»:
// новая GitRepoSnapshot обязана пробрасывать Error в DeployGitSnapshot, иначе DeployService
// пропустит выкатку с непрочитанным состоянием.
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
        _host = new DeployHost(git, new LocalOnlyFactory(), NullLogger<DeployHost>.Instance);
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
}
