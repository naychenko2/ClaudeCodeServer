using System.Diagnostics;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Узел tests (run_tests) в MCP-конфиге хода и потолок вызова MCP-инструмента в env CLI:
/// узел едет в каждый ход при контексте и гасится TrimMcpServers без Keep("tests");
/// MCP_TOOL_TIMEOUT=600000 стоит в env и не делает сигнатуру запуска зависимой от хода.
/// </summary>
public class TestsMcpNodeTests : IDisposable
{
    private readonly List<string> _configs = [];
    private readonly List<Process> _processes = [];
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { }
        foreach (var p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            p.Dispose();
        }
        foreach (var dir in _dirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
    }

    private static TestsMcpContext Ctx() => new("http://localhost:5000", () => "svc-tok", UseHttp: true);

    private static LlmProviderRegistry Providers(bool trim, string keep = "tasks")
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
        };
        if (trim)
        {
            dict["LlmProviders:local:TrimMcpServers"] = "true";
            dict["LlmProviders:local:KeepMcpServers:0"] = keep;
        }
        return new LlmProviderRegistry(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(dict).Build());
    }

    private (JsonObject? Servers, string Keys) BuildTurn(TestsMcpContext? tests, bool trim = false, string keep = "tasks")
    {
        var context = new LlmSessionContext(
            RootPath: Path.Combine(Path.GetTempPath(), "ccs-tests-" + Guid.NewGuid().ToString("N")[..8]),
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            TestsMcp: tests);
        var session = new ClaudeSession(new Session { Id = "sess-1", Model = "test-model" }, context,
            providers: Providers(trim, keep));
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null, false])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;
        if (string.IsNullOrEmpty(path)) return (null, keys);
        _configs.Add(path);
        return ((JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!, keys);
    }

    [Fact]
    public void Контекст_УзелНаКаждомХоде_СигнатураСтабильна()
    {
        var first = BuildTurn(Ctx());
        var second = BuildTurn(Ctx());

        foreach (var turn in new[] { first, second })
        {
            var node = turn.Servers![McpEndpoints.TestsName];
            node.Should().NotBeNull("контекст есть — узел tests обязан ехать в каждый ход");
            node!["type"]!.GetValue<string>().Should().Be("http");
            node["url"]!.GetValue<string>().Should().Be("http://localhost:5000/mcp/tests/sess-1");
            node["headers"]![McpEndpoints.CallerSessionHeader]!.GetValue<string>().Should().Be("sess-1");
        }
        second.Keys.Should().Be(first.Keys);
    }

    [Fact]
    public void БезКонтекста_УзлаНет()
    {
        var (servers, keys) = BuildTurn(null);

        (servers?.ContainsKey(McpEndpoints.TestsName) ?? false).Should().BeFalse();
        keys.Should().NotContain("tests");
    }

    [Fact]
    public void TrimMcp_TestsНеВKeep_Гасится()
    {
        var (servers, keys) = BuildTurn(Ctx(), trim: true);

        (servers?.ContainsKey(McpEndpoints.TestsName) ?? false).Should().BeFalse(
            "tests не в KeepMcpServers провайдера — селективный блок TrimMcpServers гасит узел");
        keys.Should().NotContain("tests");
    }

    // Сборка (dev: build) едет вторым узлом на том же контексте, что и tests: гейты одни
    [Fact]
    public void Контекст_УзелDevНаКаждомХоде_СигнатураСтабильна()
    {
        var first = BuildTurn(Ctx());
        var second = BuildTurn(Ctx());

        foreach (var turn in new[] { first, second })
        {
            var node = turn.Servers![McpEndpoints.DevName];
            node.Should().NotBeNull("контекст есть — узел dev обязан ехать в каждый ход");
            node!["type"]!.GetValue<string>().Should().Be("http");
            node["url"]!.GetValue<string>().Should().Be("http://localhost:5000/mcp/dev/sess-1");
            node["headers"]![McpEndpoints.CallerSessionHeader]!.GetValue<string>().Should().Be("sess-1");
        }
        first.Keys.Should().Contain("dev");
        second.Keys.Should().Be(first.Keys);
    }

    [Fact]
    public void БезКонтекста_УзлаDevНет()
    {
        var (servers, _) = BuildTurn(null);

        (servers?.ContainsKey(McpEndpoints.DevName) ?? false).Should().BeFalse();
    }

    // TrimMcpServers гасит dev своим ключом: Keep("tests") оставляет только тесты
    [Fact]
    public void TrimMcp_KeepТолькоTests_DevГасится()
    {
        var (servers, _) = BuildTurn(Ctx(), trim: true, keep: McpEndpoints.TestsName);

        servers!.ContainsKey(McpEndpoints.TestsName).Should().BeTrue();
        servers.ContainsKey(McpEndpoints.DevName).Should().BeFalse("dev не в KeepMcpServers провайдера");
    }

    // Лаунчер ловит env и сигнатуру хода; процесс — спящий shell, гасится в Dispose
    private sealed class EnvCapturingLauncher(List<Process> processes) : IProcessLauncher
    {
        public TaskCompletionSource<IReadOnlyDictionary<string, string>> Env { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsSandboxed => false;
        public bool TargetIsWindows => OperatingSystem.IsWindows();
        public IPathMapper Paths => IdentityPathMapper.Instance;
        public string ClaudeCliCommand => "fake-claude";
        public string HostTempDir => Path.GetTempPath();
        public string? McpApiUrlOverride => null;

        public Process Start(ProcessSpec spec)
        {
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
            Env.TrySetResult(spec.Env?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? []);
            return process;
        }

        public int EstimateCommandLineLength(ProcessSpec spec)
        {
            var total = spec.FileName.Length;
            foreach (var a in spec.Args) total += TurnPromptAssembler.ArgCost(a);
            return total;
        }

        public void Kill(Process process, string? turnId = null)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    private async Task<(IReadOnlyDictionary<string, string> Env, string? Signature)> RunTurnAsync(string root)
    {
        var launcher = new EnvCapturingLauncher(_processes);
        var context = new LlmSessionContext(
            RootPath: root,
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null, BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            Launcher: launcher,
            TestsMcp: Ctx());
        var session = new ClaudeSession(new Session { Id = "sess-env" }, context);
        await session.SendMessageAsync("ход");
        var env = await launcher.Env.Task.WaitAsync(TimeSpan.FromSeconds(15));
        return (env, session.LastLaunchSignature);
    }

    [Fact]
    public async Task EnvХода_McpToolTimeout600000_СигнатураНеЗависитОтХода()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccs-tests-env-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _dirs.Add(root);

        var first = await RunTurnAsync(root);
        var second = await RunTurnAsync(root);

        first.Env.Should().Contain("MCP_TOOL_TIMEOUT", "600000",
            "без потолка вызова CLI оборвёт прогон тестов раньше серверного потолка 540 с");
        first.Signature.Should().NotBeNullOrEmpty();
        second.Signature.Should().Be(first.Signature,
            "значение постоянное — два хода с одинаковыми свойствами дают одну сигнатуру запуска");
    }
}
