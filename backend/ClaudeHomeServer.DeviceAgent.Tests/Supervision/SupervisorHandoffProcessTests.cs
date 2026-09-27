using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeHomeServer.DeviceAgent.Install;
using ClaudeHomeServer.DeviceAgent.Supervision;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>
/// Эстафета супервизора на настоящих процессах: настоящий <c>ai-home-agent supervise</c> из
/// копии сборки в <c>versions/{v}</c>. Каталоги пользователя (XDG) уведены во временные —
/// дочерний <c>run</c> без сопряжения выходит сразу и чужого агента не трогает.
/// </summary>
public sealed class SupervisorHandoffProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agent-handoff-").FullName;
    private readonly string _home = Directory.CreateTempSubdirectory("agent-handoff-home-").FullName;
    private readonly List<Process> _processes = [];

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        foreach (var dir in new[] { _root, _home })
            for (var i = 0; i < 20; i++)
            {
                try { Directory.Delete(dir, recursive: true); break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
            }
    }

    [Fact]
    public async Task Новый_супервизор_ждёт_замок_прежнего_и_берёт_его_только_после_выхода()
    {
        var layout = new AgentLayout(_root);
        InstallAgent(layout, "2.0.0");

        // Прежний супервизор — сам тест: держит замок, пока эстафета не принята
        var old = SupervisorLock.TryAcquire(layout)!;
        var result = await new DetachedSupervisorHandoff(layout, new ProcessStarter(this))
            .HandOffAsync("2.0.0", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        result.Should().Be(HandoffResult.Ok, Log(layout));
        var successor = _processes.Single();
        await Task.Delay(1000);
        successor.HasExited.Should().BeFalse("принявший эстафету ждёт замок, а не выходит «уже работает»");
        File.ReadAllText(layout.SupervisorPidFile).Trim().Should().Be(Environment.ProcessId.ToString(),
            "пока прежний держит замок, новый не супервизор — двух одновременно нет");

        old.Dispose();

        await SupervisorContractTests.WaitUntilAsync(
            () => File.Exists(layout.SupervisorPidFile) && File.ReadAllText(layout.SupervisorPidFile).Trim() == successor.Id.ToString(),
            "новый супервизор не взял замок после выхода прежнего", () => Log(layout));
        Log(layout).Should().Contain("стартовал из").And.Contain(layout.VersionDir("2.0.0"));
        File.Exists(layout.SupervisorHandoffFile).Should().BeFalse("отметка эстафеты убирается, взяв замок");
        SupervisorLock.TryAcquire(layout).Should().BeNull("замок у нового супервизора");
    }

    [SkippableFact]
    public async Task Linux_супервизор_заменяет_себя_новой_версией_на_месте()
    {
        Skip.If(OperatingSystem.IsWindows(), "exec на месте — ветка Linux");
        var layout = new AgentLayout(_root);
        InstallAgent(layout, "1.0.0");
        InstallAgent(layout, "2.0.0");
        layout.RollbackTo("2.0.0");
        File.WriteAllText(layout.HealthyOf("2.0.0"), "ok");

        var supervisor = Start(layout.ExeOf("1.0.0"), [Autostarts.SupervisorCommand], layout.VersionDir("1.0.0"));

        await SupervisorContractTests.WaitUntilAsync(() => Log(layout).Contains("стартовал из " + layout.VersionDir("2.0.0")),
            "супервизор 1.0.0 не передал эстафету версии 2.0.0", () => Log(layout));
        supervisor.HasExited.Should().BeFalse();
        Log(layout).Should().Contain($"pid={supervisor.Id}] info: Супервизор", "тот же PID — MainPID unit systemd верен");
        File.ReadAllText(layout.SupervisorPidFile).Trim().Should().Be(supervisor.Id.ToString());
        SupervisorLock.TryAcquire(layout).Should().BeNull("замок взят новым образом заново");

        // Сигналы после exec живы: SIGTERM гасит супервизор, как systemctl stop
        kill(supervisor.Id, 15).Should().Be(0);
        await SupervisorContractTests.WaitUntilAsync(() => supervisor.HasExited, "SIGTERM не погасил супервизор после exec", () => Log(layout));
    }

    private static string Log(AgentLayout layout)
    {
        var file = Path.Combine(layout.LogDirectory, "supervisor.log");
        return File.Exists(file) ? File.ReadAllText(file) : "журнала нет";
    }

    private static void InstallAgent(AgentLayout layout, string version)
    {
        var from = AppContext.BaseDirectory;
        var to = layout.VersionDir(version);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(layout.ExeOf(version), ChildFixture.Executable);
    }

    private Process Start(string exe, IReadOnlyList<string> args, string workingDirectory)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(_home, "config");
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(_home, "data");
        var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _processes.Add(process);
        return process;
    }

    /// <summary>Отсоединённый запуск для теста: обычный процесс, чтобы тест его и прибрал.</summary>
    private sealed class ProcessStarter(SupervisorHandoffProcessTests owner) : IDetachedStarter
    {
        public DetachedStart Start(string executable, IReadOnlyList<string> args, string workingDirectory) =>
            new(DetachedStartStatus.Started, owner.Start(executable, args, workingDirectory).Id);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
