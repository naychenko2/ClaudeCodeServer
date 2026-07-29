using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm.Claude;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Признак «ход идёт в чужом дереве»: агент вызвал встроенный EnterWorktree в обход
// тумблера чата (Session.WorktreePath), SessionManager.EffectiveRoot об этом не знает.
// ResolveTurnWorktree сверяет фактический cwd из system/init с корнем, который сервер
// сам передал в WorkingDirectory (тот УЖЕ учитывает штатное дерево чата).
public class ClaudeSessionWorktreeTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ccs-worktree-tests", "project");

    [Fact]
    public void Cwd_СовпалСОжидаемымКорнем_ВозвращаетNull()
    {
        var result = ClaudeSession.ResolveTurnWorktree(Root, Root, LocalProcessRunner.Instance);
        Assert.Null(result);
    }

    [Fact]
    public void Cwd_Null_ВозвращаетNull()
    {
        Assert.Null(ClaudeSession.ResolveTurnWorktree(null, Root, LocalProcessRunner.Instance));
    }

    // Признак косметический, но вызывается на каждом system/init — сбой нормализации пути
    // (Path.GetFullPath на '\0' кидает ArgumentException независимо от ОС) не должен ронять
    // ход: без внутреннего try/catch исключение ушло бы в общий catch цикла чтения прогона
    // ДО отправки SessionStartedMessage, обрывая ход целиком
    [Fact]
    public void Cwd_НормализацияБросаетИсключение_ВозвращаетNullБезИсключения()
    {
        Assert.Throws<ArgumentException>(() => Path.GetFullPath("\0"));

        var result = ClaudeSession.ResolveTurnWorktree("\0", Root, LocalProcessRunner.Instance);

        Assert.Null(result);
    }

    // Регистр и разделители пути не должны давать ложное срабатывание на Windows
    [Fact]
    public void Cwd_РегистрИРазделителиОтличаются_ВозвращаетNull()
    {
        var altCwd = Root.ToUpperInvariant().Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = ClaudeSession.ResolveTurnWorktree(altCwd, Root, LocalProcessRunner.Instance);
        Assert.Null(result);
    }

    [Fact]
    public void Cwd_ПодпапкаClaudeWorktrees_ЗаполняетПризнак()
    {
        var wtPath = Path.Combine(Root, ".claude", "worktrees", "feature-x");
        var result = ClaudeSession.ResolveTurnWorktree(wtPath, Root, LocalProcessRunner.Instance);
        Assert.NotNull(result);
        Assert.Equal(wtPath, result!.Path);
        Assert.Equal("feature-x", result.Name);
    }

    // Session.WorktreePath чата подставляется в rootPath (SessionManager.EffectiveRoot)
    // ДО старта процесса — совпадение cwd с ним значит штатное дерево чата, не чужое
    [Fact]
    public void Cwd_РавенШтатномуДеревуЧата_ВозвращаетNull()
    {
        var chatWorktree = Path.Combine(Path.GetTempPath(), "ccs-worktree-tests", "chat-worktree");
        var result = ClaudeSession.ResolveTurnWorktree(chatWorktree, chatWorktree, LocalProcessRunner.Instance);
        Assert.Null(result);
    }

    // В песочнице WorkingDirectory переводится в контейнерный путь при старте процесса
    // (DockerProcessRunner) — сверка обязана переводить rootPath тем же мапером, иначе
    // ЛЮБОЙ ход в контейнере ложно считался бы «чужим деревом» (cwd там в другом
    // пространстве путей, чем хостовый rootPath)
    [Fact]
    public void Sandboxed_CwdРавенПереведённомуКорню_ВозвращаетNull()
    {
        var launcher = new FakeSandboxedLauncher(Root, "/projects/demo");
        var result = ClaudeSession.ResolveTurnWorktree("/projects/demo", Root, launcher);
        Assert.Null(result);
    }

    [Fact]
    public void Sandboxed_CwdПодпапкаПереведённогоКорня_ЗаполняетПризнак()
    {
        var launcher = new FakeSandboxedLauncher(Root, "/projects/demo");
        var result = ClaudeSession.ResolveTurnWorktree("/projects/demo/.claude/worktrees/feature-x", Root, launcher);
        Assert.NotNull(result);
        Assert.Equal("feature-x", result!.Name);
    }

    // Непереводимый rootPath (вне известных монтирований песочницы) не должен ронять ход —
    // сравниваем как есть, деградация, а не исключение
    [Fact]
    public void Sandboxed_НепереводимыйКорень_НеБросаетИсключение()
    {
        var launcher = new FakeSandboxedLauncher("/другой/путь", "/projects/demo");
        var result = ClaudeSession.ResolveTurnWorktree("/projects/demo", Root, launcher);
        Assert.NotNull(result);
    }

    private sealed class FakeSandboxedLauncher(string hostRoot, string runtimeRoot) : IProcessLauncher
    {
        public bool IsSandboxed => true;
        public bool TargetIsWindows => false;
        public IPathMapper Paths { get; } = new FakePathMapper(hostRoot, runtimeRoot);
        public string ClaudeCliCommand => "claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;
        public System.Diagnostics.Process Start(ProcessSpec spec) => throw new NotSupportedException();
        public void Kill(System.Diagnostics.Process process, string? turnId = null) => throw new NotSupportedException();
    }

    private sealed class FakePathMapper(string hostRoot, string runtimeRoot) : IPathMapper
    {
        public string ToRuntime(string hostPath) =>
            string.Equals(hostPath, hostRoot, StringComparison.OrdinalIgnoreCase)
                ? runtimeRoot
                : throw new InvalidOperationException($"Путь недоступен в песочнице: {hostPath}");

        public string ToHost(string runtimePath) => throw new NotSupportedException();
    }
}
