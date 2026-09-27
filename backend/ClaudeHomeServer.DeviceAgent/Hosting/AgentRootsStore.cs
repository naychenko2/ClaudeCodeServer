using System.Text.Json;
using ClaudeHomeServer.DeviceAgent.Composition;
using ClaudeHomeServer.Protocol;

namespace ClaudeHomeServer.DeviceAgent.Hosting;

/// <summary>
/// Корни, явно разрешённые пользователем на этой машине (ADR-016 §5): файл
/// <c>roots.json</c> в конфиге агента, правится командой <c>ai-home-agent roots</c>, а не
/// сервером — серверной проверке агент не доверяет. Файл перечитывается по времени правки,
/// так что добавленный корень работает без перезапуска агента.
///
/// Автовыдача папок проектов (решение владельца 2026-09-27): подписи «добавлен автоматически
/// для проекта «…»» лежат отдельным файлом <c>roots-labels.json</c>, а не в <c>roots.json</c> —
/// формат корней не меняется, и откат агента не превратит их в «ни одного корня». Выключатель
/// автовыдачи — файл-маркер <c>roots-auto-off</c>: ставит и снимает его только команда
/// <c>roots auto on|off</c> на машине, в протоколе с сервером такой операции нет.
/// </summary>
internal sealed class AgentRootsStore(string file) : IAgentRoots
{
    private string LabelsFile => Path.Combine(Path.GetDirectoryName(file) ?? "", "roots-labels.json");
    private string AutoOffFile => Path.Combine(Path.GetDirectoryName(file) ?? "", "roots-auto-off");

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
        var labels = LoadLabels();
        if (labels.Remove(full)) SaveLabels(labels);
        return removed;
    }

    /// <summary>Автовыдача папок проектов включена (по умолчанию — да).</summary>
    public bool AutoEnabled => !File.Exists(AutoOffFile);

    /// <summary>Выключатель автовыдачи: только команда на машине.</summary>
    public void SetAuto(bool enabled)
    {
        if (enabled) File.Delete(AutoOffFile);
        else File.WriteAllText(AutoOffFile, "Автовыдача папок проектов выключена командой «ai-home-agent roots auto off»\n");
    }

    /// <summary>
    /// Корень, выданный автоматически под проект: как <see cref="Add"/> (общий на запись каталог —
    /// отказ, без --force), плюс подпись для <c>roots list</c>. Имя проекта — только подпись.
    /// </summary>
    public void AddAuto(string path, string? projectName)
    {
        Add(path);
        var labels = LoadLabels();
        labels[Path.GetFullPath(path)] = $"добавлен автоматически для проекта «{DisplayName(projectName)}»";
        SaveLabels(labels);
    }

    /// <summary>Подпись корня для <c>roots list</c>; null — добавлен вручную.</summary>
    public string? LabelOf(string root) => LoadLabels().GetValueOrDefault(root);

    // Имя приходит с сервера: без управляющих символов и с потолком длины
    private static string DisplayName(string? name)
    {
        var clean = new string((name ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > BindFolderProtocol.MaxProjectNameLength)
            clean = clean[..BindFolderProtocol.MaxProjectNameLength];
        return clean.Length == 0 ? "без имени" : clean;
    }

    private Dictionary<string, string> LoadLabels()
    {
        var labels = new Dictionary<string, string>(AgentPathPolicy.PathComparer);
        if (!File.Exists(LabelsFile)) return labels;
        try
        {
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LabelsFile)) ?? [])
                labels[k] = v;
        }
        // Битые подписи — только без подписей: на корни это не влияет
        catch (JsonException) { }
        return labels;
    }

    private void SaveLabels(Dictionary<string, string> labels)
    {
        var tmp = LabelsFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(labels, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, LabelsFile, overwrite: true);
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
