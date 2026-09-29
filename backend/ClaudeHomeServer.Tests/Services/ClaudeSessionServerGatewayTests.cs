using System.Diagnostics;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Services.Gateway;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Серверный ход провайдера с NormalizeToolInputArrays через шлюз LLM (ADR-016 §2): env хода
// смотрит на шлюз, ключ провайдера в env не попадает, токен без устройства выдаётся только новому
// процессу и отзывается финализацией прогона или сбоем запуска. Провайдер без флага, выключенный
// шлюз и чат проекта на устройстве — ход напрямую, как раньше.
//
// Платформонезависимость (CI — ubuntu): «CLI» — cmd/sh-сон без вывода, как в
// ClaudeSessionProxyBypassEnvTests.
public sealed class ClaudeSessionServerGatewayTests : IDisposable
{
    private const string GatewayApi = "http://ccs-gw-host:5000";
    private readonly List<Process> _processes = [];
    private readonly GatewayTestKit _kit = new();
    private readonly TurnTokenService _tokens = new(new TurnEventBus());

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            p.Dispose();
        }
        _kit.Dispose();
    }

    private sealed class EnvCapturingLauncher(List<Process> processes, bool failStart) : IProcessLauncher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            if (failStart) throw new InvalidOperationException("запуск упал");
            var process = LocalProcessRunner.Instance.Start(new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows() ? ["/c", "ping -n 120 127.0.0.1 >nul"] : ["-c", "sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            });
            lock (processes) processes.Add(process);
            Started.TrySetResult();
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

    private sealed record TurnResult(Dictionary<string, string>? Env, ClaudeSession Session, TaskCompletionSource Exited);

    private async Task<TurnResult> RunTurnAsync(string model, string? gatewayApiUrl = GatewayApi,
        bool gatewayEnabled = true, bool failStart = false)
    {
        _kit.Options.CurrentValue = new LlmGatewayOptions
        {
            Enabled = gatewayEnabled, AllowSubscriptions = false, AnthropicBaseUrl = "https://anthropic.test",
        };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var root = Path.Combine(Path.GetTempPath(), "ccs-server-gateway-tests");
        Directory.CreateDirectory(root);
        var launcher = new EnvCapturingLauncher(_processes, failStart);
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
            LlmGatewayApiUrl: gatewayApiUrl);
        var session = new ClaudeSession(new Session { Model = model, OwnerId = "owner-1" }, context,
            providers: _kit.Providers,
            serverGateway: new ServerTurnGateway(_kit.Selector, _tokens, _kit.Options));

        try { await session.SendMessageAsync("тестовый ход"); }
        catch (InvalidOperationException) when (failStart) { }
        if (!failStart)
            (await Task.WhenAny(launcher.Started.Task, Task.Delay(TimeSpan.FromSeconds(15))))
                .Should().Be(launcher.Started.Task, "процесс хода обязан стартовать");
        return new TurnResult(launcher.CapturedEnv, session, exited);
    }

    private async Task KillAndWaitAsync(TurnResult turn)
    {
        Process cli;
        lock (_processes) cli = _processes[^1];
        cli.Kill();
        await Task.WhenAny(turn.Exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
    }

    private static (string TurnId, string Token) GatewayOf(Dictionary<string, string> env)
    {
        var baseUrl = env["ANTHROPIC_BASE_URL"];
        baseUrl.Should().StartWith(GatewayApi + "/gw/t/").And.EndWith("/llm");
        var turnId = baseUrl[(GatewayApi + "/gw/t/").Length..^"/llm".Length];
        var header = env["ANTHROPIC_CUSTOM_HEADERS"].Split('\n')
            .Single(h => h.StartsWith(TurnTokenEndpointFilter.HeaderName + ": ", StringComparison.Ordinal));
        return (turnId, header[(TurnTokenEndpointFilter.HeaderName + ": ").Length..]);
    }

    [Fact]
    public async Task ПровайдерСФлагом_EnvНаШлюз_КлючаВEnvНет_ТокенЖивОтозванПоВыходу()
    {
        var turn = await RunTurnAsync("mmx-m3");
        var env = turn.Env!;

        var (turnId, token) = GatewayOf(env);
        turnId.Should().NotBe(new string('0', 32), "новому процессу — настоящий ход шлюза, а не заглушка");
        var grant = _tokens.Validate(turnId, token);
        grant.Should().NotBeNull("токен выдан и жив, пока жив процесс");
        grant!.DeviceId.Should().BeNull();
        grant.Lifetime.Should().Be(TurnTokenLifetime.Process);
        grant.Route!.ProviderKey.Should().Be("mmx");
        env.Values.Should().NotContain(v => v.Contains("mmx-key"), "ключ провайдера подставляет шлюз");
        env["NO_PROXY"].Split(',').Should().Contain("ccs-gw-host", "запрос к шлюзу не смеет уезжать в прокси");
        turn.Session.LastLaunchSignature.Should().NotContain(token, "сигнатура считается по заглушкам");

        await KillAndWaitAsync(turn);

        _tokens.Validate(turnId, token).Should().BeNull("финализация прогона отзывает токен процесса");
    }

    [Fact]
    public async Task ДваХода_СигнатураСтабильна_ТокеныРазные()
    {
        var first = await RunTurnAsync("mmx-m3");
        var firstSignature = first.Session.LastLaunchSignature;
        var (firstTurn, _) = GatewayOf(first.Env!);
        await KillAndWaitAsync(first);

        var second = await RunTurnAsync("mmx-m3");

        second.Session.LastLaunchSignature.Should().Be(firstSignature,
            "токен хода в сигнатуру не входит — иначе живой процесс не получал бы следующий ход");
        GatewayOf(second.Env!).TurnId.Should().NotBe(firstTurn);
        await KillAndWaitAsync(second);
    }

    // Живому процессу следующий ход отдаётся, только пока шлюз принимает его токен
    [Fact]
    public void IsAlive_ЖивойТокенДа_ОтозванныйИЧужойНет()
    {
        var gateway = new ServerTurnGateway(_kit.Selector, _tokens, _kit.Options);
        var issued = gateway.Start("owner-1", "chat", "mmx-m3").Token!;

        gateway.IsAlive(issued.Grant.TurnId, issued.Token).Should().BeTrue();
        gateway.IsAlive(issued.Grant.TurnId, "чужой").Should().BeFalse();
        gateway.End(issued.Grant.TurnId);
        gateway.IsAlive(issued.Grant.TurnId, issued.Token).Should().BeFalse();
    }

    [Fact]
    public async Task СбойЗапуска_ТокенОтозван()
    {
        await RunTurnAsync("mmx-m3", failStart: true);

        _tokens.ActiveCount.Should().Be(0);
    }

    [Theory]
    [InlineData("deepseek-v4-pro", GatewayApi, true)] // провайдер без флага
    [InlineData("mmx-m3", GatewayApi, false)] // шлюз выключен — ход напрямую
    [InlineData("mmx-m3", null, true)] // чат проекта на устройстве: шлюз ставит раннер устройства
    public async Task БезСерверногоРежима_ХодНапрямую(string model, string? gatewayApiUrl, bool gatewayEnabled)
    {
        var turn = await RunTurnAsync(model, gatewayApiUrl, gatewayEnabled);

        turn.Env!["ANTHROPIC_BASE_URL"].Should().Be(_kit.Providers.ResolveByModel(model)!.AnthropicBaseUrl);
        turn.Env.ContainsKey("ANTHROPIC_CUSTOM_HEADERS").Should().BeFalse();
        _tokens.ActiveCount.Should().Be(0);
        await KillAndWaitAsync(turn);
    }
}
