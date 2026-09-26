using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.DeviceAgent.Exec;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Sidecar;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Desktop;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Gateway;
using ClaudeHomeServer.Services.Turn;
using ClaudeHomeServer.Tests.Helpers;
using ClaudeHomeServer.Tests.Services.Gateway;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClaudeHomeServer.Tests.Services;

// Сквозной ход на устройстве (ADR-016, задача 2.8) в одном процессе поверх НАСТОЯЩИХ сторон:
// серверный RemoteProcessRunner ↔ серверный поток исполнения (DeviceExecStream) ↔ WebSocket ↔
// связь агента (ExecLink) ↔ TurnExecutor агента ↔ фейковый CLI ↔ сайдкар агента ↔ шлюз
// LLM/MCP сервера с фейковым upstream. Токен хода выдаёт настоящий UpstreamSelector.StartTurn
// через шов IDeviceTurnGateway. Фейковые здесь только CLI, upstream и конечная точка MCP.
public sealed class RemoteProcessRunnerDeviceAgentE2ETests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private const string SetupToken = "sk-ant-oat01-E2E-SETUP-TOKEN-5d1e";
    private const string ServiceJwt = "eyJhbGciOiJIUzI1NiJ9.E2E-SERVICE-JWT.sig";
    private const string SessionId = "chat-e2e";
    private const string UpstreamText = "привет от upstream";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "device-e2e-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly GatewayTestKit _kit = new(("st1", SetupToken, null));
    private readonly TurnEventBus _bus = new();
    private readonly TurnTokenService _tokens;
    // Настоящая учётка устройства: шлюз пускает токен хода только вместе с ней
    private readonly GatewayTestDevice _device = new("owner-e2e");
    private readonly FakeUpstream _upstream = new();
    private readonly TurnGrants _grants = new();

    private string _deviceDir = "";
    private string _projectDir = "";
    private string _serverTmp = "";
    private WebApplication _gatewayApp = null!;
    private SidecarHost _sidecar = null!;
    private TurnExecutor _executor = null!;
    private SpyTurnGateway _gateway = null!;
    private AgentChannel _channel = null!;
    private RemoteProcessRunner _runner = null!;

    private sealed class FakeUpstream : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Calls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Calls) Calls.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"content":[{"type":"text","text":"{{UpstreamText}}"}]}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    // Настоящий шов шлюза плюс сигнал отзыва: конец исполнения наступает в фоне раннера
    private sealed class SpyTurnGateway(IDeviceTurnGateway inner) : IDeviceTurnGateway
    {
        public DeviceExecGateway? Issued { get; private set; }
        public TaskCompletionSource<string> Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string OwnerId, string SessionId, string DeviceId)> Started { get; } = [];

        public DeviceTurnGatewayStart StartTurn(string ownerId, string sessionId, string deviceId, string? model)
        {
            lock (Started) Started.Add((ownerId, sessionId, deviceId));
            var start = inner.StartTurn(ownerId, sessionId, deviceId, model);
            Issued = start.Gateway;
            return start;
        }

        public void EndTurn(string gatewayTurnId)
        {
            inner.EndTurn(gatewayTurnId);
            Ended.TrySetResult(gatewayTurnId);
        }
    }

    private sealed class Identity(string deviceToken) : IDeviceIdentity
    {
        public Uri ServerUri { get; } = new("http://localhost/");
        public string DeviceToken => deviceToken;
        public string Fingerprint => GatewayTestDevice.Fingerprint;
    }

    public RemoteProcessRunnerDeviceAgentE2ETests() => _tokens = new TurnTokenService(_bus);

    // Разрешённый корень машины — сам каталог проекта
    private sealed class ProjectRoot(string root) : IAgentRoots
    {
        public IReadOnlyList<string> Roots { get; } = [root];
    }

    private sealed class CliSource(string path) : ICliLeaseSource
    {
        public ICliHandle? TryAcquire(out string? problem)
        {
            problem = null;
            return new Handle(path);
        }

        private sealed class Handle(string path) : ICliHandle
        {
            public string ExecutablePath => path;
            public string Version => "9.9.9-e2e";
            public void Dispose() { }
        }
    }

    // Канал исполнения без хаба и HTTP: настоящий серверный поток и настоящая связь агента
    // соединены WebSocket поверх loopback TCP; команду «открой исполнение» агент получает сразу
    private sealed class AgentChannel(TurnExecutor executor) : IDeviceExecChannel
    {
        public int Opened;
        public readonly List<Task> Runs = [];
        public readonly List<(ExecLink Link, LoopbackConnector Connector)> Links = [];
        // Потолок простоя серверного потока: null — боевой
        public TimeSpan? ServerMaxOutage;

        public DeviceExecStatus? GetStatus(string ownerId, string deviceId) =>
            new(deviceId, "e2e", Online: true, "linux", "1.0", "9.9.9-e2e", "9.9.9-e2e",
                [DeviceCapabilities.Exec], HarnessReady: true, HarnessProblem: null);

        public async Task<IDeviceExecStream> OpenAsync(string ownerId, string deviceId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Opened);
            var execId = Guid.NewGuid().ToString("N");
            var stream = new DeviceExecStream(execId, ownerId, deviceId, _ => { }, ServerMaxOutage);
            var connector = new LoopbackConnector(stream);
            var link = new ExecLink(execId, connector, TimeSpan.FromSeconds(20));
            await link.StartAsync(ct);
            lock (Links) Links.Add((link, connector));
            lock (Runs) Runs.Add(Task.Run(() => executor.RunAsync(link, CancellationToken.None)));
            return stream;
        }
    }

    private sealed class LoopbackConnector(DeviceExecStream stream) : IExecSocketConnector
    {
        // Устройство потеряло сеть: переподключения не проходят
        public volatile bool Unreachable;

        public async Task<WebSocket> ConnectAsync(string execId, CancellationToken ct)
        {
            if (Unreachable) throw new IOException("устройство без сети");
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync(ct).AsTask();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
            var serverSide = await accept;

            var serverSocket = WebSocket.CreateFromStream(serverSide.GetStream(), isServer: true, null, Timeout.InfiniteTimeSpan);
            _ = Task.Run(() => stream.RunAsync(serverSocket, CancellationToken.None));
            return WebSocket.CreateFromStream(client.GetStream(), isServer: false, null, Timeout.InfiniteTimeSpan);
        }
    }

    // Фейковый CLI: пишет свой env/argv/файлы в рабочий каталог, порождает внука, по строке
    // stdin «llm» зовёт LLM через ANTHROPIC_BASE_URL, по «mcp» — MCP tasks из конфига хода,
    // на user-сообщение stream-json (ход ClaudeSession) зовёт LLM и отвечает result'ом; первый
    // такой ход взводит фоновую задачу — прогон доживает, и следующий ход идёт тем же процессом
    private const string FakeCli = """
        #!/usr/bin/env node
        const fs = require('fs');
        const { spawn } = require('child_process');
        const argv = process.argv.slice(2);
        const files = {};
        for (const a of argv) { try { if (fs.statSync(a).isFile()) files[a] = fs.readFileSync(a, 'utf8'); } catch {} }
        fs.writeFileSync('cli-dump.json', JSON.stringify({ env: process.env, argv, files }));
        const child = spawn('sleep', ['600'], { stdio: 'ignore' });
        const out = o => process.stdout.write(JSON.stringify(o) + '\n');
        out({ type: 'system', subtype: 'init', pid: process.pid, grandchild: child.pid });
        const mcpUrl = () => JSON.parse(Object.values(files).find(t => t.includes('mcpServers'))).mcpServers.tasks.url;
        const llm = () => fetch(process.env.ANTHROPIC_BASE_URL + '/v1/messages?beta=true', { method: 'POST',
          headers: { 'content-type': 'application/json', authorization: 'Bearer ' + process.env.ANTHROPIC_AUTH_TOKEN },
          body: JSON.stringify({ model: 'claude-opus-5-5', max_tokens: 1 }) });
        let bgStarted = false;
        function userMessage(line) { try { return JSON.parse(line).type === 'user'; } catch { return false; } }
        async function handle(line) {
          if (line === 'llm') {
            const r = await llm();
            out({ type: 'llm', status: r.status, body: await r.text() });
          } else if (userMessage(line)) {
            if (!bgStarted) {
              bgStarted = true;
              out({ type: 'system', subtype: 'task_started', task_id: 'bg-1', tool_use_id: 'toolu-bg-1', description: 'bg',
                subagent_type: 'general-purpose', task_type: 'local_agent', prompt: 'p' });
            }
            const r = await llm();
            const ok = r.status === 200;
            out({ type: 'result', subtype: ok ? 'success' : 'error_during_execution', is_error: !ok,
              duration_ms: 1, num_turns: 1, result: ok ? 'ok' : await r.text() });
          } else if (line === 'mcp') {
            const r = await fetch(mcpUrl(), { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{}' });
            out({ type: 'mcp', status: r.status, body: await r.text() });
          } else out({ type: 'echo', line });
        }
        let buf = '';
        let chain = Promise.resolve();
        process.stdin.on('data', d => {
          buf += d.toString('utf8');
          let i;
          while ((i = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, i); buf = buf.slice(i + 1);
            chain = chain.then(() => handle(line)).catch(e => out({ type: 'error', message: String(e) }));
          }
        });
        process.stdin.on('end', () => chain.then(() => process.exit(0)));
        """;

    public async Task InitializeAsync()
    {
        _deviceDir = Directory.CreateDirectory(Path.Combine(_root, "device")).FullName;
        _projectDir = Directory.CreateDirectory(Path.Combine(_deviceDir, "project")).FullName;
        _serverTmp = Directory.CreateDirectory(Path.Combine(_root, "server-tmp")).FullName;
        var cliPath = Path.Combine(_deviceDir, "cli", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(cliPath)!);
        File.WriteAllText(cliPath, FakeCli.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(cliPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Шлюз сервера: настоящие маршруты LLM и фильтр токена хода, upstream — фейковый.
        // MCP-эндпоинт — заглушка за тем же фильтром: проверяется сквозной адрес, а не tasks
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IOptionsMonitor<LlmGatewayOptions>>(_kit.Options);
        builder.Services.AddSingleton(_tokens);
        builder.Services.AddSingleton(_kit.Selector);
        builder.Services.AddSingleton(new SubscriptionLimitRecorder(_kit.Usage, _kit.Pool));
        builder.Services.AddHttpClient(LlmGatewayEndpoints.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _upstream);
        _device.AddTo(builder.Services);
        _gatewayApp = builder.Build();
        _gatewayApp.MapLlmGateway();
        _gatewayApp.Map("/gw/t/{" + TurnTokenEndpointFilter.RouteKey + "}/mcp/{**rest}",
                (HttpContext ctx) => Results.Text("mcp:" + ctx.Request.Path))
            .AddEndpointFilter<TurnTokenEndpointFilter>();
        await _gatewayApp.StartAsync();

        _sidecar = await SidecarHost.StartAsync(_grants, new Identity(_device.Token), NullLoggerFactory.Instance,
            gatewayHandler: _gatewayApp.GetTestServer().CreateHandler());

        var inherited = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Directory.CreateDirectory(Path.Combine(_deviceDir, "home")).FullName,
            ["LANG"] = "C.UTF-8",
            // Секрет в окружении агента: до CLI он доехать не должен
            ["ANTHROPIC_API_KEY"] = "sk-ant-api-AGENT-ENV-SECRET",
        };
        _executor = new TurnExecutor(new ExecOptions
        {
            TurnsRoot = Path.Combine(_deviceDir, "turns"),
            ConfigDirectory = Path.Combine(_deviceDir, "profile"),
            SidecarUrl = () => _sidecar.Url,
            InheritedEnvironment = () => inherited,
            DrainTimeout = TimeSpan.FromSeconds(10),
            PathPolicy = new AgentPathPolicy(new ProjectRoot(_projectDir)),
        }, new CliSource(cliPath), _grants, new TurnJournal(Path.Combine(_deviceDir, "journal")));

        _gateway = new SpyTurnGateway(new DeviceTurnGateway(_kit.Selector, _tokens));
        _channel = new AgentChannel(_executor);
        _runner = new RemoteProcessRunner(_channel, _gateway, "owner-e2e", _device.Id);
    }

    public async Task DisposeAsync()
    {
        _executor.KillAll();
        Task[] runs;
        lock (_channel.Runs) runs = [.. _channel.Runs];
        try { await Task.WhenAll(runs).WaitAsync(Wait); } catch { }
        await _sidecar.DisposeAsync();
        await _gatewayApp.DisposeAsync();
        _kit.Dispose();
        _device.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProcessSpec Spec(string turnId)
    {
        // MCP-конфиг — как у серверного хода: адрес бэкенда и сервисный JWT в заголовке
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["tasks"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = McpEndpoints.EndpointFor("http://127.0.0.1:5000", McpEndpoints.TasksName, SessionId),
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + ServiceJwt },
                },
            },
        };
        var mcpPath = Path.Combine(_serverTmp, $"claude-mcp-{turnId}.json");
        File.WriteAllText(mcpPath, mcp.ToJsonString());

        return new ProcessSpec
        {
            FileName = "claude",
            Args = ["--print", "--output-format", "stream-json", "--input-format", "stream-json",
                    "--model", "claude-opus-5-5", "--mcp-config", mcpPath],
            WorkingDirectory = _projectDir,
            Env = new Dictionary<string, string> { ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1" },
            RedirectStdin = true,
            StdioEncoding = new UTF8Encoding(false),
            EnableRaisingEvents = true,
            TurnId = turnId,
            SessionId = SessionId,
        };
    }

    private static async Task<JsonNode> ReadJsonAsync(Process p)
    {
        var line = await p.StandardOutput.ReadLineAsync().WaitAsync(Wait);
        line.Should().NotBeNull("ретранслятор закрыл stdout раньше ожидаемой строки");
        return JsonNode.Parse(line!)!;
    }

    private static async Task<JsonNode> AskAsync(Process p, string line)
    {
        await p.StandardInput.WriteLineAsync(line);
        await p.StandardInput.FlushAsync();
        return await ReadJsonAsync(p);
    }

    // Процесс жив: есть в /proc и не зомби
    private static bool IsAlive(int pid)
    {
        var stat = $"/proc/{pid}/stat";
        if (!File.Exists(stat)) return false;
        try
        {
            var text = File.ReadAllText(stat);
            return text[(text.LastIndexOf(')') + 2)] != 'Z';
        }
        catch (IOException) { return false; }
    }

    private static async Task WaitDeadAsync(int pid)
    {
        using var cts = new CancellationTokenSource(Wait);
        while (IsAlive(pid))
            await Task.Delay(50, cts.Token);
    }

    [SkippableFact]
    public async Task Ход_ПроходитСквозь_LlmИMcpЧерезСайдкарИШлюз_ТокенТолькоВПамятиАгента_ОтзывПоКонцу()
    {
        Skip.If(OperatingSystem.IsWindows(), "фейковый CLI — скрипт с shebang, дерево процессов — группа Unix");

        using var p = _runner.Start(Spec("turn-e2e"));
        var init = await ReadJsonAsync(p);
        ((string?)init["subtype"]).Should().Be("init", "stdout CLI на устройстве доехал до серверного Process");

        var issued = _gateway.Issued!;
        issued.Should().NotBeNull();
        _tokens.Validate(issued.TurnId, issued.Token, _device.Id).Should().NotBeNull("токен выдан настоящим StartTurn");
        _tokens.Validate(issued.TurnId, issued.Token, "чужое-устройство").Should().BeNull("токен привязан к устройству хода");

        // LLM: CLI → сайдкар (/t/{ключ}/llm) → шлюз (/gw/t/{ход}/llm) → upstream с setup-token
        var llm = await AskAsync(p, "llm");
        ((int?)llm["status"]).Should().Be(200, (string?)llm["body"]);
        ((string?)llm["body"]).Should().Contain(UpstreamText);
        HttpRequestMessage up;
        lock (_upstream.Calls) up = _upstream.Calls.Single();
        up.Headers.GetValues("Authorization").Should().Equal("Bearer " + SetupToken);
        up.Headers.Contains(TurnTokenEndpointFilter.HeaderName).Should().BeFalse();

        // MCP: адрес из конфига хода ({{ccs-sidecar}} развёрнут агентом) доходит до шлюза
        var mcp = await AskAsync(p, "mcp");
        ((int?)mcp["status"]).Should().Be(200, (string?)mcp["body"]);
        ((string?)mcp["body"]).Should().Be($"mcp:/gw/t/{issued.TurnId}/mcp/tasks/{SessionId}");

        (await AskAsync(p, "привет"))["line"]!.GetValue<string>().Should().Be("привет", "UTF-8 через всю петлю");

        p.StandardInput.Close();
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().Be(0);

        // Конец хода — токен отозван: шлюз его больше не принимает
        (await _gateway.Ended.Task.WaitAsync(Wait)).Should().Be(issued.TurnId);
        _tokens.ActiveCount.Should().Be(0);
        using var gw = _gatewayApp.GetTestClient();
        (await gw.SendAsync(GatewayRequest(issued))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Адресация сайдкара на обеих сторонах одна: env и конфиг MCP смотрят на /t/{ключ}
        var dump = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_projectDir, "cli-dump.json")))!;
        var baseUrl = (string?)dump["env"]!["ANTHROPIC_BASE_URL"];
        var turnUrl = Regex.Match(baseUrl!, $"^{Regex.Escape(_sidecar.Url)}/t/[0-9a-f]{{32}}(?=/llm$)");
        turnUrl.Success.Should().BeTrue(baseUrl);
        var mcpText = dump["files"]!.AsObject().Single().Value!.GetValue<string>();
        mcpText.Should().Contain($"\"{turnUrl.Value}/mcp/tasks/{SessionId}\"");
        ((string?)dump["env"]!["CLAUDE_CODE_DISABLE_CLAUDE_MDS"]).Should().Be("1");

        // Сторож G3 на пути токена хода: ни на диске устройства, ни в env/argv/файлах CLI
        // (всё это в дампе) нет ни токена хода, ни учёток сервера и устройства
        var secrets = new Dictionary<string, string>
        {
            ["turnToken"] = issued.Token,
            ["setupToken"] = SetupToken,
            ["serviceJwt"] = ServiceJwt,
            ["deviceToken"] = _device.Token,
            ["agentEnvKey"] = "sk-ant-api-AGENT-ENV-SECRET",
        };
        var report = DeviceSecretScanner.Scan(secrets, _deviceDir);
        report.ScannedFiles.Should().BeGreaterThanOrEqualTo(2, "дамп CLI и сам CLI");
        report.Hits.Should().BeEmpty();
        _grants.Count.Should().Be(0, "выдача шлюза снята с агента по концу хода");
    }

    [SkippableFact]
    public async Task Kill_ПоTurnId_УбиваетДеревоНаУстройстве_ТокенОтзывается()
    {
        Skip.If(OperatingSystem.IsWindows(), "фейковый CLI — скрипт с shebang, дерево процессов — группа Unix");

        using var p = _runner.Start(Spec("turn-e2e-kill"));
        var init = await ReadJsonAsync(p);
        var cliPid = (int)init["pid"]!;
        var grandchildPid = (int)init["grandchild"]!;
        IsAlive(cliPid).Should().BeTrue();
        IsAlive(grandchildPid).Should().BeTrue();

        // Kill ищет ход по владельцу и TurnId, а не по объекту процесса
        using var unrelated = Process.Start(new ProcessStartInfo("node", ["-e", "setTimeout(()=>{}, 60000)"]))!;
        new RemoteProcessRunner(_channel, _gateway, "owner-e2e", _device.Id).Kill(unrelated, "turn-e2e-kill");

        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().NotBe(0);
        await WaitDeadAsync(cliPid);
        await WaitDeadAsync(grandchildPid);

        var issued = _gateway.Issued!;
        (await _gateway.Ended.Task.WaitAsync(Wait)).Should().Be(issued.TurnId);
        _tokens.Validate(issued.TurnId, issued.Token, _device.Id).Should().BeNull();
        _executor.LiveCount.Should().Be(0);
    }

    // Ретранслятор — дочерний процесс бэкенда; его SIGKILL закрывает loopback-сокет, мост
    // видит конец и доходит до finally с отзывом токена. Потолок — минута (задача ревью 2.4)
    [SkippableFact]
    public async Task KillМинус9Ретранслятора_ТокенОтзываетсяНеПозжеМинуты_ХодНаУстройствеУбит()
    {
        Skip.If(OperatingSystem.IsWindows(), "фейковый CLI — скрипт с shebang, дерево процессов — группа Unix");

        using var p = _runner.Start(Spec("turn-e2e-relay-kill9"));
        var init = await ReadJsonAsync(p);
        var cliPid = (int)init["pid"]!;
        var issued = _gateway.Issued!;
        _tokens.Validate(issued.TurnId, issued.Token, _device.Id).Should().NotBeNull();

        using (var kill = Process.Start("kill", ["-9", p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)])!)
            await kill.WaitForExitAsync();

        (await _gateway.Ended.Task.WaitAsync(TimeSpan.FromMinutes(1))).Should().Be(issued.TurnId);
        _tokens.Validate(issued.TurnId, issued.Token, _device.Id).Should().BeNull();
        await WaitDeadAsync(cliPid);
    }

    // Устройство пропало из сети посреди хода и не вернулось, ретранслятор жив и держит свой
    // сокет. Серверный поток ждёт реконнекта не дольше потолка простоя, затем закрывается:
    // ретранслятор выходит с ошибкой, токен отзывается
    [SkippableFact]
    public async Task ОбрывКаналаУстройстваБезВозврата_РетрансляторЖив_ТокенОтзываетсяПоПотолкуПростоя()
    {
        Skip.If(OperatingSystem.IsWindows(), "фейковый CLI — скрипт с shebang, дерево процессов — группа Unix");
        _channel.ServerMaxOutage = TimeSpan.FromSeconds(2);

        using var p = _runner.Start(Spec("turn-e2e-device-lost"));
        ((string?)(await ReadJsonAsync(p))["subtype"]).Should().Be("init");
        var issued = _gateway.Issued!;

        var (link, connector) = _channel.Links.Single();
        connector.Unreachable = true;
        link.AbortConnection();

        (await _gateway.Ended.Task.WaitAsync(TimeSpan.FromMinutes(1))).Should().Be(issued.TurnId);
        _tokens.Validate(issued.TurnId, issued.Token, _device.Id).Should().BeNull();
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().NotBe(0, "процесс на устройстве кода выхода не прислал");
    }

    // Прямой звонок шлюзу с учёткой устройства хода: 401 здесь — только из-за токена
    private HttpRequestMessage GatewayRequest(DeviceExecGateway issued)
    {
        var req = _device.Sign(new HttpRequestMessage(HttpMethod.Get, $"/gw/t/{issued.TurnId}/llm/api/hello"));
        req.Headers.TryAddWithoutValidation(TurnTokenEndpointFilter.HeaderName, issued.Token);
        return req;
    }

    // ADR-016 §2: ClaudeSession держит один процесс CLI на много ходов — токен живёт по
    // процессу, а не по ходу: конец хода его не гасит, kill процесса — гасит
    [SkippableFact]
    public async Task ДваХодаВОдномПроцессе_ОбаПроходятШлюз_ПослеKillПроцесса401()
    {
        Skip.If(OperatingSystem.IsWindows(), "фейковый CLI — скрипт с shebang, дерево процессов — группа Unix");

        using var p = _runner.Start(Spec("turn-e2e-two"));
        ((string?)(await ReadJsonAsync(p))["subtype"]).Should().Be("init");
        var issued = _gateway.Issued!;

        var first = await AskAsync(p, "llm");
        ((int?)first["status"]).Should().Be(200, (string?)first["body"]);
        await _bus.PublishAsync(new TurnCompleted(new TurnContext(SessionId, "owner-e2e", 1, 0), "success"));

        var second = await AskAsync(p, "llm");
        ((int?)second["status"]).Should().Be(200, "второй ход того же процесса: " + (string?)second["body"]);
        var mcp = await AskAsync(p, "mcp");
        ((int?)mcp["status"]).Should().Be(200, (string?)mcp["body"]);
        lock (_upstream.Calls) _upstream.Calls.Should().HaveCount(2);

        _runner.Kill(p, "turn-e2e-two");
        await p.WaitForExitAsync().WaitAsync(Wait);
        (await _gateway.Ended.Task.WaitAsync(Wait)).Should().Be(issued.TurnId);
        using var gw = _gatewayApp.GetTestClient();
        (await gw.SendAsync(GatewayRequest(issued))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "процесс убит");
    }

    [Fact]
    public void ОтказШлюза_ХодЗавершаетсяОшибкойСТекстом_ПроцессНаУстройствеНеСтартует()
    {
        _kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = true, AllowSubscriptions = false };

        var act = () => _runner.Start(Spec("turn-e2e-refused"));

        var refused = act.Should().Throw<DeviceExecRefusedException>().Which;
        refused.Reason.Should().Be(DeviceExecRefusal.GatewayRefused);
        refused.Message.Should().Be(TurnFailureText.GatewaySubscriptionsDisabled);
        _channel.Opened.Should().Be(0, "канал исполнения не открывался");
        _executor.LiveCount.Should().Be(0);
        _tokens.ActiveCount.Should().Be(0);
        Directory.Exists(Path.Combine(_deviceDir, "turns")).Should().BeFalse("каталог хода на устройстве не создан");
    }

    // ADR-016 §5: папка хода вне разрешённых корней машины — отказ агента доходит до человека
    // причиной (UI узнаёт её по «разрешённым корням»), а не общим «процесс упал»
    [Fact]
    public void ПапкаВнеКорнейМашины_ОтказАгентаСПричиной_CLIНеСтартует()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_deviceDir, "outside")).FullName;

        var act = () => _runner.Start(Spec("turn-e2e-outside") with { WorkingDirectory = outside });

        var refused = act.Should().Throw<DeviceExecRefusedException>().Which;
        refused.Reason.Should().Be(DeviceExecRefusal.AgentRefused);
        refused.Message.Should().Contain("не под разрешёнными корнями").And.Contain("ai-home-agent roots add");
        TurnFailureText.ForException(refused).Should().Be(refused.Message);
        _executor.LiveCount.Should().Be(0);
        _tokens.ActiveCount.Should().Be(0, "токен хода отозван вместе с отказом");
    }

    [Fact]
    public void ШлюзВыключен_ХодЗавершаетсяОшибкойСПричиной_ПроцессНаУстройствеНеСтартует()
    {
        _kit.Options.CurrentValue = new LlmGatewayOptions { Enabled = false, AllowSubscriptions = true };

        var act = () => _runner.Start(Spec("turn-e2e-gw-off"));

        var refused = act.Should().Throw<DeviceExecRefusedException>().Which;
        refused.Reason.Should().Be(DeviceExecRefusal.GatewayRefused);
        refused.Message.Should().Be(TurnFailureText.GatewayDisabled);
        _channel.Opened.Should().Be(0, "канал исполнения не открывался");
        _executor.LiveCount.Should().Be(0);
        _tokens.ActiveCount.Should().Be(0);
    }

    // ADR-016, задача 3.2а: настоящий ход чата локального проекта. Раннер берёт боевая фабрика
    // (ForProject), ClaudeSession сам ставит в spec свой чат — по нему шлюз выдаёт токен.
    // Два хода идут одним процессом CLI на устройстве, и оба проходят шлюз LLM.
    [SkippableFact]
    public async Task ХодЧатаЛокальногоПроекта_ЧерезForProject_ТокенСЧатомИУстройством_ДваХодаОднимПроцессом()
    {
        Skip.If(OperatingSystem.IsWindows(), "фейковый CLI — скрипт с shebang, дерево процессов — группа Unix");

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataPath"] = Path.Combine(_serverTmp, "data", "projects.json"),
                ["Sandbox:ProjectsRoot"] = Path.Combine(_serverTmp, "sandbox"),
            }).Build();
        var factory = new LauncherFactory(
            new UserStore(config, new FakeHostEnvironment(), NullLogger<UserStore>.Instance),
            new SandboxManager(config, NullLogger<SandboxManager>.Instance),
            () => _channel, () => _gateway);
        var project = new ClaudeHomeServer.Models.Project { OwnerId = "owner-e2e", DeviceId = _device.Id, RootPath = _projectDir };
        var launcher = factory.ForProject(project);
        launcher.Should().BeOfType<RemoteProcessRunner>();

        var results = System.Threading.Channels.Channel.CreateUnbounded<ResultMessage>();
        var errors = new List<ErrorMessage>();
        var context = new LlmSessionContext(
            RootPath: _projectDir,
            OnMessage: m =>
            {
                if (m is ResultMessage r) results.Writer.TryWrite(r);
                if (m is ErrorMessage e) lock (errors) errors.Add(e);
                return Task.CompletedTask;
            },
            RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: launcher);
        var chat = new ClaudeHomeServer.Models.Session { ProjectId = "p-local" };
        var session = new ClaudeHomeServer.Services.Llm.Claude.ClaudeSession(chat, context);
        await using (session)
        {
            await session.SendMessageAsync("первый ход");
            var first = await ReadResultAsync(results, errors);
            first.Subtype.Should().Be("success");

            lock (_gateway.Started)
                _gateway.Started.Should().Equal([("owner-e2e", chat.Id, _device.Id)], "токен выдан чату и устройству этого проекта");
            var issued = _gateway.Issued!;
            _tokens.Validate(issued.TurnId, issued.Token, _device.Id)!.SessionId.Should().Be(chat.Id);

            await session.SendMessageAsync("второй ход");
            var second = await ReadResultAsync(results, errors);
            second.Subtype.Should().Be("success", "второй ход того же процесса проходит шлюз тем же токеном");

            _channel.Opened.Should().Be(1, "оба хода — один процесс CLI на устройстве");
            lock (_gateway.Started) _gateway.Started.Should().HaveCount(1);
            lock (_upstream.Calls) _upstream.Calls.Should().HaveCount(2);
            lock (errors) errors.Should().BeEmpty();
        }

        // Чат закрыт — процесс на устройстве убит, токен отозван
        (await _gateway.Ended.Task.WaitAsync(Wait)).Should().Be(_gateway.Issued!.TurnId);
        _tokens.ActiveCount.Should().Be(0);
    }

    private static async Task<ResultMessage> ReadResultAsync(
        System.Threading.Channels.Channel<ResultMessage> results, List<ErrorMessage> errors)
    {
        try
        {
            return await results.Reader.ReadAsync().AsTask().WaitAsync(Wait);
        }
        catch (TimeoutException)
        {
            lock (errors) throw new TimeoutException("ход не дошёл до result: " + string.Join(" | ", errors.Select(e => e.Text + " " + e.Details)));
        }
    }
}
