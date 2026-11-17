using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Plugins;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// И1 (ADR-041, KM-B3b): слой CLI хода ребёнка — встроенные инструменты (п.11), запросы
// разрешений (п.12), плагины и CLAUDE.md (п.13). CLI 2.1.325: «--tools ""» оставляет у хода
// ноль встроенных (проверено живым запуском, init.tools = []), поэтому замок — пустой
// разрешающий список, а не список запретов.
[Collection(TestCollections.ProcessGlobalState)]
public class ChildCliLayerTests : IDisposable
{
    private readonly List<Process> _processes = [];
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            ProcessTestCleanup.SafeDispose(p);
        }
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* уже удалён */ }
    }

    private static ChildPolicySnapshot Child() =>
        new("kid", new ChildProfile { Enabled = true, ProviderKey = "kids-api", Model = "sonnet" }, false);

    // --- п.12: запросы разрешений ---

    private static readonly MethodInfo Decide =
        typeof(ClaudeSession).GetMethod("DecidePermissionAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewSession(
        Session info, Func<ChildPolicySnapshot?>? childPolicy)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null, ChildPolicyProvider: childPolicy);
        return (new ClaudeSession(info, context), sent);
    }

    private static Task<string> DecideAsync(ClaudeSession session, string toolName)
    {
        using var doc = JsonDocument.Parse("{}");
        return (Task<string>)Decide.Invoke(session, ["req-1", toolName, doc.RootElement.Clone(), new object()])!;
    }

    // Каждая из веток, что у взрослого отвечает allow без карточки, у ребёнка обязана дать deny:
    // исполнитель задачи, «всегда разрешать», персона в AcceptEdits, свой MCP-сервер, план хода.
    [Theory]
    [InlineData("Bash", true, false)]                              // исполнитель задачи
    [InlineData("Bash", false, true)]                              // «всегда разрешать»
    [InlineData("mcp__tasks__tasks_list", false, false)]           // свой MCP, вне детского allow-list
    [InlineData("mcp__websearch__web_search", false, false)]       // веб-поиск выключен для ребёнка
    [InlineData("TaskCreate", false, false)]                       // план хода — не детский инструмент
    [InlineData("Write", false, false)]                            // обычный запрос → без карточки, deny
    public async Task ХодРебёнка_ЗапросВнеAllowList_ОтказБезКарточки(string tool, bool taskExecution, bool autoAllow)
    {
        var info = new Session { TaskExecution = taskExecution, AutoAllowTools = autoAllow ? [tool] : [], PersonaId = "p1" };
        var (session, sent) = NewSession(info, Child);
        await using var _ = session;

        var decision = await DecideAsync(session, tool).WaitAsync(TimeSpan.FromSeconds(10));

        decision.Should().Be("deny");
        lock (sent) sent.OfType<PermissionRequestMessage>().Should().BeEmpty("карточки у ребёнка нет");
    }

    [Fact]
    public async Task Контроль_ВзрослыйХод_ТеЖеВетки_Разрешают()
    {
        var info = new Session { TaskExecution = true };
        var (session, _) = NewSession(info, () => null);
        await using var _ = session;

        (await DecideAsync(session, "Bash")).Should().Be("allow");
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("mcp__")]
    [InlineData("mcp__server")]
    [InlineData("mcp____tool")]
    [InlineData("mcp__server__")]
    [InlineData("")]
    public void ChildPermissionDecision_НераспознанноеИмя_Deny(string tool) =>
        ClaudeSession.ChildPermissionDecision(tool).Should().Be("deny");

    // --- п.11: встроенные инструменты ---

    [Fact]
    public void ForceNoBuiltinTools_СнимаетПрежнийСписокИСтавитПустой()
    {
        List<string> args = ["--model", "sonnet", "--tools", "Bash Edit Read", "--print"];

        ClaudeSession.ForceNoBuiltinTools(args);

        args.Should().Equal("--model", "sonnet", "--print", "--tools", "");
    }

    // --- п.11 и п.13 целиком: собранный ProcessSpec ---

    private sealed class SpecCapturingLauncher(List<Process> processes, TaskCompletionSource started) : IProcessLauncher
    {
        public ProcessSpec? Captured { get; private set; }
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            Captured = spec;
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows() ? ["/c", "ping -n 120 127.0.0.1 >nul"] : ["-c", "exec sleep 120"],
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

        public int EstimateCommandLineLength(ProcessSpec spec) =>
            (spec.FileName ?? "").Length + spec.Args.Sum(TurnPromptAssembler.ArgCost);

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
        }
    }

    private sealed class FixedPlugins : IPluginDirSource
    {
        public IReadOnlyList<string> GetPluginDirs(string ownerId, string? personaId, Project? project) =>
            [Path.Combine(Path.GetTempPath(), "ccs-plugin-x")];
    }

    private async Task<ProcessSpec> CaptureSpecAsync(Func<ChildPolicySnapshot?>? childPolicy)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-child-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        var launcher = new SpecCapturingLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m => { if (m is ErrorMessage) failed.TrySetResult(); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null,
            Launcher: launcher, ContentRootPath: root, OwnerId: "kid",
            PluginDirSource: new FixedPlugins(), ChildPolicyProvider: childPolicy);
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var pool = new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"{ClaudeSubscriptionPool.Section}:kids-api:ApiKey"] = "sk-ant-kids" }).Build());
        var session = new ClaudeSession(new Session { Model = "sonnet", Provider = "claude" }, context,
            providers: providers, subscriptionPool: pool);

        await session.SendMessageAsync("тестовый ход");
        await Task.WhenAny(started.Task, failed.Task).WaitAsync(TimeSpan.FromSeconds(30));
        launcher.Captured.Should().NotBeNull("процесс CLI должен был стартовать");
        return launcher.Captured!;
    }

    [Fact]
    public async Task ХодРебёнка_ПустойСписокВстроенных_БезПлагинов_БезCLAUDEmd()
    {
        var spec = await CaptureSpecAsync(() => Child());

        using var scope = new AssertionScope();
        var args = spec.Args.ToList();
        var i = args.IndexOf("--tools");
        i.Should().BeGreaterThanOrEqualTo(0);
        (i >= 0 && i + 1 < args.Count ? args[i + 1] : "<нет>").Should().BeEmpty(
            "пустой разрешающий список: ни одного встроенного инструмента");
        args.Count(a => a == "--tools").Should().Be(1);
        spec.Args.Should().NotContain("--plugin-dir", "плагины владельца ребёнку не отдаются");
        spec.Env!.GetValueOrDefault("CLAUDE_CODE_DISABLE_CLAUDE_MDS").Should().Be("1", "CLAUDE.md проекта ребёнку не грузится");
    }

    [Fact]
    public async Task ВзрослыйХод_ВстроенныеИПлагиныНаМесте()
    {
        var spec = await CaptureSpecAsync(() => null);

        spec.Args.Should().NotContain("--tools");
        spec.Args.Should().Contain("--plugin-dir");
        spec.Env.Should().NotContainKey("CLAUDE_CODE_DISABLE_CLAUDE_MDS");
    }
}
