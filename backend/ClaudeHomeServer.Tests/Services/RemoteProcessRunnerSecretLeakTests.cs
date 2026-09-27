using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож G3 (ADR-016, серверная часть задачи 2.3): ход через RemoteProcessRunner ↔ агент в
// одном процессе с фейковым CLI, который пишет свой env/argv в каталог устройства. Затем
// сканер ищет точные значения фейковых секретов сервера — токены пула, API-ключи, сервисный
// JWT, JWT-секрет, токен хода — во всех файлах устройства и в принятых агентом кадрах.
// Контрольный тест с подложенным в spec JWT доказывает, что сканер вообще что-то видит.
public class RemoteProcessRunnerSecretLeakTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static readonly Dictionary<string, string> Secrets = new()
    {
        ["poolOAuthToken"] = "sk-ant-oat01-FAKE-POOL-OAUTH-7f3a9c",
        ["poolApiKey"] = "sk-ant-api03-FAKE-POOL-KEY-51be20",
        ["providerApiKey"] = "sk-FAKE-DEEPSEEK-KEY-c0ffee",
        ["serviceJwt"] = "eyJhbGciOiJIUzI1NiJ9.FAKE-SERVICE-JWT.b64sig",
        ["jwtSecret"] = "FAKE-JWT-SIGNING-SECRET-2b7d6e1f",
        ["turnToken"] = "tt_FAKE-TURN-TOKEN-9a8b7c",
        ["falKey"] = "FAKE-FAL-KEY-4e5f",
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-g3-" + Guid.NewGuid().ToString("N"));
    private readonly string _serverTmp;
    private readonly string _deviceDir;
    private readonly string _projectDir;
    private readonly string _fakeCli;

    public RemoteProcessRunnerSecretLeakTests()
    {
        _serverTmp = Directory.CreateDirectory(Path.Combine(_root, "server-tmp")).FullName;
        _deviceDir = Directory.CreateDirectory(Path.Combine(_root, "device")).FullName;
        _projectDir = Directory.CreateDirectory(Path.Combine(_deviceDir, "project")).FullName;
        Directory.CreateDirectory(Path.Combine(_deviceDir, "home"));
        _fakeCli = Path.Combine(_root, "fake-cli.mjs");
        File.WriteAllText(_fakeCli, FakeDeviceAgent.FakeCliSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // spec в том виде, в каком его собрал бы ClaudeSession серверного хода: env провайдера и
    // пула, MCP-конфиг с сервисным JWT, плюс то, что туда могла бы занести небрежная правка
    private ProcessSpec ServerStyleSpec(string turnId, params string[] extraArgs)
    {
        var reg = RemoteProcessRunnerSpecTests.Registry(Secrets["providerApiKey"]);
        var env = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in reg.BuildCliEnv("deepseek-v4-pro")!) env[k] = v;
        foreach (var (k, v) in reg.BuildOAuthCliEnv("pool-1", Secrets["poolOAuthToken"], Secrets["poolApiKey"], "claude-opus-4-1")!)
            env[k] = v;
        env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1";
        env["Jwt__Key"] = Secrets["jwtSecret"];
        env["CCS_TURN_TOKEN"] = Secrets["turnToken"];
        env["TASKS_API_TOKEN"] = Secrets["serviceJwt"];

        var bearer = "Bearer " + Secrets["serviceJwt"];
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["tasks"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = McpEndpoints.EndpointFor("http://127.0.0.1:5000", McpEndpoints.TasksName, "sess-1"),
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = bearer,
                        ["X-Turn-Token"] = Secrets["turnToken"],
                        [McpEndpoints.CallerSessionHeader] = "sess-1",
                    },
                },
                ["notes"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = McpEndpoints.EndpointFor("http://127.0.0.1:5000", McpEndpoints.NotesName, "sess-1"),
                    ["headers"] = new JsonObject { ["Authorization"] = bearer },
                },
                ["fal-ai"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = "https://mcp.fal.ai/mcp",
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + Secrets["falKey"] },
                },
                ["workspace-stdio"] = new JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new JsonArray("/app/mcp/workspace-server/index.js"),
                    ["env"] = new JsonObject { ["WORKSPACE_API_TOKEN"] = Secrets["serviceJwt"] },
                },
            },
        };
        var mcpPath = Path.Combine(_serverTmp, $"claude-mcp-{turnId}.json");
        File.WriteAllText(mcpPath, mcp.ToJsonString());
        var promptPath = Path.Combine(_serverTmp, "CLAUDE-local.md");
        File.WriteAllText(promptPath, "# Карта проекта");

        return new ProcessSpec
        {
            FileName = "claude",
            Args = ["--print", "--output-format", "stream-json", "--input-format", "stream-json",
                    "--permission-prompt-tool", "stdio", "--mcp-config", mcpPath,
                    "--system-prompt-file", promptPath, .. extraArgs],
            WorkingDirectory = _projectDir,
            Env = env,
            ClearEnv = LlmProviderRegistryKeys,
            RedirectStdin = true,
            StdioEncoding = new UTF8Encoding(false),
            EnableRaisingEvents = true,
            TurnId = turnId,
            SessionId = "sess-1",
        };
    }

    private static readonly IReadOnlyList<string> LlmProviderRegistryKeys =
        ClaudeHomeServer.Services.Llm.LlmProviderRegistry.ProviderEnvKeys;

    private async Task<(DeviceSecretScanner.Report Report, JsonNode Dump)> RunTurnAsync(ProcessSpec spec,
        bool agentLeaksGateway = false)
    {
        var agent = new FakeDeviceAgent(_deviceDir, _fakeCli) { LeakGatewayToDisk = agentLeaksGateway };
        var runner = new RemoteProcessRunner(new InProcessDeviceExecChannel { Agent = agent.RunAsync },
            new FakeDeviceTurnGateway { Token = Secrets["turnToken"] }, "owner-g3", "dev-1");

        using var p = runner.Start(spec);
        (await p.StandardOutput.ReadLineAsync().WaitAsync(Wait)).Should().Contain("init");
        await p.StandardInput.WriteLineAsync("""{"type":"user","message":{"content":"ход"}}""");
        await p.StandardInput.FlushAsync();
        (await p.StandardOutput.ReadLineAsync().WaitAsync(Wait)).Should().Contain("echo");
        p.StandardInput.Close();
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().Be(0);

        // Токен хода (выдан шлюзом, а не взят из spec) доехал до агента своим полем кадра —
        // сканер ниже проверяет, что больше он не попал никуда
        agent.ReceivedGateway?.Token.Should().Be(Secrets["turnToken"]);

        var dumpPath = Path.Combine(_projectDir, "cli-dump.json");
        File.Exists(dumpPath).Should().BeTrue("фейковый CLI обязан был реально отработать на устройстве");
        var report = DeviceSecretScanner.Scan(Secrets, _deviceDir,
            agent.ReceivedControl.Select((t, i) => ($"кадр управления #{i}", t)));
        return (report, JsonNode.Parse(File.ReadAllText(dumpPath))!);
    }

    [Fact]
    public async Task ХодНаУстройстве_НиОдногоСекретаСервера()
    {
        var (report, dump) = await RunTurnAsync(ServerStyleSpec("turn-g3", "--resume", "cli-sess-1"));

        report.ScannedFiles.Should().BeGreaterThanOrEqualTo(4, "журнал агента, MCP-конфиг, карта и дамп CLI");
        report.Hits.Should().BeEmpty();

        // Ход остался рабочим: BareMode доехал, MCP нашего бэкенда смотрит в сайдкар
        ((string?)dump["env"]!["CLAUDE_CODE_DISABLE_CLAUDE_MDS"]).Should().Be("1");
        ((string?)dump["env"]!["ANTHROPIC_BASE_URL"]).Should().Be(FakeDeviceAgent.SidecarUrl);
        var mcpText = dump["files"]!.AsObject().Single(kv => kv.Key.EndsWith(".json", StringComparison.Ordinal)).Value!.GetValue<string>();
        mcpText.Should().Contain(FakeDeviceAgent.SidecarUrl + "/mcp/tasks/sess-1");
        dump["argv"]!.AsArray().Select(a => (string?)a).Should().ContainInOrder("--resume", "cli-sess-1");
    }

    [Fact]
    public async Task Контроль_ПодложенныйВSpecJwt_СканерНаходит()
    {
        // Мутация сторожа: секрет, пронесённый аргументом, обязан всплыть на устройстве
        var (report, _) = await RunTurnAsync(
            ServerStyleSpec("turn-g3-mut", "--append-system-prompt", "token " + Secrets["serviceJwt"]));

        report.Hits.Select(h => h.Secret).Should().Contain("serviceJwt");
    }

    [Fact]
    public async Task Контроль_АгентЗаписалВыдачуШлюзаНаДиск_СканерНаходитТокенХода()
    {
        // Мутация сторожа на пути токена хода: он едет в кадре spawn, и агент, сохранивший
        // кадр целиком, обязан покраснеть
        var (report, _) = await RunTurnAsync(ServerStyleSpec("turn-g3-gw"), agentLeaksGateway: true);

        report.Hits.Select(h => h.Secret).Should().Contain("turnToken");
    }
}
