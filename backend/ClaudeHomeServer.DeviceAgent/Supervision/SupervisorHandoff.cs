using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClaudeHomeServer.DeviceAgent.Install;

namespace ClaudeHomeServer.DeviceAgent.Supervision;

/// <summary>Итог передачи эстафеты: <see cref="Accepted"/> — новый супервизор жив и ждёт замка, старому пора выйти.</summary>
internal sealed record HandoffResult(bool Accepted, string? Error = null)
{
    public static readonly HandoffResult Ok = new(true);
    public static HandoffResult Failed(string error) => new(false, error);
}

/// <summary>
/// Самообновление супервизора: поднять <c>supervise</c> из каталога другой версии. Зовётся
/// только между дочерними — когда <c>run</c> уже вышел, то есть ни одного хода нет.
/// </summary>
internal interface ISupervisorHandoff
{
    /// <summary>Linux при успехе не возвращается вовсе: процесс заменён новой версией на месте.</summary>
    Task<HandoffResult> HandOffAsync(string version, CancellationToken ct);
}

internal static class SupervisorHandoffs
{
    /// <summary>Аргумент нового супервизора: «прими эстафету у PID» — ждать замок, а не выходить сразу.</summary>
    public const string TakeoverArg = "--takeover";

    /// <summary>Сколько новый супервизор ждёт, пока старый отпустит замок.</summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(60);

    public static ISupervisorHandoff ForCurrentOs(AgentLayout layout) => OperatingSystem.IsWindows()
        ? new DetachedSupervisorHandoff(layout, new WindowsDetachedStarter())
        : new ExecSupervisorHandoff(layout, new ProcessCommandRunner());

    /// <summary>Разбор <c>supervise [--takeover PID]</c>; null — аргументы не те.</summary>
    public static (bool Ok, int? From) ParseArgs(string[] args) => args switch
    {
        [] => (true, null),
        [TakeoverArg, var pid] when int.TryParse(pid, out var from) && from > 0 => (true, from),
        _ => (false, null),
    };
}

/// <summary>
/// Эстафета отдельным процессом (Windows): новый <c>supervise --takeover PID</c> стартует
/// отсоединённо, пишет свой PID в <c>supervisor.handoff</c> и ждёт замок. Старый, увидев
/// файл, выходит и отпускает замок. Замок у одного процесса в каждый момент — двух
/// супервизоров нет; новый не ответил — старый остаётся на месте.
/// </summary>
internal sealed class DetachedSupervisorHandoff(
    AgentLayout layout, IDetachedStarter starter, TimeSpan? readyTimeout = null, TimeSpan? poll = null) : ISupervisorHandoff
{
    private readonly TimeSpan _readyTimeout = readyTimeout ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _poll = poll ?? TimeSpan.FromMilliseconds(200);

    public async Task<HandoffResult> HandOffAsync(string version, CancellationToken ct)
    {
        try { File.Delete(layout.SupervisorHandoffFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HandoffResult.Failed(e.Message); }

        var started = starter.Start(layout.ExeOf(version),
            [Autostarts.SupervisorCommand, SupervisorHandoffs.TakeoverArg, Environment.ProcessId.ToString()], layout.VersionDir(version));
        if (started.Status != DetachedStartStatus.Started)
            return HandoffResult.Failed($"новый супервизор не запустился: {started.Error ?? started.Status.ToString()}");

        var deadline = DateTime.UtcNow + _readyTimeout;
        while (!ct.IsCancellationRequested)
        {
            if (ReadyPid() == started.ProcessId) return HandoffResult.Ok;
            if (!IsAlive(started.ProcessId)) return HandoffResult.Failed($"новый супервизор pid={started.ProcessId} вышел, не приняв эстафету");
            if (DateTime.UtcNow >= deadline) break;
            try { await Task.Delay(_poll, ct); }
            catch (OperationCanceledException) { break; }
        }

        // Не ответил или нас гасят — лишний ждущий замка не нужен
        Kill(started.ProcessId);
        return HandoffResult.Failed(ct.IsCancellationRequested
            ? "остановка супервизора во время передачи эстафеты"
            : $"новый супервизор pid={started.ProcessId} не ответил за {_readyTimeout.TotalSeconds:0} с");
    }

    private int? ReadyPid()
    {
        try
        {
            return File.Exists(layout.SupervisorHandoffFile) && int.TryParse(File.ReadAllText(layout.SupervisorHandoffFile).Trim(), out var pid)
                ? pid
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private static void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>
/// Эстафета на месте (Linux): <c>execv</c> бинаря новой версии с тем же PID. Отдельный
/// процесс здесь не годится — unit <c>systemd --user</c> при выходе главного процесса гасит
/// всю свою cgroup вместе с преемником, а unit самообновление не переписывает. PID тот же —
/// <c>MainPID</c> unit и <c>supervisor.pid</c> остаются верными; замок (дескриптор с
/// O_CLOEXEC) снимается самим exec, новый образ берёт его заново — окна двух супервизоров
/// нет. Перед exec — проба <c>--version</c>: битый бинарь не заменит живой супервизор.
/// </summary>
internal sealed class ExecSupervisorHandoff(AgentLayout layout, ICommandRunner runner) : ISupervisorHandoff
{
    public async Task<HandoffResult> HandOffAsync(string version, CancellationToken ct)
    {
        var exe = layout.ExeOf(version);
        var (code, output) = await Task.Run(() => runner.Run(exe, ["--version"]), ct);
        if (code != 0) return HandoffResult.Failed($"проба «{exe} --version» не прошла (код {code}): {output}");
        if (OperatingSystem.IsWindows()) return HandoffResult.Failed("exec на месте — только Linux");

        Exec(exe);
        return HandoffResult.Failed($"execv {exe} не удался (errno {Marshal.GetLastPInvokeError()})");
    }

    [UnsupportedOSPlatform("windows")]
    private static void Exec(string exe)
    {
        // Рабочий каталог — как у запуска из автозапуска: каталог версии
        Directory.SetCurrentDirectory(Path.GetDirectoryName(exe)!);
        execv(exe, [exe, Autostarts.SupervisorCommand, null]);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int execv(string path, string?[] argv);
}
