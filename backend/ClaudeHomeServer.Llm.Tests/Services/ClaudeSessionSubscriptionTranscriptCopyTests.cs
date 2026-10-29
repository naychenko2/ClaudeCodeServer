using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Доработка ревью ccb3d43c к SP1 (пункт 1): класс чатов «Provider = ключ пула + модель стороннего
// провайдера». До правки SP1 подписочный env перетирал провайдерский CLAUDE_CONFIG_DIR, и транскрипт
// таких чатов писался в профиль подписки sub-{key}; после — ход ищет транскрипт в профиле провайдера,
// --resume его там не находит и ход отказывает (TurnFailureText.ResumeTranscriptMissing). Теперь ход
// сам копирует транскрипт из собственного профиля сессии (context.CliConfigRoot — его SessionManager
// разрешил по Info.Provider) в профиль, куда смотрит CLI этого хода (TryMigrate — копия, исходник не
// трогается). «CLI» — молчаливый сон cmd/sh, как в ClaudeSessionSubscriptionThirdPartyEnvTests:
// проверка — состояние диска до старта процесса, а не env и не вывод CLI.
public class ClaudeSessionSubscriptionTranscriptCopyTests : IDisposable
{
    private const string SubKey = "claude-2";
    private const string SubOAuth = "oauth-sub-token";
    private const string ExternalProviderKey = "minimax";
    private const string ExternalApiKey = "mm-key";
    private const string ExternalBaseUrl = "https://minimax.test/anthropic";
    private const string ExternalModel = "MiniMax-M2.1";
    private const string ClaudeSessionId = "submodel-csid-0001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(),
        "ccs_sub_transcript_copy_" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudeSessionSubscriptionTranscriptCopyTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Конфиг как в находке: облачный провайдер MiniMax + подписка пула "claude-2" с OAuth-токеном.
    private (LlmProviderRegistry Providers, ClaudeSubscriptionPool Pool) BuildConfig()
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["LlmProviders:minimax:AnthropicBaseUrl"] = ExternalBaseUrl,
            ["LlmProviders:minimax:ApiKey"] = ExternalApiKey,
            ["LlmProviders:minimax:Models:0:Id"] = ExternalModel,
            [$"{ClaudeSubscriptionPool.Section}:{SubKey}:OAuthToken"] = SubOAuth,
        };
        var config = TestConfig.Build(dict);
        return (new LlmProviderRegistry(config), new ClaudeSubscriptionPool(config));
    }

    // «CLI», который просто стартовал и молчит: нужен момент старта — копия обязана случиться ДО него.
    private sealed class SilentCliLauncher(List<Process> processes, TaskCompletionSource started)
        : IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "exec sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
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
            try { process.Kill(entireProcessTree: true); }
            catch { /* уже мёртв */ }
        }
    }

    // Ход resumed-чата класса «ключ пула + сторонняя модель»: Provider — ключ пула, модель —
    // сторонняя, ClaudeSessionId есть, CliConfigRoot — профиль подписки (как его передаёт
    // SessionManager.ConfigRootFor по Info.Provider).
    private async Task RunResumedTurnAsync(LlmProviderRegistry providers, ClaudeSubscriptionPool pool,
        string root)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Directory.CreateDirectory(root);

        var launcher = new SilentCliLauncher(_processes, started);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: m =>
            {
                if (m is ExitedMessage) exited.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: launcher,
            CliConfigRoot: providers.GetProfileDir("sub-" + SubKey));
        var session = new ClaudeSession(
            new Session { Model = ExternalModel, Provider = SubKey, ClaudeSessionId = ClaudeSessionId },
            context, providers: providers, subscriptionPool: pool);

        await session.SendMessageAsync("тестовый ход");
        var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(started.Task, "процесс хода обязан стартовать");

        Process cli;
        lock (_processes) cli = _processes[^1];
        cli.Kill();
        await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Пункт 1: транскрипт, оставшийся только в профиле подписки, копируется в профиль провайдера,
    /// куда смотрит --resume этого хода; исходник не трогается. Мутация (снятие вызова
    /// CopyTranscriptToTurnRoot в RunTurnAsync) краснит этот тест.
    /// </summary>
    [Fact]
    public async Task КлючПодпискиИСторонняяМодель_ТранскриптКопируетсяВПрофильПровайдера()
    {
        var (providers, pool) = BuildConfig();
        var root = Path.Combine(_tempDir, "root");

        var srcDir = Path.Combine(providers.GetProfileDir("sub-" + SubKey), "projects",
            TranscriptMigrator.FlattenCwd(root));
        Directory.CreateDirectory(srcDir);
        const string body = "{\"type\":\"user\"}\n";
        await File.WriteAllTextAsync(Path.Combine(srcDir, ClaudeSessionId + ".jsonl"), body);

        await RunResumedTurnAsync(providers, pool, root);

        var dstFile = Path.Combine(providers.GetProfileDir(ExternalProviderKey), "projects",
            TranscriptMigrator.FlattenCwd(root), ClaudeSessionId + ".jsonl");
        File.Exists(dstFile).Should().BeTrue(
            "транскрипт копируется туда, где --resume этого хода его ищет — в профиль провайдера");
        File.ReadAllText(dstFile).Should().Be(body);
        File.Exists(Path.Combine(srcDir, ClaudeSessionId + ".jsonl")).Should().BeTrue(
            "исходник не трогается: это копия, а не перенос");
    }

    /// <summary>
    /// Регрессия идемпотентности: чат, созданный после правки (транскрипт уже в профиле провайдера),
    /// не порождает обратной копии в профиль подписки — копия идёт только в сторону корня хода.
    /// </summary>
    [Fact]
    public async Task ТранскриптУжеВПрофилеПровайдера_ОбратнойКопииНеПоявляется()
    {
        var (providers, pool) = BuildConfig();
        var root = Path.Combine(_tempDir, "root2");

        var dstDir = Path.Combine(providers.GetProfileDir(ExternalProviderKey), "projects",
            TranscriptMigrator.FlattenCwd(root));
        Directory.CreateDirectory(dstDir);
        await File.WriteAllTextAsync(Path.Combine(dstDir, ClaudeSessionId + ".jsonl"), "{}\n");

        await RunResumedTurnAsync(providers, pool, root);

        var subFile = Path.Combine(providers.GetProfileDir("sub-" + SubKey), "projects",
            TranscriptMigrator.FlattenCwd(root), ClaudeSessionId + ".jsonl");
        File.Exists(subFile).Should().BeFalse(
            "обратной копии нет: приёмник уже содержит файл, копия односторонняя");
    }
}
