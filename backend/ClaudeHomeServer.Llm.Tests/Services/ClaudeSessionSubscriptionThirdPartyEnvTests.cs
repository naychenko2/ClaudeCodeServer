using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// SP1 (находка задачи c47fe6da): учётка подписки пула накладывается ТОЛЬКО на родной ход.
// Инвариант CLAUDE.md: «профили CLI изолируют OAuth-логин ~/.claude — иначе CLI пошлёт чужому
// эндпоинту токен подписки (401)». Раньше ветка подписки в сборке env пропускала случай, когда
// Info.Provider — ключ Claude («claude-2»), а модель резолвится в стороннего провайдера:
// CLAUDE_CONFIG_DIR/CLAUDE_CODE_OAUTH_TOKEN/ANTHROPIC_AUTH_TOKEN подписки ложились поверх env
// провайдера и доезжали до чужого эндпоинта. Теперь признак — ResolveByModel(EffectiveModel)
// is null (родной ход); при стороннем провайдере подписочных переменных в env нет, а его
// собственные значения (ключ, профиль, базовый URL) остаются нетронутыми.
//
// Платформонезависимость (CI — ubuntu): «CLI» — cmd/sh-сон без вывода, как в
// ClaudeSessionProxyBypassEnvTests. Процесс-глобальное состояние не трогаем (NO_PROXY не
// ни выставляем, ни утверждаем) — коллекция ProcessGlobalState не нужна.
public class ClaudeSessionSubscriptionThirdPartyEnvTests : IDisposable
{
    private const string SubKey = "claude-2";
    private const string SubOAuth = "oauth-sub-token";
    private const string SubApiKey = "sub-api-key";
    private const string ExternalProviderKey = "minimax";
    private const string ExternalApiKey = "mm-key";
    private const string ExternalBaseUrl = "https://minimax.test/anthropic";
    private const string ExternalModel = "MiniMax-M2.1";
    private const string LocalProviderKey = "localqwen";
    private const string LocalModelId = "qwen3.8-27b";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(),
        "ccs_sub_thirdparty_env_" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _processes = [];

    public ClaudeSessionSubscriptionThirdPartyEnvTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            try { p.Dispose(); } catch (NullReferenceException) { } // Process.Close гонится с уборкой сессии
        }
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // Конфиг: внешний облачный провайдер (MiniMax — из находки), локальный vLLM (qwen) и
    // подписка пула "claude-2" с OAuth-токеном и/или API-ключом.
    private (LlmProviderRegistry Providers, ClaudeSubscriptionPool Pool) BuildConfig(
        bool subOAuth, bool subApiKey)
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(_tempDir, "projects.json"),
            ["LlmProviders:minimax:AnthropicBaseUrl"] = ExternalBaseUrl,
            ["LlmProviders:minimax:ApiKey"] = ExternalApiKey,
            ["LlmProviders:minimax:Models:0:Id"] = ExternalModel,
            ["LlmProviders:localqwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:localqwen:IsLocal"] = "true",
            ["LlmProviders:localqwen:Models:0:Id"] = LocalModelId,
        };
        if (subOAuth) dict[$"{ClaudeSubscriptionPool.Section}:{SubKey}:OAuthToken"] = SubOAuth;
        if (subApiKey) dict[$"{ClaudeSubscriptionPool.Section}:{SubKey}:ApiKey"] = SubApiKey;
        var config = TestConfig.Build(dict);
        return (new LlmProviderRegistry(config), new ClaudeSubscriptionPool(config));
    }

    // «CLI», который стартовал и молчит: захватывает env хода (spec.Env), как в
    // ClaudeSessionProxyBypassEnvTests.
    private sealed class EnvCapturingLauncher(List<Process> processes, TaskCompletionSource started)
        : IProcessLauncher
    {
        public Dictionary<string, string>? CapturedEnv { get; private set; }
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            CapturedEnv = spec.Env is null ? null : new Dictionary<string, string>(spec.Env);

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

    private async Task<Dictionary<string, string>> RunTurnAsync(string model,
        LlmProviderRegistry providers, ClaudeSubscriptionPool pool)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(_tempDir, "root");
        Directory.CreateDirectory(root);

        var launcher = new EnvCapturingLauncher(_processes, started);
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
            Launcher: launcher);
        var session = new ClaudeSession(new Session { Model = model, Provider = SubKey }, context,
            providers: providers, subscriptionPool: pool);

        await session.SendMessageAsync("тестовый ход");
        var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(started.Task, "процесс хода обязан стартовать");
        launcher.CapturedEnv.Should().NotBeNull("env хода собирается до запуска процесса");

        // Убираем за собой: глушим «CLI» и дожидаемся терминала хода
        Process cli;
        lock (_processes) cli = _processes[^1];
        cli.Kill();
        await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        return launcher.CapturedEnv!;
    }

    /// <summary>
    /// Сторонний провайдер при подписочном ключе сессии: ни OAuth-токена подписки, ни её
    /// профиля в env нет — остаются собственные значения провайдера (ключ, профиль, URL).
    /// Мутация (снятие условия ResolveByModel(EffectiveModel) is null) краснит этот тест.
    /// </summary>
    [Theory]
    [InlineData(ExternalModel, ExternalProviderKey, ExternalBaseUrl)] // MiniMax — случай из находки
    [InlineData(LocalModelId, LocalProviderKey, "http://127.0.0.1:18020")] // локальный провайдер — тот же инвариант
    public async Task СтороннийПровайдер_УчёткаПодпискиВEnvНеПопадает(
        string model, string providerKey, string baseUrl)
    {
        var (providers, pool) = BuildConfig(subOAuth: true, subApiKey: false);

        var env = await RunTurnAsync(model, providers, pool);

        env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN").Should().BeFalse(
            "токен подписки чужому эндпоинту не едет (инвариант CLAUDE.md)");
        var configDir = env["CLAUDE_CONFIG_DIR"];
        configDir.Should().EndWith(Path.Combine("claude-profiles", providerKey),
            "профиль провайдера, а не профиль подписки");
        configDir.Should().NotContain("sub-" + SubKey);
        env["ANTHROPIC_BASE_URL"].Should().Be(baseUrl);
        env["ANTHROPIC_AUTH_TOKEN"].Should().NotBe(SubApiKey, "ключ провайдера остаётся своим");
        env["ANTHROPIC_API_KEY"].Should().NotBe(SubApiKey);
    }

    /// <summary>
    /// Тот же чужой эндпоинт, но подписка с API-ключом: BuildOAuthCliEnv поставил бы
    /// ANTHROPIC_AUTH_TOKEN/ANTHROPIC_API_KEY подписки поверх ключа провайдера — их быть не должно.
    /// </summary>
    [Fact]
    public async Task СтороннийПровайдер_ApiKeyПодпискиНеПеретираетКлючПровайдера()
    {
        var (providers, pool) = BuildConfig(subOAuth: false, subApiKey: true);

        var env = await RunTurnAsync(ExternalModel, providers, pool);

        env["ANTHROPIC_AUTH_TOKEN"].Should().Be(ExternalApiKey,
            "ключ провайдера не перетирается подписочным");
        env["ANTHROPIC_API_KEY"].Should().Be(ExternalApiKey);
        env["CLAUDE_CONFIG_DIR"].Should().EndWith(Path.Combine("claude-profiles", ExternalProviderKey));
        env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN").Should().BeFalse();
    }

    /// <summary>
    /// Регрессия родного пути: ResolveByModel == null (алиас семейства) — учётка подписки на
    /// месте, как раньше: свой профиль sub-{key} и CLAUDE_CODE_OAUTH_TOKEN.
    /// </summary>
    [Fact]
    public async Task РоднойХод_УчёткаПодпискиНаМесте()
    {
        var (providers, pool) = BuildConfig(subOAuth: true, subApiKey: false);

        var env = await RunTurnAsync("sonnet", providers, pool);

        env["CLAUDE_CODE_OAUTH_TOKEN"].Should().Be(SubOAuth);
        env["CLAUDE_CONFIG_DIR"].Should().EndWith(Path.Combine("claude-profiles", "sub-" + SubKey),
            "родной ход на подписке — её изолированный профиль");
        env.ContainsKey("ANTHROPIC_BASE_URL").Should().BeFalse("родной эндпоинт не объявляется");
    }
}
