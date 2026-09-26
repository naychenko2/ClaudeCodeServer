using System.Diagnostics;
using ClaudeHomeServer.DeviceAgent.Install;
using ClaudeHomeServer.DeviceAgent.Processes;
using ClaudeHomeServer.DeviceAgent.Supervision;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClaudeHomeServer.DeviceAgent.Tests.Supervision;

/// <summary>
/// Замороженный контракт «супервизор ↔ дочерний» на настоящих процессах. Дочерний — фикстура
/// «новой версии» (<c>agent-child-fixture</c>), знающая контракт только литералами: код 75,
/// аргумент <c>run</c>, файлы <c>active</c>/<c>previous</c>, переменные окружения. Супервизор
/// текущего кода обязан её поднять — так живёт ленивое обновление самого супервизора (Р7).
/// </summary>
public sealed class SupervisorContractTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("agent-contract-").FullName;

    public void Dispose()
    {
        foreach (var pidFile in Directory.EnumerateFiles(_root, "child.pid", SearchOption.AllDirectories))
            if (int.TryParse(File.ReadAllText(pidFile), out var pid)) KillQuietly(pid);
        for (var i = 0; i < 20; i++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(250); }
        }
    }

    [Fact]
    public async Task Супервизор_поднимает_дочерний_новой_версии_из_фикстуры()
    {
        var layout = new AgentLayout(_root);
        ChildFixture.Install(layout, "1.0.0", "switch:2.0.0");
        ChildFixture.Install(layout, "2.0.0", "healthy");
        layout.SetActive("1.0.0");

        using var stop = new CancellationTokenSource();
        var output = new List<string>();
        var supervisor = new AgentSupervisor(layout, new ProcessChildLauncher(line => { lock (output) output.Add(line); }),
            _ => { }, NullLogger.Instance, new SupervisorOptions { HealthyTimeout = TimeSpan.FromSeconds(30) });
        var run = supervisor.RunAsync(stop.Token);

        await WaitUntilAsync(() => layout.IsHealthy("2.0.0"), "новая версия не стала здоровой");
        var childPid = ChildFixture.Pid(layout, "2.0.0");
        stop.Cancel();
        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(0);

        ChildFixture.Argv(layout, "1.0.0").Should().Equal("run");
        ChildFixture.Argv(layout, "2.0.0").Should().Equal(["run"], "аргументы дочернего — часть контракта");
        File.ReadAllText(Path.Combine(layout.VersionDir("2.0.0"), "child-healthy-env.txt"))
            .Should().Be(layout.HealthyOf("2.0.0"), "маркер healthy — versions/{v}/healthy по переменной контракта");
        layout.ReadActive().Should().Be("2.0.0");
        layout.ReadPrevious().Should().Be("1.0.0");
        await WaitUntilAsync(() => !IsAlive(childPid), "остановка супервизора не погасила дочерний");
    }

    [Fact]
    public async Task Дочерний_гибнет_вместе_с_жёстко_убитым_супервизором()
    {
        // Настоящий `ai-home-agent supervise` из копии сборки в versions/{v}: корень установки он
        // выводит из места бинаря, как при запуске из автозапуска
        var layout = new AgentLayout(_root);
        CopyDirectory(AppContext.BaseDirectory, layout.VersionDir("0.9.0"));
        ChildFixture.Install(layout, "2.0.0", "healthy");
        layout.SetActive("2.0.0");

        var psi = new ProcessStartInfo(layout.ExeOf("0.9.0"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = layout.VersionDir("0.9.0"),
        };
        psi.ArgumentList.Add(Autostarts.SupervisorCommand);
        using var supervisor = Process.Start(psi)!;
        supervisor.OutputDataReceived += (_, _) => { };
        supervisor.ErrorDataReceived += (_, _) => { };
        supervisor.BeginOutputReadLine();
        supervisor.BeginErrorReadLine();

        await WaitUntilAsync(() => layout.IsHealthy("2.0.0"), "супервизор не поднял дочерний",
            () => File.Exists(Path.Combine(layout.LogDirectory, "supervisor.log")) ? File.ReadAllText(Path.Combine(layout.LogDirectory, "supervisor.log")) : "журнала нет");
        var childPid = ChildFixture.Pid(layout, "2.0.0");
        IsAlive(childPid).Should().BeTrue();

        // Только сам супервизор, не дерево: так его убивает диспетчер задач или OOM
        supervisor.Kill(entireProcessTree: false);
        // По хэндлу процесса, а не WaitForExitAsync: тот ждёт ещё и конца трубы вывода, а её
        // держит осиротевший дочерний — падение было бы не о том
        await WaitUntilAsync(() => supervisor.HasExited, "супервизор не умер после kill");

        await WaitUntilAsync(() => !IsAlive(childPid), "дочерний пережил супервизор — сирота");
    }

    [SkippableFact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task Windows_отсоединённый_запуск_стартует_или_честно_отказывает()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "CreateProcess с breakaway — ветка Windows");
        var layout = new AgentLayout(_root);
        ChildFixture.Install(layout, "1.0.0", "crash");

        var result = new WindowsDetachedStarter().Start(layout.ExeOf("1.0.0"), [Autostarts.SupervisorCommand], layout.VersionDir("1.0.0"));

        result.Status.Should().BeOneOf(DetachedStartStatus.Started, DetachedStartStatus.BreakawayDenied);
        if (result.Status == DetachedStartStatus.Started)
        {
            result.ProcessId.Should().BePositive();
            await WaitUntilAsync(() => File.Exists(Path.Combine(layout.VersionDir("1.0.0"), "child-argv.txt")), "процесс не стартовал");
            await WaitUntilAsync(() => ChildFixture.Argv(layout, "1.0.0").Length > 0, "argv не записан");
            ChildFixture.Argv(layout, "1.0.0").Should().Equal("supervise");
        }
    }

    [Fact]
    public void Командная_строка_Windows_берёт_пути_с_пробелами_в_кавычки()
    {
        WindowsCommandLine.Build(@"C:\Users\Иван Петров\AppData\Local\AiHomeAgent\versions\1.2.0\ai-home-agent.exe", ["supervise"])
            .Should().Be(@"""C:\Users\Иван Петров\AppData\Local\AiHomeAgent\versions\1.2.0\ai-home-agent.exe"" supervise");
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(to, "ai-home-agent"), ChildFixture.Executable);
    }

    internal static bool IsAlive(int pid)
    {
        if (!OperatingSystem.IsWindows()) return UnixGroupProcess.IsAlive(pid);
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static void KillQuietly(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string because, Func<string>? diagnostics = null)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        condition().Should().BeTrue(because + (diagnostics is null ? "" : "; " + diagnostics()));
    }
}

/// <summary>Фикстура «дочерний новой версии» под именем ai-home-agent в versions/{v}/.</summary>
internal static class ChildFixture
{
    public const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const string Name = "agent-child-fixture";

    public static void Install(AgentLayout layout, string version, string mode)
    {
        var dir = layout.VersionDir(version);
        Directory.CreateDirectory(dir);
        var bin = AppContext.BaseDirectory;
        // apphost ищет свою сборку по вшитому имени рядом с собой — переименовывается только exe
        File.Copy(Path.Combine(bin, OperatingSystem.IsWindows() ? Name + ".exe" : Name), layout.ExeOf(version), overwrite: true);
        foreach (var suffix in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
            File.Copy(Path.Combine(bin, Name + suffix), Path.Combine(dir, Name + suffix), overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(layout.ExeOf(version), Executable);
        File.WriteAllText(Path.Combine(dir, "fixture-mode"), mode);
    }

    public static int Pid(AgentLayout layout, string version) =>
        int.Parse(File.ReadAllText(Path.Combine(layout.VersionDir(version), "child.pid")).Trim());

    public static string[] Argv(AgentLayout layout, string version)
    {
        var file = Path.Combine(layout.VersionDir(version), "child-argv.txt");
        if (!File.Exists(file)) return [];
        var text = File.ReadAllText(file);
        return text.Length == 0 ? [] : text.Split('\n');
    }
}
