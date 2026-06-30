namespace ClaudeHomeServer.Services;

// Память роли-собеседника: устойчивые факты о проекте и договорённости, переживающие
// отдельные чаты. Один markdown-файл на роль: data/role-memory/<roleId>.md.
// Наполняется двумя каналами (см. ClaudeSession): маркер [MEMORY] в ответах роли (реалтайм)
// и периодический авто-summary. Инжектится в системный промпт каждого чата с ролью.
public class RoleMemoryService
{
    private readonly string _memDir;
    private readonly Lock _lock = new();

    public RoleMemoryService(IConfiguration config)
    {
        var dataDir = Path.GetDirectoryName(
            config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json"))
            ?? Path.Combine(AppContext.BaseDirectory, "data");
        _memDir = Path.Combine(dataDir, "role-memory");
    }

    private string PathFor(string roleId) => Path.Combine(_memDir, roleId + ".md");

    public string Read(string roleId)
    {
        var p = PathFor(roleId);
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }

    // Дозапись новых фактов с дедупликацией по содержимому строки (регистронезависимо)
    public void Append(string roleId, IEnumerable<string> facts)
    {
        lock (_lock)
        {
            var existing = Read(roleId);
            var existingLines = existing
                .Split('\n')
                .Select(l => l.TrimStart('-', ' ', '\t').Trim())
                .Where(l => l.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toAdd = facts
                .Select(f => f.Trim())
                .Where(f => f.Length > 0 && !existingLines.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (toAdd.Count == 0) return;

            Directory.CreateDirectory(_memDir);
            var sb = new System.Text.StringBuilder(existing);
            if (existing.Length > 0 && !existing.EndsWith("\n")) sb.Append('\n');
            foreach (var f in toAdd) sb.Append("- ").Append(f).Append('\n');
            File.WriteAllText(PathFor(roleId), sb.ToString());
        }
    }

    // Полная перезапись (авто-summary компактит память; ручная правка из UI)
    public void Overwrite(string roleId, string content)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_memDir);
            File.WriteAllText(PathFor(roleId), content ?? "");
        }
    }

    public void Delete(string roleId)
    {
        lock (_lock)
        {
            var p = PathFor(roleId);
            if (File.Exists(p)) File.Delete(p);
        }
    }
}
