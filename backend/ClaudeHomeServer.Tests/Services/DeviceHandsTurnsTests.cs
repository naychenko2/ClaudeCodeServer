using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>Реестр ходов с руками: донесения — только по своему ходу, бейдж — по последнему донесению.</summary>
public class DeviceHandsTurnsTests
{
    private readonly string _owner = "owner-" + Guid.NewGuid().ToString("N")[..6];
    private const string Device = "dev-1";

    [Fact]
    public void Бейдж_следует_за_донесениями_своего_чата()
    {
        DeviceHandsTurns.Register(_owner, Device, "t1", "chat-1");
        DeviceHandsTurns.ChatStateOf(_owner, Device, "chat-1").Should().Be((HandsChatStates.Allowed, (string?)null));

        DeviceHandsTurns.SetLast(_owner, Device, new("chat-1", "t1", HandsChatStates.Active, null, DateTimeOffset.UtcNow));
        DeviceHandsTurns.ChatStateOf(_owner, Device, "chat-1").State.Should().Be(HandsChatStates.Active);
        DeviceHandsTurns.ChatStateOf(_owner, Device, "chat-2").State.Should().Be(HandsChatStates.Allowed, "чужой чат руками не действует");

        // Ход погас без донесения (связь оборвалась) — «действует руками» не залипает
        DeviceHandsTurns.Remove(_owner, Device, "t1");
        DeviceHandsTurns.ChatStateOf(_owner, Device, "chat-1").State.Should().Be(HandsChatStates.Allowed);

        DeviceHandsTurns.SetLast(_owner, Device, new("chat-1", "t1", HandsChatStates.Stopped, HandsEndReason.StoppedFromTray, DateTimeOffset.UtcNow));
        DeviceHandsTurns.ChatStateOf(_owner, Device, "chat-1").Should().Be((HandsChatStates.Stopped, HandsEndReason.StoppedFromTray));
    }

    [Fact]
    public void Ход_с_руками_узнаётся_по_маркеру_в_spawn()
    {
        var with = new DeviceExecSpawn("claude", [], "/w", new Dictionary<string, string>(),
            [new DeviceExecFile("f1", "mcp.json", RemoteProcessRunner.SanitizeMcpConfig(new System.Text.Json.Nodes.JsonObject
            {
                ["mcpServers"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["hands"] = new System.Text.Json.Nodes.JsonObject { ["type"] = DeviceExecPlaceholders.Hands, ["vision"] = true },
                },
            }.ToJsonString()))], true);
        var without = with with { Files = [new DeviceExecFile("f1", "mcp.json", """{"mcpServers":{}}""")] };

        RemoteProcessRunner.HasHandsMarker(with).Should().BeTrue();
        RemoteProcessRunner.HasHandsMarker(without).Should().BeFalse();
    }
}
