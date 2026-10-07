using System.Text.Json.Nodes;
using ClaudeHomeServer.Services.ChatContext;
using ClaudeHomeServer.Services.Media;
using ClaudeHomeServer.Services.VideoEditor.Contracts;
using FluentAssertions;
using Xunit;
using static ClaudeHomeServer.Services.VideoEditor.Mcp.VideoEditorToolset;

namespace ClaudeHomeServer.Services.VideoEditor.Tests.Mcp;

// video_prefs_changed уходит владельцу при ЛЮБОЙ записи префов: и ручкой человека, и форматом агента —
// иначе вторая вкладка не увидит смену
public sealed class VideoPrefsBroadcastTests
{
    private static VideoPrefsDto Wish(string aspect) =>
        new(null, null, null, aspect, null, null);

    [Fact]
    public void PUT_префов_человека_шлёт_событие_владельцу()
    {
        using var w = new AgentWorld();

        w.Rest.PutPrefs(w.ProjectId, Wish("9:16"));

        var call = w.F.Broadcaster.ToOwnerCalls.Should().ContainSingle(c => c.Message is VideoPrefsChangedMessage).Subject;
        call.OwnerId.Should().Be(w.Owner);
        var sent = (VideoPrefsChangedMessage)call.Message;
        sent.ScopeKey.Should().Be(w.F.Scope.Key);
        sent.Prefs.Aspect.Should().Be("9:16");
    }

    [Fact]
    public async Task Запись_формата_агентом_шлёт_событие_владельцу()
    {
        using var w = new AgentWorld();
        var scene = w.NewScene();

        (await w.Call(ToolShoot, new JsonObject { ["sceneId"] = scene.SceneId, ["aspect"] = "16:9", ["quality"] = "high" }))
            .IsError.Should().BeFalse();
        await w.WaitJobsAsync();

        var call = w.F.Broadcaster.ToOwnerCalls.Should().ContainSingle(c => c.Message is VideoPrefsChangedMessage).Subject;
        call.OwnerId.Should().Be(w.Owner);
        ((VideoPrefsChangedMessage)call.Message).Prefs.Format!.By.Should().Be(ContextActor.Agent);
    }
}
