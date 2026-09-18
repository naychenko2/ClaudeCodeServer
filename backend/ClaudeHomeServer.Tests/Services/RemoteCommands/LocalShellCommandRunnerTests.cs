using ClaudeHomeServer.Services;
using ClaudeHomeServer.Services.RemoteCommands;
using FluentAssertions;

namespace ClaudeHomeServer.Tests.Services.RemoteCommands;

// Живой раннер пульта на настоящих процессах. Команды подобраны так, чтобы работать и в
// cmd (Windows-разработка), и в bash (CI — ubuntu); пути строятся от Path.GetTempPath().
public class LocalShellCommandRunnerTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(),
        "remote_commands_" + Guid.NewGuid().ToString("N"));

    private readonly LocalShellCommandRunner _runner = new();

    public LocalShellCommandRunnerTests() => Directory.CreateDirectory(_workDir);

    // Долгая команда, которую придётся убивать по таймауту
    private static string LongCommand => OperatingSystem.IsWindows()
        ? "ping -n 30 127.0.0.1 >nul"
        : "sleep 30";

    // Печать текущей рабочей папки
    private static string PrintWorkDir => OperatingSystem.IsWindows() ? "cd" : "pwd";

    [Fact]
    public async Task RunAsync_ОтдаётВыводИКодВыхода_ИКопитЕгоВБуфер()
    {
        var sink = new OutputRingBuffer();

        var result = await _runner.RunAsync("echo remote-commands-ok", _workDir, 30, sink, CancellationToken.None);

        result.Outcome.Should().Be(ShellRunOutcome.Exited);
        result.ExitCode.Should().Be(0);
        result.Output.Should().Contain("remote-commands-ok");
        sink.GetAll().Should().Contain("remote-commands-ok");
    }

    [Fact]
    public async Task RunAsync_НенулевойКодДоезжаетКакЕсть()
    {
        var result = await _runner.RunAsync("exit 3", _workDir, 30, null, CancellationToken.None);

        result.Outcome.Should().Be(ShellRunOutcome.Exited);
        result.ExitCode.Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_КомандаИдётИзЗаданнойРабочейПапки()
    {
        var result = await _runner.RunAsync(PrintWorkDir, _workDir, 30, null, CancellationToken.None);

        result.Outcome.Should().Be(ShellRunOutcome.Exited);
        result.Output.Should().Contain(Path.GetFileName(_workDir));
    }

    [Fact]
    public async Task RunAsync_ЗависшаяКоманда_ПрерываетсяПоТаймауту()
    {
        var result = await _runner.RunAsync(LongCommand, _workDir, 1, null, CancellationToken.None);

        result.Outcome.Should().Be(ShellRunOutcome.Timeout, "по истечении таймаута дерево команды убивается");
        result.Failure.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Spawn_ПроцессЖивётПослеВозвратаИГаснетПоKillTree()
    {
        var spawn = _runner.Spawn(LongCommand, _workDir, new OutputRingBuffer());

        spawn.Failure.Should().BeNull();
        var proc = spawn.Process!;
        using (proc)
        {
            proc.HasExited.Should().BeFalse("спавн не ждёт выхода — процесс должен остаться живым");

            proc.KillTree();

            proc.HasExited.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Spawn_ПроцессЗавершившийсяСам_ВиденКакВышедший()
    {
        var spawn = _runner.Spawn("echo bye", _workDir, new OutputRingBuffer());

        var proc = spawn.Process!;
        using (proc)
        {
            // Ждём событие выхода, а не паузу: на слабом CI фиксированная задержка флакает
            await proc.WaitForExitAsync(CancellationToken.None);

            proc.HasExited.Should().BeTrue();
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* временный каталог — мусор не критичен */ }
    }
}
