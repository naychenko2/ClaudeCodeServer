using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;

namespace ClaudeHomeServer.DeviceAgent.Hosting;

/// <summary>
/// Корни, явно разрешённые пользователем на этой машине (ADR-016 §5): файл
/// <c>roots.json</c> в конфиге агента, правится командой <c>ai-home-agent roots</c>, а не
/// сервером — серверной проверке агент не доверяет. Файл перечитывается по времени правки,
/// так что добавленный корень работает без перезапуска агента.
/// </summary>
internal sealed class AgentRootsStore(string file) : IAgentRoots
{
    private readonly Lock _lock = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private IReadOnlyList<string> _roots = [];

    public IReadOnlyList<string> Roots
    {
        get
        {
            lock (_lock)
            {
                var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                if (stamp != _loadedStamp)
                {
                    _roots = Load();
                    _loadedStamp = stamp;
                }
                return _roots;
            }
        }
    }

    public void Add(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Каталога нет: {full}");
        var roots = Load().ToList();
        if (!roots.Contains(full, AgentPathPolicy.PathComparer)) roots.Add(full);
        Save(roots);
    }

    public bool Remove(string path)
    {
        var full = Path.GetFullPath(path);
        var roots = Load().ToList();
        var removed = roots.RemoveAll(r => AgentPathPolicy.PathComparer.Equals(r, full)) > 0;
        if (removed) Save(roots);
        return removed;
    }

    private IReadOnlyList<string> Load()
    {
        if (!File.Exists(file)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(File.ReadAllText(file)) ?? [])
                .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
                .ToList();
        }
        // Битый файл — ни одного корня: закрыто по умолчанию, а не «всё разрешено»
        catch (JsonException) { return []; }
    }

    private void Save(IReadOnlyList<string> roots)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(roots, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, overwrite: true);
    }
}
