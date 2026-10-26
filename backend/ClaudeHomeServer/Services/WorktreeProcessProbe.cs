namespace ClaudeHomeServer.Services;

// Снимок процессов машины: «держит ли кто-то это дерево». Процесс держит дерево, если его
// рабочий каталог внутри него либо в командной строке есть путь внутри (dotnet bin/x.dll,
// дев-сервер, MSBuild). Строится ОДИН раз за проход — обход /proc дорог.
public interface IWorktreeProcessProbe
{
    /// <summary>false — снимок не построен (не Linux / нет /proc): уборка fail-closed.</summary>
    bool Available { get; }
    bool IsBusy(string worktreePath);
}

public sealed class ProcWorktreeProcessProbe : IWorktreeProcessProbe
{
    private readonly List<(string? Cwd, string Cmdline)> _procs = [];

    public bool Available { get; }

    /// <summary>Процессов, чей cwd не удалось прочитать (чужой пользователь): остаточный риск.</summary>
    public int Unreadable { get; private set; }

    public ProcWorktreeProcessProbe(string procRoot = "/proc")
    {
        if (!Directory.Exists(procRoot)) return;
        Available = true;
        var self = Environment.ProcessId.ToString();
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            var pid = Path.GetFileName(dir);
            if (pid.Length == 0 || !pid.All(char.IsAsciiDigit) || pid == self) continue;
            string? cwd = null;
            try { cwd = new DirectoryInfo(Path.Combine(dir, "cwd")).LinkTarget; }
            catch (Exception) { /* процесс завершился или чужой */ }
            if (cwd is null) Unreadable++;

            var cmd = "";
            try { cmd = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' '); }
            catch (Exception) { /* то же */ }
            if (cwd is not null || cmd.Length > 0) _procs.Add((cwd, cmd));
        }
    }

    public bool IsBusy(string worktreePath) =>
        Matches(_procs, worktreePath);

    // Совпадение по границе компонента пути: «…/Server-a» не должен ловить «…/Server-ab».
    public static bool Matches(IEnumerable<(string? Cwd, string Cmdline)> procs, string worktreePath)
    {
        var root = worktreePath.TrimEnd('/');
        var prefix = root + "/";
        foreach (var (cwd, cmd) in procs)
        {
            if (cwd is not null && (cwd == root || cwd.StartsWith(prefix, StringComparison.Ordinal))) return true;
            if (cmd.Contains(prefix, StringComparison.Ordinal) || cmd.EndsWith(root, StringComparison.Ordinal)
                || cmd.Contains(root + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
