using System.ComponentModel;
using System.Diagnostics;

namespace ClaudeHomeServer.DeviceAgent.Processes;

/// <summary>Что запустить: путь к бинарю берётся только из аренды управляемой копии.</summary>
internal sealed record TurnLaunch(
    string ExecutablePath,
    IReadOnlyList<string> Args,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// Процесс хода вместе с деревом потомков (у CLI дети — MCP-клиенты, Bash).
/// Убивается всегда целиком: Unix — группой процессов, Windows — Job Object'ом.
/// </summary>
internal abstract class TurnProcess : IDisposable
{
    protected TurnProcess(Process process) => Process = process;

    public Process Process { get; }

    public int Id => Process.Id;

    public Stream StandardInput => Process.StandardInput.BaseStream;
    public Stream StandardOutput => Process.StandardOutput.BaseStream;
    public Stream StandardError => Process.StandardError.BaseStream;

    /// <summary>Время старта — по нему журнал отличает свой процесс от чужого с тем же pid.</summary>
    public DateTime StartTimeUtc { get; protected init; }

    public Task WaitForExitAsync(CancellationToken ct = default) => Process.WaitForExitAsync(ct);

    public int ExitCode => Process.ExitCode;

    /// <summary>Убить процесс хода и всех его потомков. Идемпотентно.</summary>
    public abstract void KillTree();

    public virtual void Dispose() => Process.Dispose();

    public static TurnProcess Start(TurnLaunch launch) =>
        OperatingSystem.IsWindows() ? WindowsJobProcess.Start(launch) : UnixGroupProcess.Start(launch);

    protected static ProcessStartInfo BaseStartInfo(string fileName, TurnLaunch launch)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Окружение — ровно собранное агентом: унаследованное от агента стирается целиком
        psi.Environment.Clear();
        foreach (var (key, value) in launch.Environment) psi.Environment[key] = value;
        return psi;
    }

    protected static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return DateTime.UtcNow;
        }
    }

    // Запасной путь, когда основной механизм ОС не сработал
    protected void KillTreeFallback()
    {
        try { Process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}
