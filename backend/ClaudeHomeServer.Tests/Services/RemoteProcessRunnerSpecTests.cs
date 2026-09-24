using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Сторож G3, серверная часть (ADR-016, задача 2.3): spec для устройства собирается по
// allow-list — ни одного ключа из BuildCliEnv/BuildOAuthCliEnv в env, ни одного
// Authorization (и вообще заголовков) в MCP-конфиге.
public class RemoteProcessRunnerSpecTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "remote-spec-" + Guid.NewGuid().ToString("N"));

    public RemoteProcessRunnerSpecTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    internal static LlmProviderRegistry Registry(string apiKey = "sk-test") => new(TestConfig.Build(new()
    {
        ["LlmProviders:deepseek:DisplayName"] = "DeepSeek",
        ["LlmProviders:deepseek:AnthropicBaseUrl"] = "https://api.deepseek.com/anthropic",
        ["LlmProviders:deepseek:ApiKey"] = apiKey,
        ["LlmProviders:deepseek:SmallModel"] = "deepseek-v4-flash",
        ["LlmProviders:deepseek:ExtraEnv:API_TIMEOUT_MS"] = "3000000",
        ["LlmProviders:deepseek:Models:0:Id"] = "deepseek-v4-pro",
        ["LlmProviders:deepseek:Models:0:ContextWindow"] = "1000000",
    }));

    // Всё, что реестр провайдеров способен положить в env хода
    internal static HashSet<string> ProviderProducedKeys(LlmProviderRegistry reg)
    {
        var keys = new HashSet<string>(LlmProviderRegistry.ProviderEnvKeys, StringComparer.Ordinal) { "CLAUDE_CODE_OAUTH_TOKEN" };
        keys.UnionWith(reg.BuildCliEnv("deepseek-v4-pro")!.Keys);
        keys.UnionWith(reg.BuildOAuthCliEnv("pool-1", "oauth-tok", model: "claude-opus-4-1")!.Keys);
        keys.UnionWith(reg.BuildOAuthCliEnv("pool-2", "oauth-tok", apiKey: "sk-ant-api", model: "claude-opus-4-1")!.Keys);
        return keys;
    }

    [Fact]
    public void AllowList_НеПересекаетсяСКлючамиПровайдеров()
    {
        var produced = ProviderProducedKeys(Registry());
        produced.Should().Contain(["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN", "API_TIMEOUT_MS"],
            "сторож обязан видеть и каталожные ключи, и ExtraEnv провайдера");
        RemoteProcessRunner.AllowedEnvKeys.Should().NotIntersectWith(produced);
    }

    [Fact]
    public void Env_ПолныйНаборПровайдера_НаУстройствоЕдетТолькоAllowList()
    {
        var reg = Registry();
        var env = new Dictionary<string, string>();
        foreach (var (k, v) in reg.BuildCliEnv("deepseek-v4-pro")!) env[k] = v;
        foreach (var (k, v) in reg.BuildOAuthCliEnv("pool-1", "oauth-tok", "sk-ant-api", "claude-opus-4-1")!) env[k] = v;
        env["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1";
        env["NO_PROXY"] = "localhost";
        env["Jwt__Key"] = "jwt-secret";

        var spawn = RemoteProcessRunner.BuildSpawn(new ProcessSpec { FileName = "claude", Env = env });

        spawn.Env.Should().Equal(new Dictionary<string, string> { ["CLAUDE_CODE_DISABLE_CLAUDE_MDS"] = "1" });
    }

    [Fact]
    public void McpКонфиг_ТолькоНашиHttpСерверы_НаСайдкар_БезЗаголовков()
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["tasks"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = McpEndpoints.EndpointFor("http://127.0.0.1:5000", McpEndpoints.TasksName, "sess-1") + "?token=leak",
                    ["headers"] = new JsonObject
                    {
                        ["Authorization"] = "Bearer service-jwt",
                        [McpEndpoints.CallerSessionHeader] = "sess-1",
                    },
                },
                ["pmem_kira"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = McpEndpoints.EndpointFor("https://ccs.example/base/", McpEndpoints.MemoryName, "p1/-"),
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer service-jwt" },
                },
                ["fal-ai"] = new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = "https://mcp.fal.ai/mcp",
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer fal-key" },
                },
                ["notes-stdio"] = new JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new JsonArray("/app/mcp/notes-server/index.js"),
                    ["env"] = new JsonObject { ["NOTES_API_TOKEN"] = "service-jwt" },
                },
            },
        };
        var path = Path.Combine(_tmp, "claude-mcp-1.json");
        File.WriteAllText(path, config.ToJsonString());
        var prompt = Path.Combine(_tmp, "CLAUDE-local.md");
        File.WriteAllText(prompt, "# карта");

        var spawn = RemoteProcessRunner.BuildSpawn(new ProcessSpec
        {
            FileName = "claude",
            Args = ["--mcp-config", path, "--system-prompt-file", prompt, "--resume", "abc"],
        });

        spawn.Args.Should().Equal("--mcp-config", DeviceExecPlaceholders.File("f1"),
            "--system-prompt-file", DeviceExecPlaceholders.File("f2"), "--resume", "abc");
        spawn.Files.Select(f => f.Name).Should().Equal("claude-mcp-1.json", "CLAUDE-local.md");
        spawn.Files[1].Content.Should().Be("# карта");

        var mcp = JsonNode.Parse(spawn.Files[0].Content)!["mcpServers"]!.AsObject();
        mcp.Select(kv => kv.Key).Should().BeEquivalentTo("tasks", "pmem_kira");
        ((string?)mcp["tasks"]!["url"]).Should().Be(DeviceExecPlaceholders.Sidecar + "/mcp/tasks/sess-1");
        ((string?)mcp["pmem_kira"]!["url"]).Should().Be(DeviceExecPlaceholders.Sidecar + "/mcp/memory/p1/-");

        var wire = Encoding.UTF8.GetString(DeviceExecJson.Serialize(
            new DeviceExecControl(DeviceExecControlOps.Spawn, "t1", spawn)));
        wire.Should().NotContainEquivalentOf("authorization");
        wire.Should().NotContain("headers").And.NotContain("service-jwt").And.NotContain("fal-key").And.NotContain("leak");
    }

    [Fact]
    public void ФайлФлагаНеНайден_ОтказВместоПередачиСырогоЗначения()
    {
        var act = () => RemoteProcessRunner.BuildSpawn(new ProcessSpec
        {
            FileName = "claude",
            Args = ["--mcp-config", """{"mcpServers":{"x":{"headers":{"Authorization":"Bearer t"}}}}"""],
        });
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("--settings")]
    [InlineData("--settings={\"apiKeyHelper\":\"x\"}")]
    public void Settings_НаУстройствоНеЕдут(string arg)
    {
        var act = () => RemoteProcessRunner.BuildSpawn(new ProcessSpec { FileName = "claude", Args = [arg, "x"] });
        act.Should().Throw<NotSupportedException>();
    }
}
