using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Ход реального <see cref="ClaudeSession"/> с фейковым лаунчером: вместо CLI печатается готовый ход,
/// а лаунчер забирает args и содержимое <c>--mcp-config</c> ровно в том виде, в каком они
/// ушли бы в процесс. Нужен сторожам рук (ADR-016 §7) и стабильности сигнатуры запуска.
/// </summary>
internal sealed class HandsTurnHarness : IDisposable
{
    public sealed record Turn(IReadOnlyList<string> Args, JsonObject? McpServers, string? Signature)
    {
        public IReadOnlyList<string> Disallowed
        {
            get
            {
                var i = Args.ToList().IndexOf("--disallowedTools");
                return i < 0 ? [] : Args[i + 1].Split(',');
            }
        }

        public string? PermissionMode
        {
            get
            {
                var i = Args.ToList().IndexOf("--permission-mode");
                return i < 0 ? null : Args[i + 1];
            }
        }
    }

    private readonly List<Process> _processes = [];
    private readonly CapturingLauncher _launcher;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ccs-hands-turn-" + Guid.NewGuid().ToString("N"));
    private TaskCompletionSource _exited = NewTcs();
    private readonly List<ServerMessage> _messages = [];

    public ClaudeSession Session { get; }

    public IReadOnlyList<ServerMessage> Messages
    {
        get { lock (_messages) return [.. _messages]; }
    }

    public HandsTurnHarness(bool handsEnabled, IReadOnlyList<string>? handsProviders,
        ClaudeMode mode = ClaudeMode.Default, string? model = null, LlmProviderRegistry? providers = null,
        Func<ExternalMcpContext?>? external = null, DeviceExecRefusedException? refuse = null)
    {
        Directory.CreateDirectory(_root);
        // Штатный ход CLI: init, ответ, result — и выход. Убитый без result процесс ClaudeSession
        // перезапускает сам (DiedEmpty-ретрай), и следующий ход встал бы за ним в очередь
        var transcript = Path.Combine(_root, "turn.jsonl");
        File.WriteAllLines(transcript,
        [
            """{"type":"system","subtype":"init","session_id":"hands-csid","model":"sonnet","tools":[],"mcp_servers":[]}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"готово"}]},"session_id":"hands-csid"}""",
            """{"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1,"result":"готово","session_id":"hands-csid","total_cost_usd":0,"usage":{"input_tokens":1,"output_tokens":1}}""",
        ]);
        _launcher = new CapturingLauncher(_processes, transcript) { Refuse = refuse };
        var context = new LlmSessionContext(
            RootPath: _root,
            OnMessage: m =>
            {
                lock (_messages) _messages.Add(m);
                if (m is ExitedMessage or ErrorMessage) _exited.TrySetResult();
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            // Продуктовый http-узел — должен пережить отсев stdio в ходе с руками
            WidgetsMcp: new WidgetsMcpContext("http://localhost:5999", () => "tok", UseHttp: true),
            ExternalMcpProvider: external,
            Launcher: _launcher,
            HandsEnabled: handsEnabled,
            HandsProviders: handsProviders);
        Session = new ClaudeSession(new Session { Model = model, Mode = mode }, context, providers: providers);
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<Turn> RunTurnAsync(string text = "ход")
    {
        _exited = NewTcs();
        var started = _launcher.Arm();
        await Session.SendMessageAsync(text);
        var done = await Task.WhenAny(started, Task.Delay(TimeSpan.FromSeconds(15)));
        done.Should().Be(started, "процесс хода обязан стартовать");
        var turn = new Turn(_launcher.Args!, _launcher.McpServers, Session.LastLaunchSignature);
        var exited = await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        exited.Should().Be(_exited.Task, "фейковый CLI отдаёт result и завершается сам");
        return turn;
    }

    public void Dispose()
    {
        lock (_processes)
            foreach (var p in _processes)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* уже мёртв */ }
                p.Dispose();
            }
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private sealed class CapturingLauncher(List<Process> processes, string transcript) : IProcessLauncher
    {
        private TaskCompletionSource? _started;
        // Отказ устройства на старте хода — как у RemoteProcessRunner локального проекта
        public DeviceExecRefusedException? Refuse { get; init; }
        public IReadOnlyList<string>? Args { get; private set; }
        public JsonObject? McpServers { get; private set; }
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Task Arm()
        {
            _started = NewTcs();
            return _started.Task;
        }

        public Process Start(ProcessSpec spec)
        {
            Args = spec.Args?.ToArray();
            McpServers = null;
            var i = Args?.ToList().IndexOf("--mcp-config") ?? -1;
            if (i >= 0 && File.Exists(Args![i + 1]))
                McpServers = JsonNode.Parse(File.ReadAllText(Args[i + 1]))?["mcpServers"] as JsonObject;
            if (Refuse is not null)
            {
                _started?.TrySetResult();
                throw Refuse;
            }

            var fake = new ProcessSpec
            {
                FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Args = OperatingSystem.IsWindows() ? ["/c", $"type \"{transcript}\""] : ["-c", $"cat '{transcript}'"],
                WorkingDirectory = spec.WorkingDirectory,
                StdioEncoding = spec.StdioEncoding,
                EnableRaisingEvents = spec.EnableRaisingEvents,
                RedirectStdin = spec.RedirectStdin,
                Track = false,
            };
            var process = LocalProcessRunner.Instance.Start(fake);
            lock (processes) processes.Add(process);
            _started?.TrySetResult();
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
}

// Правила хода с руками (ADR-016 §7, план рук Ш4, решения 4 и 7; решение владельца 3б): маркер
// рук, отсев stdio-узлов и режим прав зависят от ОДНОГО решения ClaudeSession.HandsActiveNow.
// Shell, сабагенты и запись в .claude/.mcp.json в ходе с руками не запрещаются (3б).
public class HandsTurnTests
{
    private static readonly ExternalMcpContext External = new(
    [
        new ExternalMcpServer("ext-stdio", "stdio", "node", ["server.js"], new Dictionary<string, string>(),
            null, new Dictionary<string, string>(), AlwaysLoad: false, AuthVersion: 0),
        new ExternalMcpServer("ext-http", "http", null, [], new Dictionary<string, string>(),
            "https://mcp.example.com/mcp", new Dictionary<string, string>(), AlwaysLoad: false, AuthVersion: 0),
    ]);

    [Fact]
    public async Task РукиВключены_ShellНеЗапрещён_МаркерРук_БезStdio()
    {
        using var h = new HandsTurnHarness(true, [HandsProviders.Claude], external: () => External);
        var turn = await h.RunTurnAsync();

        turn.Disallowed.Should().NotContain(["Bash", "PowerShell", "Monitor", "BashOutput", "KillShell", "Task", "Agent",
            "Edit(.claude/**)", "Write(.claude/**)", "Edit(.mcp.json)", "Write(.mcp.json)"],
            "решение владельца 3б: shell в чате с руками разрешён, запреты Ш4 сняты");
        HandsTurnRules.PermissionRefusal(turn.Args).Should().BeNull("агент сверяет режим прав хода с руками");

        var servers = turn.McpServers!;
        var hands = servers[DeviceExecPlaceholders.HandsServerName]!.AsObject();
        ((string?)hands["type"]).Should().Be(DeviceExecPlaceholders.Hands);
        ((bool?)hands[DeviceExecPlaceholders.HandsVisionField]).Should().BeTrue("у родного Claude зрение есть");
        hands.Select(kv => kv.Key).Should().BeEquivalentTo(["type", DeviceExecPlaceholders.HandsVisionField],
            "команду и путь моста подставляет агент, сервер их не шлёт");

        servers.ContainsKey("ext-stdio").Should().BeFalse("внешний stdio-сервер — канал запуска кода на устройстве");
        servers.ContainsKey("ext-http").Should().BeTrue("http-узлы отсев не задевает");
        servers.ContainsKey("widgets").Should().BeTrue();
        servers.Where(kv => kv.Key != DeviceExecPlaceholders.HandsServerName)
            .Should().OnlyContain(kv => (string?)kv.Value!["type"] == "http" || (string?)kv.Value!["type"] == "sse");
    }

    [Fact]
    public async Task РукиВыключены_НабораНет_StdioЕдет_МаркераНет()
    {
        using var h = new HandsTurnHarness(false, [HandsProviders.Claude], external: () => External);
        var turn = await h.RunTurnAsync();

        turn.McpServers!.ContainsKey(DeviceExecPlaceholders.HandsServerName).Should().BeFalse();
        turn.McpServers!.ContainsKey("ext-stdio").Should().BeTrue();
    }

    [Fact]
    public async Task ПровайдерНеДоверен_ХодБезРук_СтатусПровайдерНеРазрешён()
    {
        using var h = new HandsTurnHarness(true, ["deepseek"]);
        var turn = await h.RunTurnAsync();

        turn.Disallowed.Should().NotContain("Bash");
        turn.McpServers!.ContainsKey(DeviceExecPlaceholders.HandsServerName).Should().BeFalse();
        h.Messages.OfType<HandsStatusMessage>().Should().ContainSingle()
            .Which.State.Should().Be(HandsChatStates.ProviderNotAllowed);
    }

    // Агент отказал ходу: руки держит другой ход этой машины — бейдж узнаёт это по коду отказа
    [Fact]
    public async Task ОтказРукиЗаняты_СтатусНедоступныСПричинойBusy()
    {
        using var h = new HandsTurnHarness(true, [HandsProviders.Claude],
            refuse: new DeviceExecRefusedException(DeviceExecRefusal.HandsBusy, HandsMachineLock.BusyText));
        await h.RunTurnAsync();

        h.Messages.OfType<HandsStatusMessage>().Should().ContainSingle()
            .Which.Should().Be(new HandsStatusMessage(HandsChatStates.Unavailable, Reason: HandsEndReason.Busy));
        h.Messages.OfType<ErrorMessage>().Should().ContainSingle().Which.Text.Should().Be(HandsMachineLock.BusyText);
    }

    // Прочие отказы устройства — только ошибка хода; даже текст про занятые руки без кода busy не даёт
    [Theory]
    [InlineData(DeviceExecRefusal.AgentRefused, HandsMachineLock.BusyText)]
    [InlineData(DeviceExecRefusal.Offline, "Устройство не в сети")]
    [InlineData(DeviceExecRefusal.GatewayRefused, "Шлюз не выдал ходу маршрут.")]
    public async Task ПрочийОтказУстройства_БезПризнакаBusy(DeviceExecRefusal reason, string text)
    {
        using var h = new HandsTurnHarness(true, [HandsProviders.Claude],
            refuse: new DeviceExecRefusedException(reason, text));
        await h.RunTurnAsync();

        h.Messages.OfType<ErrorMessage>().Should().ContainSingle();
        h.Messages.OfType<HandsStatusMessage>().Should().NotContain(m => m.Reason == HandsEndReason.Busy);
    }

    [Fact]
    public async Task РукиИРежимБезОграничений_ПонижениеДоAcceptEdits_СтрокаВЛенту()
    {
        using var h = new HandsTurnHarness(true, [HandsProviders.Claude], mode: ClaudeMode.Bypass);
        var first = await h.RunTurnAsync();
        var second = await h.RunTurnAsync();

        first.Args.Should().NotContain("bypassPermissions", "ход с руками в bypassPermissions не идёт никогда");
        first.PermissionMode.Should().Be("acceptEdits");
        HandsTurnRules.PermissionRefusal(first.Args).Should().BeNull("иначе агент откажет ходу с руками");
        second.PermissionMode.Should().Be("acceptEdits");
        h.Messages.OfType<HandsNoticeMessage>().Should().ContainSingle("строка о понижении — одна, а не на каждый ход")
            .Which.Text.Should().Be(HandsTurnRules.BypassDowngradedText);
    }

    [Fact]
    public async Task БезРук_РежимБезОграниченийКакБыл()
    {
        using var h = new HandsTurnHarness(false, [HandsProviders.Claude], mode: ClaudeMode.Bypass);
        var turn = await h.RunTurnAsync();

        turn.PermissionMode.Should().Be("bypassPermissions", "контроль: понижение — только у хода с руками");
        h.Messages.OfType<HandsNoticeMessage>().Should().BeEmpty();
    }

    [Fact]
    public async Task ПровайдерБезЗрения_МаркерБезЗрения()
    {
        var providers = new LlmProviderRegistry(Helpers.TestConfig.Build(new Dictionary<string, string?>
        {
            ["LlmProviders:glm:AnthropicBaseUrl"] = "https://glm.example.com/anthropic",
            ["LlmProviders:glm:ApiKey"] = "sk-test",
            ["LlmProviders:glm:SupportsImages"] = "false",
            ["LlmProviders:glm:Models:0:Id"] = "glm-5",
        }));
        using var h = new HandsTurnHarness(true, ["glm"], model: "glm-5", providers: providers);
        var turn = await h.RunTurnAsync();

        var hands = turn.McpServers![DeviceExecPlaceholders.HandsServerName]!.AsObject();
        ((bool?)hands[DeviceExecPlaceholders.HandsVisionField]).Should().BeFalse(
            "без зрения агент запускает мост без screenshot_control");
    }

    [Fact]
    public void HandsPermissionMode_BypassПонижается_ОстальноеКакЕсть()
    {
        foreach (var mode in Enum.GetValues<ClaudeMode>())
            ClaudeSession.HandsPermissionMode(mode).Should().Be(mode == ClaudeMode.Bypass ? ClaudeMode.AcceptEdits : mode);
    }
}
