using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.ChildMode;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

// И2 (ADR-041): ход ребёнка идёт только по API-ключу записи пула, без подписки и без фолбэка.
// Процесс CLI подменён сном — проверяем собранный ProcessSpec (env и ClearEnv) и отказ запуска.
[Collection(TestCollections.ProcessGlobalState)]
public class ChildApiKeyRouteTests : IDisposable
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

    private static ChildPolicySnapshot Child(string providerKey = "kids-api") =>
        new("kid", new ChildProfile { Enabled = true, ProviderKey = providerKey, Model = "sonnet" }, false);

    private static ClaudeSubscriptionPool Pool(params (string Key, string? OAuth, string? ApiKey)[] subs)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (key, oauth, api) in subs)
        {
            if (oauth is not null) d[$"{ClaudeSubscriptionPool.Section}:{key}:OAuthToken"] = oauth;
            if (api is not null) d[$"{ClaudeSubscriptionPool.Section}:{key}:ApiKey"] = api;
        }
        return new ClaudeSubscriptionPool(new ConfigurationBuilder().AddInMemoryCollection(d).Build());
    }

    // Возвращает (spec | null, тексты ErrorMessage)
    private async Task<(ProcessSpec? Spec, List<string> Errors)> RunAsync(
        Func<ChildPolicySnapshot?>? childPolicy, ClaudeSubscriptionPool pool)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new List<string>();
        var root = Path.Combine(Path.GetTempPath(), "ccs-child-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        var launcher = new SpecCapturingLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m =>
            {
                if (m is ErrorMessage e) { lock (errors) errors.Add(e.Text); failed.TrySetResult(); }
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null, TasksMcp: null,
            Launcher: launcher, ContentRootPath: root, OwnerId: "kid",
            ChildPolicyProvider: childPolicy);
        var providers = new LlmProviderRegistry(new ConfigurationBuilder().AddInMemoryCollection().Build());
        var session = new ClaudeSession(new Session { Model = "sonnet", Provider = "claude" }, context,
            providers: providers, subscriptionPool: pool);

        await session.SendMessageAsync("тестовый ход");
        await Task.WhenAny(started.Task, failed.Task).WaitAsync(TimeSpan.FromSeconds(30));
        return (launcher.Captured, errors);
    }

    [Fact]
    public async Task ХодРебёнка_ИдётПоКлючуПула_БезТокенаПодписки()
    {
        var pool = Pool(("kids-api", null, "sk-ant-kids"), ("claude", "oauth-parent", null));
        var (spec, _) = await RunAsync(() => Child(), pool);

        spec.Should().NotBeNull();
        spec!.Env!["ANTHROPIC_API_KEY"].Should().Be("sk-ant-kids");
        spec.Env.Should().NotContainKey("CLAUDE_CODE_OAUTH_TOKEN");
        spec.Env[ChildTurnMarker.EnvKey].Should().Be("1");
        spec.ClearEnv.Should().Contain("CLAUDE_CODE_OAUTH_TOKEN",
            "унаследованный токен подписки бэкенда обязан быть вычищен");
    }

    [Theory]
    [InlineData("oauth-only")]   // запись без ApiKey
    [InlineData("both")]         // ApiKey вместе с OAuthToken — подписка
    [InlineData("missing")]      // записи нет
    public async Task ХодРебёнка_ЗаписьНеГодится_ОтказЗапускаБезОткатаНаПодписку(string case_)
    {
        var pool = Pool(("oauth-only", "oauth-x", null), ("both", "oauth-x", "sk-ant-x"));
        var (spec, errors) = await RunAsync(() => Child(case_), pool);

        spec.Should().BeNull("процесс CLI не стартует");
        errors.Should().ContainSingle().Which.Should().Be(ChildApiKeyRoute.RefusedText);
    }

    [Fact]
    public async Task ХодРебёнка_ПолитикаDenyAll_Отказ()
    {
        var (spec, errors) = await RunAsync(() => new ChildPolicySnapshot("kid", null, true),
            Pool(("kids-api", null, "sk-ant-kids")));

        spec.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Be(ChildApiKeyRoute.RefusedText);
    }

    [Fact]
    public async Task ОбычныйХод_ПолитикаNull_МеткиРебёнкаНет()
    {
        var (spec, _) = await RunAsync(() => null, Pool(("kids-api", null, "sk-ant-kids")));

        spec.Should().NotBeNull();
        spec!.Env.Should().NotContainKey(ChildTurnMarker.EnvKey);
        spec.ClearEnv.Should().NotContain("CLAUDE_CODE_OAUTH_TOKEN");
    }
}
