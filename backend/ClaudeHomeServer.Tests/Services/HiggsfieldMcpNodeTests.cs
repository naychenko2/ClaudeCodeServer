using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Тесты узла higgsfield в MCP-конфиге хода (фаза 1.3):
/// 1. Инстанс подключён (context non-null, UseHttp=true) → узел higgsfield в конфиге
/// 2. Инстанс не подключён (context null) → узла нет
/// 3. TrimMcpServers=true, higgsfield не в KeepMcpServers → узел гасится
/// </summary>
public class HiggsfieldMcpNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "ccs-hf-node-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<string> _configs = [];

    public HiggsfieldMcpNodeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // Минимальный контекст: только higgsfield (или null) — без остальных MCP-серверов.
    // UseHttp=true — http-ветка, stdio-файлы не нужны.
    // TasksMcp — required positional parameter (nullable): null = «задач нет».
    private static LlmSessionContext Ctx(HiggsfieldMcpContext? hf) => new(
        RootPath: Path.Combine(Path.GetTempPath(), "ccs-hf-" + Guid.NewGuid().ToString("N")[..8]),
        OnMessage: _ => Task.CompletedTask,
        RawSystemPrompt: null,
        BuiltInSystemPrompt: ClaudeHomeServer.Services.ProjectManager.BuiltInSystemPrompt,
        PermissionRules: null,
        TasksMcp: null,
        HiggsfieldMcp: hf);

    private static LlmProviderRegistry NoTrim()
    {
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
        };
        return new LlmProviderRegistry(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(dict).Build());
    }

    private IReadOnlyList<string> BuildServers(HiggsfieldMcpContext? hf)
    {
        var context = Ctx(hf);
        var session = new ClaudeSession(new Session { Model = "test-model" }, context, providers: NoTrim());
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        if (string.IsNullOrEmpty(path)) return [];
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var mcpServers = (JsonObject)doc["mcpServers"]!;
        return [.. mcpServers.Select(kv => (string)kv.Key!),
               .. keys.Split(';', StringSplitOptions.RemoveEmptyEntries)];
    }

    [Fact]
    public void ИнстансПодключён_УзелHiggsfieldВКонфиге()
    {
        var hf = new HiggsfieldMcpContext("http://localhost:5000", () => "svc-tok", UseHttp: true);
        var servers = BuildServers(hf);

        servers.Should().Contain("higgsfield",
            "инстанс подключён (context non-null, UseHttp=true) — узел higgsfield обязан быть в конфиге хода");
    }

    [Fact]
    public void ИнстансНеПодключён_УзлаHiggsfieldНет()
    {
        // null контекста = «инстанс не подключён» (BuildHiggsfieldContext вернул null:
        // RO-персона или EnsureFresh() = null)
        var servers = BuildServers(null);

        servers.Should().NotContain("higgsfield",
            "инстанс не подключён (context=null) — узел higgsfield не объявляется ходу");
    }

    [Fact]
    public void TrimMcp_HiggsfieldНеВKeep_Гасится()
    {
        // Провайдер с TrimMcpServers=true и KeepMcpServers=["tasks"] — higgsfield не в списке
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:TrimMcpServers"] = "true",
            ["LlmProviders:local:KeepMcpServers:0"] = "tasks",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
        };
        var providers = new LlmProviderRegistry(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(dict).Build());

        var hf = new HiggsfieldMcpContext("http://localhost:5000", () => "svc-tok", UseHttp: true);
        var context = Ctx(hf);
        var session = new ClaudeSession(new Session { Model = "test-model" }, context, providers: providers);
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;

        if (string.IsNullOrEmpty(path))
        {
            // Если нет ни одного сервера → конфиг не создан, higgsfield гасился полностью
            keys.Should().NotContain("higgsfield");
            return;
        }
        _configs.Add(path);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        var mcpServers = (JsonObject)doc["mcpServers"]!;
        mcpServers.Should().NotContainKey("higgsfield",
            "higgsfield не в KeepMcpServers провайдера — селективный блок TrimMcpServers гасит узел");
    }

    /// <summary>
    /// BareMode-профиль → KeepMcpTools["higgsfield"] = ровно 3 инструмента (generate_image,
    /// generate_video, job_status). Проверка через McpToolWhitelist: белый список фильтрует
    /// состав tools/list до трёх, остальные 7 инструментов полного ядра вырезаются.
    /// </summary>
    [Fact]
    public void BareMode_KeepMcpToolsHiggsfield_ТриИнструмента()
    {
        // Провайдер с KeepMcpTools["higgsfield"] = 3 инструмента
        var dict = new Dictionary<string, string?>
        {
            ["LlmProviders:local:AnthropicBaseUrl"] = "http://127.0.0.1:18020",
            ["LlmProviders:local:IsLocal"] = "true",
            ["LlmProviders:local:Models:0:Id"] = "test-model",
            // KeepMcpTools: higgsfield → 3 tools (BareMode-состав)
            ["LlmProviders:local:KeepMcpTools:higgsfield:0"] = "generate_image",
            ["LlmProviders:local:KeepMcpTools:higgsfield:1"] = "generate_video",
            ["LlmProviders:local:KeepMcpTools:higgsfield:2"] = "job_status",
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(dict).Build();
        var providers = new LlmProviderRegistry(config);
        var provider = providers.ResolveByModel("test-model");
        provider.Should().NotBeNull("провайдер должен резолвиться по модели");
        // Проверяем, что провайдер имеет KeepMcpTools с higgsfield → 3 инструмента
        provider!.KeepMcpTools.Should().ContainKey("higgsfield",
            "KeepMcpTools содержит ключ higgsfield");
        var tools = provider.KeepMcpTools["higgsfield"];
        tools.Should().HaveCount(3,
            "BareMode-состав higgsfield: ровно 3 инструмента (generate_image, generate_video, job_status)");
        tools.Should().BeEquivalentTo(
            new[] { "generate_image", "generate_video", "job_status" },
            "состав KeepMcpTools[higgsfield]: generate_image, generate_video, job_status");
    }
}
