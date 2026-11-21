namespace ClaudeHomeServer.Services.Execution;

// Slice-юниты systemd в cgroupfs (только cgroup v2): путь slice по правилу дефиса и его
// заморозка. Каталог slice ищется у предков собственного cgroup процесса: у user-юнитов сервер
// живёт в app.slice, а ccs-agents.slice — в ccs.slice под тем же user@<uid>.service, uid в код
// не зашит. Любой сбой чтения — «не знаем»: IsFrozen отвечает false (fail-open, ход пойдёт как
// раньше и в худшем случае упадёт на старте, но не будет отказан на пустом месте).
public static class CgroupSlices
{
    public const string DefaultCgroupFile = "/proc/self/cgroup";
    public const string DefaultCgroupRoot = "/sys/fs/cgroup";

    // Путь slice в cgroupfs по правилу systemd: дефис — уровень иерархии,
    // «ccs-agents.slice» → «ccs.slice/ccs-agents.slice». Не похоже на имя slice — null
    public static string? SlicePath(string slice)
    {
        if (!slice.EndsWith(".slice", StringComparison.Ordinal)) return null;
        var name = slice[..^".slice".Length];
        if (name.Length == 0 || name.Contains('/') || name.Contains("..") || name.StartsWith('-') || name.EndsWith('-'))
            return null;
        var parts = name.Split('-');
        if (parts.Any(p => p.Length == 0)) return null;
        return string.Join('/', parts.Select((_, i) => string.Join('-', parts[..(i + 1)]) + ".slice"));
    }

    // Заморожен ли slice — сам или любой его предок (systemctl freeze родителя замораживает и
    // детей, а их собственный cgroup.freeze остаётся 0). Каталога самого slice может ещё не быть
    // (systemd создаёт его при первом scope) — тогда смотрим существующих предков: замороженный
    // родитель всё равно не даст создать в нём scope. Читаем cgroup.freeze (заказанное
    // состояние), а не frozen из cgroup.events: последнее у пустого cgroup ведёт себя по-разному
    // в разных ядрах, а заказ виден сразу после systemctl freeze.
    public static bool IsFrozen(string slice, string cgroupFile = DefaultCgroupFile, string cgroupRoot = DefaultCgroupRoot)
    {
        try
        {
            if (SlicePath(slice) is not { } rel) return false;
            if (!File.Exists(cgroupFile)) return false;
            var own = File.ReadLines(cgroupFile).FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal))?[3..].Trim();
            if (own is not { Length: > 0 } || own[0] != '/') return false;
            var root = Path.GetFullPath(cgroupRoot).TrimEnd('/');
            var start = Path.GetFullPath(root + own).TrimEnd('/');
            var top = rel.Split('/')[0];
            // Основание — ближайший предок своего cgroup, в котором есть верхний slice цепочки
            var basePath = Ancestors(start, root).FirstOrDefault(a => Directory.Exists(Path.Combine(a, top)));
            if (basePath is null) return false;
            for (var node = Path.Combine(basePath, rel);
                 node.Length > basePath.Length;
                 node = Path.GetDirectoryName(node) ?? basePath)
            {
                var freeze = Path.Combine(node, "cgroup.freeze");
                if (File.Exists(freeze) && File.ReadAllText(freeze).Trim() == "1") return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // Каталог и его предки до корня cgroupfs включительно
    internal static IEnumerable<string> Ancestors(string node, string root)
    {
        for (var cur = node; cur.Length >= root.Length && cur.StartsWith(root, StringComparison.Ordinal);
             cur = Path.GetDirectoryName(cur) ?? "")
        {
            yield return cur;
            if (cur.Length == root.Length) yield break;
        }
    }
}
