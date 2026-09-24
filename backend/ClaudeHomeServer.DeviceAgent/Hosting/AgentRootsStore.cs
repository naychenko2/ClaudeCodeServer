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

    /// <summary>Предупреждение, которое <c>roots add</c> печатает всегда.</summary>
    public const string SharedWriteWarning =
        "Корень не должен лежать в каталоге, куда пишут другие пользователи машины: " +
        "жёсткую ссылку на свой файл они подложат внутрь, и агент её не отличит от файла проекта";

    /// <summary>
    /// Разрешить корень. Каталог, доступный на запись группе или всем (Unix), — только с
    /// <paramref name="force"/>: там чужая жёсткая ссылка неотличима от файла проекта
    /// (ревью 4.5, MAJOR #2). На Windows права — ACL, их здесь не разбираем: только
    /// предупреждение.
    /// </summary>
    public void Add(string path, bool force = false)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Каталога нет: {full}");
        if (!force && !OperatingSystem.IsWindows()
            && (File.GetUnixFileMode(full) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new SharedRootException(
                $"В {full} могут писать группа или все пользователи машины — корень не добавлен. " +
                "Сузь права (chmod go-w) или добавь с --force, если уверен");
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

/// <summary>Корень доступен на запись другим пользователям: без --force не добавляется.</summary>
internal sealed class SharedRootException(string message) : Exception(message);
