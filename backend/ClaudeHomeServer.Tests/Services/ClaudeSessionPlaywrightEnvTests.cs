using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Env процесса CLI для MCP-сервера плагина playwright: headless, изолированный профиль и
// папка вывода в .cc-attachments/. Без них браузер агента падает на сервере без RDP-входа
// (нет DISPLAY), а два параллельных хода делят один профиль. Проверяем сквозным запуском
// хода: env ловит лаунчер-заглушка.
public class ClaudeSessionPlaywrightEnvTests : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* уже удалён/занят */ }
        }
    }

    private sealed class EnvCapturingLauncher(List<Process> processes, TaskCompletionSource started, bool sandboxed)
        : IProcessLauncher
    {
        public IReadOnlyDictionary<string, string>? CapturedEnv { get; private set; }
        public bool IsSandboxed => sandboxed;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            CapturedEnv = spec.Env?.ToDictionary(kv => kv.Key, kv => kv.Value);
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            });
            lock (processes) processes.Add(process);
            started.TrySetResult();
            return process;
        }

        public int EstimateCommandLineLength(ProcessSpec spec)
        {
            var total = (spec.FileName ?? string.Empty).Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    private async Task<(IReadOnlyDictionary<string, string> Env, string Root)> RunTurnAsync(
        bool browserEnabled, bool sandboxed = false)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-pw-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var launcher = new EnvCapturingLauncher(_processes, started, sandboxed);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            WidgetsMcp: null,
            PersonaAgentsProvider: null,
            HttpMcpActive: false,
            HttpMcpEnabledProvider: null,
            Launcher: launcher,
            BrowserEnabled: browserEnabled);
        var session = new ClaudeSession(new Session(), context);

        await session.SendMessageAsync("тестовый ход");
        var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(started.Task, "процесс хода обязан стартовать");
        launcher.CapturedEnv.Should().NotBeNull();
        return (launcher.CapturedEnv!, root);
    }

    [Fact]
    public async Task БраузерВключён_EnvСодержитHeadlessIsolatedИПапкуВывода()
    {
        var (env, root) = await RunTurnAsync(browserEnabled: true);

        env.Should().Contain("PLAYWRIGHT_MCP_HEADLESS", "true");
        env.Should().Contain("PLAYWRIGHT_MCP_ISOLATED", "true");
        env.Should().Contain("PLAYWRIGHT_MCP_OUTPUT_DIR",
            Path.Combine(root, ".cc-attachments", "playwright"));
    }

    [Fact]
    public async Task БраузерВыключен_ПеременныхPlaywrightНет()
    {
        var (env, _) = await RunTurnAsync(browserEnabled: false);

        env.Keys.Should().NotContain(k => k.StartsWith("PLAYWRIGHT_MCP_"));
    }

    [Fact]
    public async Task Песочница_ПеременныхPlaywrightНет()
    {
        var (env, _) = await RunTurnAsync(browserEnabled: true, sandboxed: true);

        env.Keys.Should().NotContain(k => k.StartsWith("PLAYWRIGHT_MCP_"));
    }
}
