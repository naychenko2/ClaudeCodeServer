using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сквозной сторож правки ClaudeSession.cs:2376 — флаг --effort должен ставиться и при
// пустом Session.Effort, если у провайдера модели есть SupportedEfforts. До правки условие
// `if (!string.IsNullOrWhiteSpace(Info.Effort))` пропускало пустой случай, CLI подставлял
// дефолт «high» → 400 на qwen3.8-27b через vLLM (прод 20260905-014747).
//
// По образцу ClaudeSessionProxyBypassEnvTests: поднимаем ClaudeSession с фейковым
// лаунчером, который вместо CLI запускает cmd/sh-сон и захватывает spec.Args —
// реальный набор флагов, уходящих в процесс хода.
public class ClaudeSessionEffortArgsTests : IDisposable
{
    private const string LocalModelId = "qwen3.8-27b";
    private readonly List<Process> _processes = [];

    // Реестр с двумя провайдерами: локальный vLLM с SupportedEfforts=["low"] и облачный
    // deepseek без SupportedEfforts — на нём же проверяется, что поведение непричастных
    // ходов не изменилось.
    private static LlmProviderRegistry LocalProviders() => new(Helpers.TestConfig.Build(
        new Dictionary<string, string?>
        {
            ["LlmProviders:local-qwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local-qwen:IsLocal"] = "true",
            ["LlmProviders:local-qwen:SupportedEfforts:0"] = "low",
            ["LlmProviders:local-qwen:Models:0:Id"] = LocalModelId,
            ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
            ["LlmProviders:deepseek:ApiKey"] = "sk-test",
            ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
        }));

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
    }

    // «CLI», который стартовал и молчит: захватывает args реального хода, реальную команду
    // подменяет сном. Отдельный лаунчер ради сборки args — ClaudeSessionProxyBypassEnvTests
    // собирает только env и не покрывает нашу регрессию.
    private sealed class ArgsCapturingLauncher(List<Process> processes, TaskCompletionSource started,
        bool sandboxed) : IProcessLauncher
    {
        public IReadOnlyList<string>? CapturedArgs { get; private set; }
        public bool IsSandboxed => sandboxed;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
            CapturedArgs = spec.Args?.ToArray();

            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", "ping -n 120 127.0.0.1 >nul"]
                    : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false, // тестовый процесс: в реестр боевых PID его не пишем
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            lock (processes) processes.Add(process);
            started.TrySetResult();
            return process;
        }

                public int EstimateCommandLineLength(ProcessSpec spec)
        {
            // Заглушка для фейков: тесты, которые гоняют ClaudeSession.ApplyBudget,
            // нуждаются в числовом ответе, но не в точной семантике раннера (её
            // проверяет DockerProcessRunnerCmdlineEstimationTests на реальном раннере).
            // Считаем FileName + args через TurnPromptAssembler.ArgCost — та же формула,
            // что в LocalProcessRunner.EstimateCommandLineLength, без RawArguments.
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

    private async Task<IReadOnlyList<string>?> RunTurnAsync(string model,
        string? effort = null, LlmProviderRegistry? providers = null)
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-effort-args-tests");
        Directory.CreateDirectory(root);

        var launcher = new ArgsCapturingLauncher(_processes, started, sandboxed: false);
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
            WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: false),
            PersonaAgentsProvider: null,
            HttpMcpActive: false,
            HttpMcpEnabledProvider: null,
            Launcher: launcher);
        var session = new ClaudeSession(new Session { Model = model, Effort = effort }, context,
            providers: providers);

        try
        {
            await session.SendMessageAsync("тестовый ход");
            var done = await Task.WhenAny(started.Task, Task.Delay(TimeSpan.FromSeconds(15)));
            done.Should().Be(started.Task, "процесс хода обязан стартовать");
            launcher.CapturedArgs.Should().NotBeNull("args хода собираются до запуска процесса");

            Process cli;
            lock (_processes) cli = _processes[^1];
            cli.Kill();
            await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            return launcher.CapturedArgs;
        }
        finally
        {
            // Info.Effort задан — единственный путь через него в ClaudeSession; на фейке
            // никаких переменных окружения не выставляли, сбрасывать нечего.
        }
    }

    /// <summary>
    /// Блокер боевой секции local-qwen, сквозной: ход на локальной модели при пустом
    /// Session.Effort уносит флаг --effort low (самый лёгкий из SupportedEfforts). До правки
    /// условие пропускало пустой случай, CLI подставлял дефолт «high» → 400 на vLLM.
    /// </summary>
    [Fact]
    public async Task ХодНаЛокальномПровайдере_ПустойEffort_СтавитLow()
    {
        var args = await RunTurnAsync(LocalModelId, effort: null, providers: LocalProviders());

        var arr = args!.ToArray();
        var idx = Array.IndexOf(arr, "--effort");
        idx.Should().BeGreaterOrEqualTo(0, "у локального провайдера с SupportedEfforts флаг должен уйти");
        idx.Should().BeLessThan(arr.Length - 1, "за флагом должно следовать значение");
        arr[idx + 1].Should().Be("low", "единственный поддерживаемый уровень — low");
    }

    /// <summary>
    /// Обратная сторона той же правки: облачный провайдер без SupportedEfforts при пустом
    /// Session.Effort флага не получает — поведение непричастных ходов не изменилось,
    /// лишний --effort у DeepSeek/cli/cli-эквивалента ничего бы не сломал, но зачем.
    /// </summary>
    [Fact]
    public async Task ХодНаОблачномПровайдере_ПустойEffort_ФлагаНет()
    {
        var args = await RunTurnAsync("deepseek-v4-pro", effort: null, providers: LocalProviders());

        args!.Should().NotContain("--effort", "fail-open: пусть CLI берёт свой дефолт");
    }

    /// <summary>
    /// Родной Claude (модель не резолвится ни в какого провайдера) при пустом
    /// Session.Effort — флага нет. Прежнее поведение сохраняется.
    /// </summary>
    [Fact]
    public async Task ХодНаРодномClaude_ПустойEffort_ФлагаНет()
    {
        var args = await RunTurnAsync("", effort: null, providers: LocalProviders());

        args!.Should().NotContain("--effort");
    }

    /// <summary>
    /// Защита от регресса: подмена неподдерживаемого уровня по-прежнему работает
    /// (EffortFor остаётся единой точкой подмены и для непустого Session.Effort).
    /// </summary>
    [Fact]
    public async Task ХодНаЛокальном_НепустойEffortHigh_СтавитLow()
    {
        var args = await RunTurnAsync(LocalModelId, effort: "high", providers: LocalProviders());

        var arr = args!.ToArray();
        var idx = Array.IndexOf(arr, "--effort");
        idx.Should().BeGreaterOrEqualTo(0);
        arr[idx + 1].Should().Be("low", "high не поддерживается — ближайший снизу low");
    }
}