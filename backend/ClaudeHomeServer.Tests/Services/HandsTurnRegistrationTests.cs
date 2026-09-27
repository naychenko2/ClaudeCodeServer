using System.Text;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

/// <summary>
/// Окно приёма донесений о руках (дефект 3d761fc8): агент шлёт <c>active</c> раньше первого
/// кадра хода, а итог — после кадра Exit. Регистрация хода обязана накрывать оба края, а
/// донесение по чужому ходу — по-прежнему не приниматься.
/// </summary>
public class HandsTurnRegistrationTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private const string Device = "dev-1";
    private readonly string _owner = "owner-" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "hands-reg-" + Guid.NewGuid().ToString("N"));

    public HandsTurnRegistrationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private ProcessSpec HandsSpec(string turnId)
    {
        var mcp = Path.Combine(_tmp, "claude-mcp-" + turnId + ".json");
        File.WriteAllText(mcp, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [DeviceExecPlaceholders.HandsServerName] = new JsonObject { ["type"] = DeviceExecPlaceholders.Hands },
            },
        }.ToJsonString());
        return new ProcessSpec
        {
            FileName = "claude",
            Args = ["--print", "--mcp-config", mcp],
            WorkingDirectory = "/home/device-user/project",
            RedirectStdin = true,
            StdioEncoding = new UTF8Encoding(false),
            EnableRaisingEvents = true,
            TurnId = turnId,
            SessionId = "chat-1",
        };
    }

    [Fact]
    public async Task Active_до_вердикта_и_итог_после_Exit_принимаются()
    {
        string? atSpawn = null;
        var channel = new InProcessDeviceExecChannel
        {
            Agent = async s =>
            {
                await s.FromServer.ReadAsync();
                // Ровно как агент: «active» уходит в хаб раньше, чем сервер получит первый кадр хода
                atSpawn = DeviceHandsTurns.SessionOf(_owner, Device, "turn-race");
                await s.DeviceSendLineAsync("""{"type":"result","subtype":"success"}""");
                await s.DeviceExitAsync(0);
            },
        };
        var runner = new RemoteProcessRunner(channel, new FakeDeviceTurnGateway(), _owner, Device);

        var p = runner.Start(HandsSpec("turn-race"));
        await p.WaitForExitAsync().WaitAsync(Wait);
        await EndedAsync("turn-race");

        atSpawn.Should().Be("chat-1", "донесение «active» обгоняет вердикт агента — ход уже зарегистрирован");
        DeviceHandsTurns.SessionOf(_owner, Device, "turn-race").Should().Be("chat-1",
            "итог о руках агент шлёт после кадра Exit — окно донесений ещё открыто");
        DeviceHandsTurns.SessionOf(_owner, Device, "turn-race", DateTimeOffset.UtcNow + DeviceHandsTurns.EndGrace + TimeSpan.FromSeconds(1))
            .Should().BeNull("окно донесений закрытого хода не вечное");
        DeviceHandsTurns.SessionOf(_owner, "dev-2", "turn-race").Should().BeNull("чужое устройство");
        DeviceHandsTurns.SessionOf(_owner, Device, "turn-other").Should().BeNull("чужой ход");
    }

    [Fact]
    public void Отказ_агента_снимает_регистрацию_сразу()
    {
        var channel = new InProcessDeviceExecChannel
        {
            Agent = async s =>
            {
                await s.FromServer.ReadAsync();
                await s.DeviceRefuseAsync(HandsMachineLock.BusyText, HandsEndReason.Busy);
            },
        };
        var runner = new RemoteProcessRunner(channel, new FakeDeviceTurnGateway(), _owner, Device);

        var act = () => runner.Start(HandsSpec("turn-busy"));

        act.Should().Throw<DeviceExecRefusedException>();
        DeviceHandsTurns.SessionOf(_owner, Device, "turn-busy").Should().BeNull();
    }

    // Конец исполнения ход закрывает, а не снимает: ждём именно этого перехода
    private async Task EndedAsync(string turnId)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DeviceHandsTurns.IsLive(_owner, Device, turnId))
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "исполнение хода обязано закрыться после Exit");
            await Task.Delay(20);
        }
    }
}
