using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Protocol;
using ClaudeHomeServer.Services.Execution;
using ClaudeHomeServer.Tests.Helpers;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services;

// Петля раннер ↔ агент в одном процессе (ADR-016, задача 2.3): ClaudeSession держит
// ретранслятор обычным Process — stdin/stdout/stderr, код выхода, Exited и Kill по TurnId
// должны вести себя как у локального CLI. Агент здесь сценарный: отвечает на stream-json
// строки так, как ответил бы CLI.
public class RemoteProcessRunnerLoopTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private sealed class ScriptedAgent
    {
        public TaskCompletionSource<DeviceExecControl> Spawned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DeviceExecControl> Killed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> StdinLines { get; } = [];

        public async Task RunAsync(InProcessExecStream s)
        {
            var first = await s.FromServer.ReadAsync();
            Spawned.SetResult(DeviceExecJson.Deserialize<DeviceExecControl>(first.Payload.Span)!);
            await s.DeviceSendLineAsync("""{"type":"system","subtype":"init"}""");
            await s.DeviceSendAsync(DeviceExecFrameChannel.Stderr, Encoding.UTF8.GetBytes("agent-stderr\n"));

            var buf = new StringBuilder();
            await foreach (var frame in s.FromServer.ReadAllAsync())
            {
                switch (frame.Channel)
                {
                    case DeviceExecFrameChannel.Stdin:
                        buf.Append(Encoding.UTF8.GetString(frame.Payload.Span));
                        int nl;
                        while ((nl = buf.ToString().IndexOf('\n')) >= 0)
                        {
                            var line = buf.ToString(0, nl);
                            buf.Remove(0, nl + 1);
                            lock (StdinLines) StdinLines.Add(line);
                            await ReplyAsync(s, JsonNode.Parse(line)!);
                        }
                        break;
                    case DeviceExecFrameChannel.StdinEof:
                        await s.DeviceSendLineAsync("""{"type":"result","subtype":"success"}""");
                        await s.DeviceExitAsync(0);
                        return;
                    case DeviceExecFrameChannel.Control:
                        var control = DeviceExecJson.Deserialize<DeviceExecControl>(frame.Payload.Span)!;
                        if (control.Op == DeviceExecControlOps.Kill)
                        {
                            Killed.TrySetResult(control);
                            await s.DeviceExitAsync(null, "SIGKILL");
                            return;
                        }
                        break;
                }
            }
        }

        private static async Task ReplyAsync(InProcessExecStream s, JsonNode msg)
        {
            static string? S(JsonNode? n) => n?.GetValue<string>();
            switch (S(msg["type"]))
            {
                // Ход пользователя: CLI спрашивает разрешение на инструмент через
                // --permission-prompt-tool stdio и ждёт control_response на stdin
                case "user":
                    await s.DeviceSendLineAsync(
                        """{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Bash"}}""");
                    break;
                case "control_response" when S(msg["response"]?["request_id"]) == "perm-1":
                    var behavior = S(msg["response"]?["response"]?["behavior"]);
                    await s.DeviceSendLineAsync($$"""{"type":"assistant","permission":"{{behavior}}"}""");
                    break;
                case "control_request" when S(msg["request"]?["subtype"]) == "interrupt":
                    var id = S(msg["request_id"]);
                    await s.DeviceSendLineAsync(
                        $$$"""{"type":"control_response","response":{"subtype":"success","request_id":"{{{id}}}"}}""");
                    break;
            }
        }
    }

    private static ProcessSpec ClaudeSpec(string turnId, params string[] extraArgs) => new()
    {
        FileName = "claude",
        Args = ["--print", "--output-format", "stream-json", "--input-format", "stream-json",
                "--permission-prompt-tool", "stdio", .. extraArgs],
        WorkingDirectory = "/home/device-user/project",
        RedirectStdin = true,
        StdioEncoding = new UTF8Encoding(false),
        EnableRaisingEvents = true,
        TurnId = turnId,
    };

    private static (RemoteProcessRunner Runner, InProcessDeviceExecChannel Channel, ScriptedAgent Agent) Create(string owner = "owner-1")
    {
        var agent = new ScriptedAgent();
        var channel = new InProcessDeviceExecChannel { Agent = agent.RunAsync };
        return (new RemoteProcessRunner(channel, owner, "dev-1"), channel, agent);
    }

    private static async Task<string> ReadLineAsync(Process p)
    {
        var line = await p.StandardOutput.ReadLineAsync().WaitAsync(Wait);
        line.Should().NotBeNull("ретранслятор закрыл stdout раньше ожидаемой строки");
        return line!;
    }

    private static async Task WriteLineAsync(Process p, string line)
    {
        await p.StandardInput.WriteLineAsync(line);
        await p.StandardInput.FlushAsync();
    }

    [Fact]
    public async Task PermissionPromptStdio_И_Resume_ПроходятЧерезПетлю_КодВыходаДоходит()
    {
        var (runner, _, agent) = Create();
        var p = runner.Start(ClaudeSpec("turn-perm", "--resume", "cli-session-42"));
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.Exited += (_, _) => exited.TrySetResult();
        var stderr = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) stderr.TrySetResult(d); };
        p.BeginErrorReadLine();

        var spawn = await agent.Spawned.Task.WaitAsync(Wait);
        spawn.TurnId.Should().Be("turn-perm");
        spawn.Spawn!.Args.Should().ContainInOrder("--permission-prompt-tool", "stdio");
        spawn.Spawn.Args.Should().ContainInOrder("--resume", "cli-session-42");
        spawn.Spawn.WorkingDirectory.Should().Be("/home/device-user/project");

        (await ReadLineAsync(p)).Should().Contain("\"init\"");
        (await stderr.Task.WaitAsync(Wait)).Should().Be("agent-stderr");

        await WriteLineAsync(p, """{"type":"user","message":{"role":"user","content":"привет"}}""");
        var request = JsonNode.Parse(await ReadLineAsync(p))!;
        ((string?)request["request"]!["subtype"]).Should().Be("can_use_tool");

        await WriteLineAsync(p,
            """{"type":"control_response","response":{"subtype":"success","request_id":"perm-1","response":{"behavior":"allow"}}}""");
        (await ReadLineAsync(p)).Should().Contain("\"permission\":\"allow\"");

        p.StandardInput.Close();
        (await ReadLineAsync(p)).Should().Contain("\"result\"");
        await exited.Task.WaitAsync(Wait);
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().Be(0);
        lock (agent.StdinLines) agent.StdinLines[0].Should().Contain("привет", "UTF-8 проходит петлю без искажений");
    }

    [Fact]
    public async Task Interrupt_ЧерезStdin_ДоходитДоАгента_ОтветВозвращается()
    {
        var (runner, _, _) = Create();
        var p = runner.Start(ClaudeSpec("turn-int"));
        (await ReadLineAsync(p)).Should().Contain("\"init\"");

        await WriteLineAsync(p, """{"type":"control_request","request_id":"int-7","request":{"subtype":"interrupt"}}""");
        var response = JsonNode.Parse(await ReadLineAsync(p))!;
        ((string?)response["response"]!["request_id"]).Should().Be("int-7");
        ((string?)response["response"]!["subtype"]).Should().Be("success");

        p.StandardInput.Close();
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().Be(0);
    }

    [Fact]
    public async Task Kill_ПоTurnId_УбиваетХодНаУстройстве_ДажеСДругогоЭкземпляраРаннера()
    {
        var (runner, channel, agent) = Create("owner-kill");
        var p = runner.Start(ClaudeSpec("turn-kill"));
        (await ReadLineAsync(p)).Should().Contain("\"init\"");

        // ClaudeSession мог получить раннер заново от фабрики — ход находится по владельцу и
        // TurnId, а не по объекту процесса: передаём заведомо посторонний процесс
        var other = new RemoteProcessRunner(channel, "owner-kill", "dev-1");
        using var unrelated = Process.Start(new ProcessStartInfo("node", ["-e", "setTimeout(()=>{}, 60000)"]))!;
        other.Kill(unrelated, "turn-kill");

        var kill = await agent.Killed.Task.WaitAsync(Wait);
        kill.TurnId.Should().Be("turn-kill");
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().NotBe(0);
        channel.Opened.TryPeek(out var stream).Should().BeTrue();
        await stream!.Disposed.WaitAsync(Wait);
    }

    [Fact]
    public async Task Kill_ЧужойВладелец_ТотЖеTurnId_ХодНеТрогает()
    {
        var (runner, channel, agent) = Create("owner-a");
        var p = runner.Start(ClaudeSpec("turn-shared"));
        (await ReadLineAsync(p)).Should().Contain("\"init\"");

        using var stranger = Process.Start(new ProcessStartInfo("node", ["-e", "setTimeout(()=>{}, 60000)"]))!;
        new RemoteProcessRunner(channel, "owner-b", "dev-1").Kill(stranger, "turn-shared");

        // Kill отправляет кадр синхронно и раньше конца stdin: ошибись поиск — агент
        // получил бы kill первым и ход вышел бы по сигналу
        p.StandardInput.Close();
        await p.WaitForExitAsync().WaitAsync(Wait);
        p.ExitCode.Should().Be(0);
        agent.Killed.Task.IsCompleted.Should().BeFalse("TurnId ищется только среди ходов своего владельца");
    }

    [Fact]
    public async Task РетрансляторУмерБезКодаВыхода_ХодНаУстройствеУбивается()
    {
        var (runner, _, agent) = Create();
        var p = runner.Start(ClaudeSpec("turn-orphan"));
        (await ReadLineAsync(p)).Should().Contain("\"init\"");

        p.Kill(entireProcessTree: true);

        var kill = await agent.Killed.Task.WaitAsync(Wait);
        kill.TurnId.Should().Be("turn-orphan");
    }

    [Fact]
    public void Отказ_Устройства_ДоСтартаРетранслятора()
    {
        var channel = new InProcessDeviceExecChannel
        {
            Refuse = new DeviceExecRefusedException(DeviceExecRefusal.Offline, "Устройство не в сети"),
        };
        var runner = new RemoteProcessRunner(channel, "owner-1", "dev-1");

        var act = () => runner.Start(ClaudeSpec("turn-off"));

        act.Should().Throw<DeviceExecRefusedException>().Which.Reason.Should().Be(DeviceExecRefusal.Offline);
        channel.Opened.Should().BeEmpty();
    }

    [Fact]
    public void Раннер_ОбъявляетСредуУстройства()
    {
        var runner = new RemoteProcessRunner(
            new InProcessDeviceExecChannel { Platform = "windows" }, "owner-1", "dev-1");

        runner.IsSandboxed.Should().BeTrue();
        runner.TargetIsWindows.Should().BeTrue();
        runner.Paths.ToRuntime(@"C:\src\app").Should().Be(@"C:\src\app");
        runner.ClaudeCliCommand.Should().Be("claude");
        Directory.Exists(runner.HostTempDir).Should().BeTrue();
    }

    [Fact]
    public void RawArguments_НеПоддерживаются()
    {
        var runner = new RemoteProcessRunner(new InProcessDeviceExecChannel(), "owner-1", "dev-1");
        var act = () => runner.Start(new ProcessSpec { FileName = "cmd", RawArguments = "/s /c \"x\"" });
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void КадрSpawn_ЭтоJsonКонтракта()
    {
        var spawn = RemoteProcessRunner.BuildSpawn(ClaudeSpec("t"));
        var json = JsonSerializer.Serialize(new DeviceExecControl(DeviceExecControlOps.Spawn, "t", spawn), DeviceExecJson.Options);
        DeviceExecJson.Deserialize<DeviceExecControl>(Encoding.UTF8.GetBytes(json))!.Spawn!.Args
            .Should().Equal(spawn.Args);
    }
}
