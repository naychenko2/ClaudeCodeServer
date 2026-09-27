using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ClaudeHomeServer.DeviceAgent.Processes;

/// <summary>
/// Linux/macOS: CLI стартует через <c>setsid</c> в собственной сессии и группе процессов
/// (PGID = PID CLI), kill бьёт <c>-PGID</c> — вместе с MCP-клиентами и Bash.
///
/// <c>setsid</c> без <c>-f</c> не форкается, если вызывающий не лидер группы (дочерний
/// процесс .NET им не является): он делает setsid() и exec — PID процесса и есть PID CLI.
///
/// Смерть самого агента: PR_SET_PDEATHSIG через <c>setsid</c> не поставить, поэтому
/// живые группы пишутся в журнал (<see cref="TurnJournal"/>) и добиваются при старте
/// агента; при штатной остановке агент гасит их сам.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixGroupProcess : TurnProcess
{
    private const int SIGKILL = 9;
    private const int ESRCH = 3;

    /// <summary>Где искать setsid: util-linux на Linux, Homebrew util-linux на macOS.</summary>
    internal static readonly IReadOnlyList<string> SetsidCandidates =
        ["/usr/bin/setsid", "/bin/setsid", "/usr/local/bin/setsid", "/opt/homebrew/bin/setsid", "/opt/homebrew/opt/util-linux/bin/setsid"];

    private UnixGroupProcess(Process process) : base(process)
    {
        StartTimeUtc = SafeStartTime(process);
    }

    public static string? FindSetsid() => SetsidCandidates.FirstOrDefault(File.Exists);

    public static new UnixGroupProcess Start(TurnLaunch launch)
    {
        var setsid = FindSetsid() ?? throw new InvalidOperationException(
            OperatingSystem.IsMacOS()
                ? "на macOS нет setsid: поставь util-linux (brew install util-linux) — без своей группы процессов ход нельзя убить целиком"
                : "не найден setsid (util-linux): без своей группы процессов ход нельзя убить целиком");

        var psi = BaseStartInfo(setsid, launch);
        psi.ArgumentList.Add(launch.ExecutablePath);
        foreach (var arg in launch.Args) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("процесс CLI не запустился");
        return new UnixGroupProcess(process);
    }

    public override void KillTree() => KillGroup(Id, leaderAlive: () => !Process.HasExited);

    /// <summary>
    /// SIGKILL всей группе. Группы ещё нет (гонка со стартом setsid) — самому лидеру, но
    /// только пока он жив: pid пожатого процесса может достаться чужому.
    /// </summary>
    public static void KillGroup(int pgid, Func<bool> leaderAlive)
    {
        if (kill(-pgid, SIGKILL) == 0) return;
        if (Marshal.GetLastPInvokeError() == ESRCH && leaderAlive()) kill(pgid, SIGKILL);
    }

    /// <summary>Группа процесса (для тестов и проверки, что setsid отработал).</summary>
    public static int GroupOf(int pid) => getpgid(pid);

    /// <summary>Жив ли процесс: зомби считаются мёртвыми (их уже не убить, их только пожинают).</summary>
    public static bool IsAlive(int pid)
    {
        if (kill(pid, 0) != 0) return Marshal.GetLastPInvokeError() != ESRCH;
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var afterName = stat[(stat.LastIndexOf(')') + 2)..];
            return afterName.Length > 0 && afterName[0] is not ('Z' or 'X');
        }
        catch (IOException) { return false; }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);
}
