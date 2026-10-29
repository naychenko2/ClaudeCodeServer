using System.Diagnostics;
using System.Reflection;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.Services.Gateway;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// SP1 «учётки хода» (нарезка c47fe6da, шаг SP2): ResultMessage обязан нести фактический маршрут
// прогона — запись расхода должна знать, у кого ход реально ушёл.
//
// Правило ключа (ComputeUpstreamProviderKey): источник правды — модель:
// ResolveByModel(EffectiveModel)?.Key; иначе Info.Provider, если он НЕ ключ стороннего
// провайдера (GetByKey is null) — т.е. "claude" или аккаунт пула; иначе "claude" (stale
// сторонний ключ при родной модели не приписывает родной ход стороннему — инвариант
// «не занижать расход Claude»). Ключ пишется как есть, "-direct" не срезается.
// Для шлюзового прогона — текущий маршрут токена: ProviderKey ?? SubscriptionKey (его знает
// только шлюз; ротация аккаунта посреди хода в нём же). Модель — только из маршрута Provider
// (её переписал шлюз); у подписочного маршрута Model — подсказка `--model` клиента, не модель
// апстрима: UpstreamModel = null, и расход берёт модель из modelUsage CLI.
//
// Мутация (c7): снятие ветки «stale сторонний → claude» (проверки GetByKey во второй ветке)
// краснит строку ("sonnet", "minimax", "claude") теории ниже.
public class ClaudeSessionUpstreamRouteTests : IDisposable
{
    private static readonly Type CliRunType =
        typeof(ClaudeSession).GetNestedType("CliRun", BindingFlags.NonPublic)!;

    private static readonly MethodInfo ProcessLineAsyncMethod =
        typeof(ClaudeSession).GetMethod("ProcessLineAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private const string RealResultLine =
        """{"type":"result","subtype":"success","duration_ms":35069,"num_turns":1,"usage":{"input_tokens":2,"output_tokens":772,"cache_read_input_tokens":52037,"cache_creation_input_tokens":77156},"total_cost_usd":0.81}""";

    private readonly List<Process> _fakeProcesses = [];

    public void Dispose()
    {
        foreach (var p in _fakeProcesses) ProcessTestCleanup.SafeDispose(p); // Process.Close гонится с уборкой сессии
    }

    // Конфиг: сторонний облачный (minimax), локальный (localqwen) и ghostcli с общим префиксом
    // "claude-" — тот случай, когда явный claude-id резолвится в сторонний апстрим.
    private static LlmProviderRegistry BuildProviders()
    {
        var dict = new Dictionary<string, string?>
        {
            ["DataPath"] = Path.Combine(Path.GetTempPath(), "upstream_route_" + Guid.NewGuid().ToString("N") + ".json"),
            ["LlmProviders:minimax:AnthropicBaseUrl"] = "https://minimax.test/anthropic",
            ["LlmProviders:minimax:ApiKey"] = "mm-key",
            ["LlmProviders:minimax:Models:0:Id"] = "MiniMax-M2.1",
            ["LlmProviders:localqwen:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:localqwen:IsLocal"] = "true",
            ["LlmProviders:localqwen:Models:0:Id"] = "qwen3.8-27b",
            ["LlmProviders:ghostcli:AnthropicBaseUrl"] = "https://ghost.test/anthropic",
            ["LlmProviders:ghostcli:ApiKey"] = "gh-key",
            ["LlmProviders:ghostcli:ModelPrefixes:0"] = "claude-",
        };
        return new LlmProviderRegistry(TestConfig.Build(dict));
    }

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewSession(
        LlmProviderRegistry providers, string? model, string? provider, string? ownerId = null)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null);
        // Provider у Session нон-nullable с дефолтом "claude"; пустая строка — тот же случай
        // «ключа сессии нет», который проверяет строка теории со null.
        var session = new ClaudeSession(new Session { Model = model, Provider = provider ?? "", OwnerId = ownerId },
            context, providers: providers);
        return (session, sent);
    }

    // CliRun вручную (белый-бокс, как ClaudeSessionEmptyResultSwallowTests): поля маршрута
    // ставляются тестом, чтобы проверить доставку значения в ResultMessage, а не только расчёт.
    private object NewRun(string? upstreamProvider, string? gatewayTurnId = null)
    {
        var run = Activator.CreateInstance(CliRunType, nonPublic: true)!;
        var process = new Process();
        _fakeProcesses.Add(process);
        CliRunType.GetProperty("Process")!.SetValue(run, process);
        CliRunType.GetProperty("Signature")!.SetValue(run, "test");
        CliRunType.GetProperty("UpstreamProvider")!.SetValue(run, upstreamProvider);
        if (gatewayTurnId is not null) CliRunType.GetProperty("GatewayTurnId")!.SetValue(run, gatewayTurnId);
        return run;
    }

    private static async Task DriveLine(ClaudeSession session, object run, string line)
    {
        var task = (Task)ProcessLineAsyncMethod.Invoke(session, [run, line])!;
        await task;
    }

    private ResultMessage SingleResult(List<ServerMessage> sent)
    {
        lock (sent) return sent.OfType<ResultMessage>().Single();
    }

    /// <summary>
    /// Таблица правила ключа (c2/c5): модель — источник правды; родная модель — ключ сессии,
    /// если он не сторонний; stale сторонний ключ — "claude". Мутация (снятие проверки GetByKey
    /// во второй ветке) краснит строку ("sonnet", "minimax", "claude"): вернётся "minimax".
    /// </summary>
    [Theory]
    [InlineData("qwen3.8-27b", "claude-2", "localqwen")]   // локальная модель при подписочном ключе — её провайдер
    [InlineData("MiniMax-M2.1", "claude-2", "minimax")]    // модель стороннего провайдера — его ключ
    [InlineData("opus", "claude-3", "claude-3")]           // родная модель на аккаунте пула — ключ аккаунта
    [InlineData("sonnet", "claude", "claude")]             // основной родной ход — "claude"
    [InlineData("sonnet", null, "claude")]                 // без ключа сессии — "claude"
    [InlineData("sonnet", "minimax", "claude")]            // stale сторонний ключ при родной модели — "claude" (мутация краснит)
    [InlineData("claude-opus-5-5", "claude", "ghostcli")]  // явный claude-id у провайдера с префиксом "claude-" — его ключ
    public void КлючАпстрима_ПоМоделиЗатемКлючуСессии(string model, string? sessionProvider, string expected)
    {
        var (session, _) = NewSession(BuildProviders(), model, sessionProvider);

        session.ComputeUpstreamProviderKey().Should().Be(expected);
    }

    /// <summary>
    /// Прямой прогон (c4): ключ, сохранённый в CliRun при старте, доезжает до ResultMessage
    /// (в CliRun — decoy-ключ: проверяется доставка значения из прогона, а не повторный расчёт).
    /// Модель у прямого прогона не подставляется — её источник не шлюз.
    /// </summary>
    [Fact]
    public async Task ПрямойПрогон_ResultMessageНесётКлючИзCliRun()
    {
        var (session, sent) = NewSession(BuildProviders(), "qwen3.8-27b", null);
        var run = NewRun(upstreamProvider: "decoy-key");

        await DriveLine(session, run, RealResultLine);

        var result = SingleResult(sent);
        result.UpstreamProvider.Should().Be("decoy-key", "ключ прогона доезжает до записи расхода");
        result.UpstreamModel.Should().BeNull("у прямого прогона модели шлюза нет");
    }

    // Шлюзовой прогон (c3): ключ и модель — из текущего маршрута токена хода, а не из
    // настроек сессии и не из резервного ключа CliRun (decoy).
    [Fact]
    public async Task ХодЧерезШлюз_ResultMessageНесётМаршрутТокена()
    {
        var kit = new GatewayTestKit();
        var tokens = new TurnTokenService(new TurnEventBus());
        var gateway = new ServerTurnGateway(kit.Selector, tokens, kit.Options);
        var issued = gateway.Start("owner-1", "chat-1", "mmx-m3").Token!;

        var (session, sent) = NewSessionWithGateway(kit, gateway);
        var run = NewRun(upstreamProvider: "decoy-key", gatewayTurnId: issued.Grant.TurnId);

        await DriveLine(session, run, RealResultLine);

        var result = SingleResult(sent);
        result.UpstreamProvider.Should().Be("mmx", "ключ провайдера из маршрута токена (ProviderKey)");
        result.UpstreamModel.Should().Be("mmx-m3", "модель маршрута, а не запроса сессии");
    }

    // «Текущий» маршрут: ротация аккаунта посреди хода (Reroute) меняет и то, что несёт
    // запись расхода. Мутация (чтение маршрута из привязки при выдаче, а не из токена)
    // краснит этот тест.
    [Fact]
    public async Task РотацияМаршрутаПосредиХода_ResultMessageНесётТекущий()
    {
        var kit = new GatewayTestKit();
        var tokens = new TurnTokenService(new TurnEventBus());
        var gateway = new ServerTurnGateway(kit.Selector, tokens, kit.Options);
        var issued = gateway.Start("owner-1", "chat-1", "mmx-m3").Token!;
        tokens.Reroute(issued.Grant.TurnId,
            new GatewayRoute(GatewayUpstreamKind.Subscription, "opus", SubscriptionKey: "claude-9"));

        var (session, sent) = NewSessionWithGateway(kit, gateway);
        var run = NewRun(upstreamProvider: "decoy-key", gatewayTurnId: issued.Grant.TurnId);

        await DriveLine(session, run, RealResultLine);

        var result = SingleResult(sent);
        result.UpstreamProvider.Should().Be("claude-9", "после ротации — аккаунт, которым ход пошёл дальше");
        // Мутация (безусловный route.Model в UpstreamOfRun) краснит эту строку: у подписочного
        // маршрута Model — подсказка `--model`, не модель апстрима; её место — UsageModel CLI.
        result.UpstreamModel.Should().BeNull("подписочный маршрут не несёт модель апстрима");
    }

    // Токен отозван (потолок жизни/сбой) — резерв: ключ, посчитанный при старте прогона.
    [Fact]
    public async Task ОтозванныйТокенШлюза_РезервныйКлючИзCliRun()
    {
        var kit = new GatewayTestKit();
        var tokens = new TurnTokenService(new TurnEventBus());
        var gateway = new ServerTurnGateway(kit.Selector, tokens, kit.Options);
        var issued = gateway.Start("owner-1", "chat-1", "mmx-m3").Token!;
        gateway.End(issued.Grant.TurnId);

        var (session, sent) = NewSessionWithGateway(kit, gateway);
        var run = NewRun(upstreamProvider: "decoy-key", gatewayTurnId: issued.Grant.TurnId);

        await DriveLine(session, run, RealResultLine);

        SingleResult(sent).UpstreamProvider.Should().Be("decoy-key",
            "маршрут недоступен — резерв из стартового расчёта");
    }

    // «CLI», который стартовал и сразу печатает result-строку в stdout (содержимое — из файла,
    // чтобы не возиться с экранированием JSON в cmd/sh), затем «спит» до уборки.
    private sealed class ResultEmittingLauncher(List<Process> processes, string resultFile)
        : ClaudeHomeServer.Services.Execution.IProcessLauncher
    {
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public ClaudeHomeServer.Services.Execution.IPathMapper Paths =>
            ClaudeHomeServer.Services.Execution.IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ClaudeHomeServer.Services.Execution.ProcessSpec spec)
        {
            var fake = new ClaudeHomeServer.Services.Execution.ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows()
                    ? ["/c", $"type \"{resultFile}\" & ping -n 120 127.0.0.1 >nul"]
                    : ["-c", $"cat '{resultFile}'; exec sleep 120"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = ClaudeHomeServer.Services.Execution.LocalProcessRunner.Instance.Start(fake);
            lock (processes) processes.Add(process);
            return process;
        }

        public int EstimateCommandLineLength(ClaudeHomeServer.Services.Execution.ProcessSpec spec)
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

    /// <summary>
    /// Стык «расчёт ключа при старте → CliRun → ResultMessage» без подстановки в CliRun руками:
    /// реальный SendMessageAsync, «CLI» печатает result-строку. Ключ сессии ("claude-2") и ожидаемый
    /// ключ апстрима ("localqwen", по модели) различаются, так что совпадение не случайно.
    /// Мутация: снять UpstreamProvider = ComputeUpstreamProviderKey() в инициализации CliRun
    /// (ClaudeSession.cs) — в ResultMessage будет null.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    public async Task РеальныйСтартПрогона_КлючПосчитанПриСтартеДоезжаетДоResultMessage()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "upstream_route_run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var resultFile = Path.Combine(tempDir, "result.jsonl");
            await File.WriteAllTextAsync(resultFile, RealResultLine + "\n");
            var root = Path.Combine(tempDir, "root");
            Directory.CreateDirectory(root);

            var gotResult = new TaskCompletionSource<ResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var launcher = new ResultEmittingLauncher(_fakeProcesses, resultFile);
            var context = new LlmSessionContext(
                RootPath: root,
                OnMessage: m =>
                {
                    if (m is ResultMessage r) gotResult.TrySetResult(r);
                    return Task.CompletedTask;
                },
                RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
                PermissionRules: null,
                TasksMcp: null,
                Launcher: launcher);
            var info = new Session { Model = "qwen3.8-27b", Provider = "claude-2" };
            var session = new ClaudeSession(info, context, providers: BuildProviders());

            await session.SendMessageAsync("тестовый ход");
            var done = await Task.WhenAny(gotResult.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            done.Should().Be(gotResult.Task, "result-строка «CLI» обязана дойти до клиента");
            (await gotResult.Task).UpstreamProvider.Should().Be("localqwen",
                "ключ из стартового расчёта доезжает до записи расхода без пересчёта");
        }
        finally
        {
            foreach (var p in _fakeProcesses)
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
            try { Directory.Delete(tempDir, recursive: true); } catch { /* не критично */ }
        }
    }

    private static (ClaudeSession Session, List<ServerMessage> Sent) NewSessionWithGateway(
        GatewayTestKit kit, ServerTurnGateway gateway)
    {
        var sent = new List<ServerMessage>();
        var context = new LlmSessionContext(
            RootPath: Path.GetTempPath(),
            OnMessage: msg => { lock (sent) sent.Add(msg); return Task.CompletedTask; },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            LlmGatewayApiUrl: "http://ccs-gw-host:5000");
        var session = new ClaudeSession(new Session { Model = "mmx-m3", OwnerId = "owner-1" },
            context, providers: kit.Providers, serverGateway: gateway);
        return (session, sent);
    }
}
