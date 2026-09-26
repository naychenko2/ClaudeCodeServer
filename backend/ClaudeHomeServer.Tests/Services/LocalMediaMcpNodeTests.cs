using System.Text.Json.Nodes;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.Llm;
using ClaudeHomeServer.Services.Llm.Claude;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Узел local-media в MCP-конфиге хода (QA 1e099019, дефект 3: «инструменты пропали после смены
/// чата»). Разбор показал, что пропажи в чате проекта не было — ходы ушли в чат вне проекта, где
/// узла нет по построению. Тест фиксирует обе половины: у чата проекта узел есть на КАЖДОМ ходе
/// с одинаковой сигнатурой (иначе перезапуск CLI со всеми MCP), у чата без контекста — нет.
/// </summary>
public class LocalMediaMcpNodeTests : IDisposable
{
    private readonly List<string> _configs = [];

    public void Dispose()
    {
        foreach (var path in _configs)
            try { File.Delete(path); } catch { }
    }

    private static ClaudeSession NewSession(LocalMediaMcpContext? localMedia) => new(
        new Session { Id = "sess-1", Model = "test-model" },
        new LlmSessionContext(
            RootPath: Path.Combine(Path.GetTempPath(), "ccs-lm-" + Guid.NewGuid().ToString("N")[..8]),
            OnMessage: _ => Task.CompletedTask,
            RawSystemPrompt: null,
            BuiltInSystemPrompt: ProjectManager.BuiltInSystemPrompt,
            PermissionRules: null,
            TasksMcp: null,
            LocalMediaMcp: localMedia));

    private (JsonObject? Servers, string Keys) BuildTurn(ClaudeSession session)
    {
        var method = typeof(ClaudeSession).GetMethod("BuildTurnMcpConfig",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var result = method.Invoke(session, [null, null])!;
        var type = result.GetType();
        var path = (string?)type.GetField("Item1")!.GetValue(result);
        var keys = (string)type.GetField("Item2")!.GetValue(result)!;
        if (string.IsNullOrEmpty(path)) return (null, keys);
        _configs.Add(path);
        return ((JsonObject)JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!, keys);
    }

    [Fact]
    public void ЧатПроекта_УзелНаКаждомХоде_СигнатураСтабильна()
    {
        var session = NewSession(new LocalMediaMcpContext("http://localhost:5000", () => "svc-tok", UseHttp: true));

        var first = BuildTurn(session);
        var second = BuildTurn(session);

        foreach (var turn in new[] { first, second })
        {
            var node = turn.Servers!["local-media"];
            node.Should().NotBeNull("контекст есть — узел local-media обязан ехать в каждый ход");
            node!["type"]!.GetValue<string>().Should().Be("http");
            node["url"]!.GetValue<string>().Should().Be("http://localhost:5000/mcp/local-media/sess-1");
        }
        second.Keys.Should().Be(first.Keys, "одинаковая сигнатура — CLI не перезапускается между ходами");
    }

    [Fact]
    public void БезКонтекста_УзлаНет()
    {
        // null контекста — чат вне проекта, локальный проект, RO-персона или выключенный тумблер
        var (servers, keys) = BuildTurn(NewSession(null));

        (servers?.ContainsKey("local-media") ?? false).Should().BeFalse();
        keys.Should().NotContain("local-media");
    }
}
