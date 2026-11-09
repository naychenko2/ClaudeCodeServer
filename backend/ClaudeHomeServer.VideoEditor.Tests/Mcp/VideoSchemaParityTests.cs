using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using ClaudeHomeServer.Services.VideoEditor.Mcp;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// Схемы MCP статичны (ADR-025 §8): пропорции и ступени модели в схему не попадают — иначе схема зависела бы от каталога
// (живого у Higgsfield), префов и сцен, а это перезапуск CLI со всеми серверами и обнулённый prefix cache.
// Сторож для video-editor: схемы при разных префах, сценах и замках побайтно одинаковы (состав tools/list держит
// McpToolsetStabilityTests)
public sealed class VideoSchemaParityTests
{
    private static string Schemas(AgentWorld w) =>
        new JsonArray([.. w.Toolset.ToolsFor(w.Ctx())
            .Select(t => (JsonNode?)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["schema"] = t.InputSchema.DeepClone() })])
            .ToJsonString();

    [Fact]
    public void Схемы_при_разных_префах_сценах_и_замках_побайтно_одинаковы()
    {
        using var plain = new AgentWorld();
        using var busy = new AgentWorld();
        busy.Prefs.Save(busy.Owner, busy.F.Scope, new VideoPrefsDto("local", "minimax-h3", 5, null, null, 2,
            new MediaFormat(FormatModes.Aspect, "9:16", FormatTier.Max, null, null, ContextActor.Agent, DateTime.UtcNow)));
        var scene = busy.NewScene();
        busy.F.Threads.SetFilmRef(busy.Owner, busy.Chat, scene.SceneId, new VideoFilmRefDto("video/утро/утро.film", 1),
            new VideoFormatLockDto("video/утро/утро.film", "16:9"));

        Schemas(busy).Should().Be(Schemas(plain));
    }

    [Fact]
    public void Пропорции_и_ступени_моделей_в_схему_не_попадают_а_quality_это_статичный_enum()
    {
        using var w = new AgentWorld();
        var tools = JsonNode.Parse(Schemas(w))!.AsArray();

        foreach (var name in new[] { VideoEditorToolset.ToolNew, VideoEditorToolset.ToolSceneSet, VideoEditorToolset.ToolShoot })
        {
            var props = tools.Single(n => n!["name"]!.GetValue<string>() == name)!["schema"]!["properties"]!;
            props["aspect"]!.AsObject().ContainsKey("enum").Should().BeFalse($"{name}: пропорции модели — в video_state, а не в enum схемы");
            props["quality"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("standard", "high", "max");
        }
        Schemas(w).Should().NotContain("21:9").And.NotContain("27:16");
    }

    [Fact]
    public void Размер_частным_параметром_в_описании_params_не_предлагается()
    {
        using var w = new AgentWorld();
        var shoot = w.Toolset.ToolsFor(w.Ctx()).Single(t => t.Name == VideoEditorToolset.ToolShoot);

        shoot.InputSchema["properties"]!["params"]!["description"]!.GetValue<string>().Should().NotContain("resolution");
    }
}
