namespace ClaudeHomeServer.Services.Llm;

// Источник файлов /sys и /proc для разбора железа: локальный диск (worker) или срез агента
// (ai, video). Пути — полные, со слэшем-разделителем; один и тот же разбор работает на обоих.
public interface ISysFileSource
{
    // Текст файла; null — файла нет или он не читается.
    string? ReadText(string path);

    // Есть ли файл или каталог по пути.
    bool Exists(string path);

    // Полные пути подкаталогов (как Directory.EnumerateDirectories).
    IEnumerable<string> EnumerateDirectories(string path);

    // Полные пути файлов каталога (как Directory.EnumerateFiles).
    IEnumerable<string> EnumerateFiles(string path);

    // Цель символической ссылки; null — это не ссылка или её нет.
    string? LinkTarget(string path);

    // Размер файловой системы точки монтирования: всего и свободно (байты).
    (long total, long free) FsSize(string mountPoint);
}

// Сегодняшнее поведение: прямое чтение диска, DriveInfo для размера ФС (подменяется в тестах).
public sealed class DiskFileSource : ISysFileSource
{
    private readonly Func<string, (long total, long free)> _fsSize;

    public DiskFileSource(Func<string, (long total, long free)>? fsSize = null)
    {
        _fsSize = fsSize ?? DefaultFsSize;
    }

    private static (long total, long free) DefaultFsSize(string mountPoint)
    {
        var d = new DriveInfo(mountPoint);
        return (d.TotalSize, d.TotalFreeSpace);
    }

    public string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);

    public IEnumerable<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path);

    public string? LinkTarget(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget; }
        catch { return null; }
    }

    public (long total, long free) FsSize(string mountPoint) => _fsSize(mountPoint);
}

// Неизменяемый срез: файлы (путь → текст), символические ссылки (путь → цель) и результаты
// statvfs (точка монтирования → всего/свободно). Каталоги выводятся из путей файлов и ссылок.
public sealed class SliceFileSource : ISysFileSource
{
    private readonly IReadOnlyDictionary<string, string> _files;
    private readonly IReadOnlyDictionary<string, string> _links;
    private readonly IReadOnlyDictionary<string, (long total, long free)> _statvfs;
    private readonly HashSet<string> _dirs = new(StringComparer.Ordinal);

    public SliceFileSource(
        IReadOnlyDictionary<string, string> files,
        IReadOnlyDictionary<string, string>? links = null,
        IReadOnlyDictionary<string, (long total, long free)>? statvfs = null)
    {
        _files = files;
        _links = links ?? new Dictionary<string, string>();
        _statvfs = statvfs ?? new Dictionary<string, (long total, long free)>();
        foreach (var p in _files.Keys.Concat(_links.Keys))
            AddParents(p);
    }

    private void AddParents(string path)
    {
        var i = path.LastIndexOf('/');
        while (i > 0)
        {
            path = path[..i];
            if (!_dirs.Add(path)) return;
            i = path.LastIndexOf('/');
        }
    }

    private static string Norm(string path) => path.Length > 1 ? path.TrimEnd('/') : path;

    public string? ReadText(string path) => _files.TryGetValue(path, out var t) ? t : null;

    public bool Exists(string path) => _files.ContainsKey(path) || _dirs.Contains(Norm(path));

    public IEnumerable<string> EnumerateDirectories(string path)
    {
        var prefix = Norm(path) + "/";
        return _dirs.Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && d.IndexOf('/', prefix.Length) < 0);
    }

    public IEnumerable<string> EnumerateFiles(string path)
    {
        var prefix = Norm(path) + "/";
        return _files.Keys.Concat(_links.Keys)
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.IndexOf('/', prefix.Length) < 0);
    }

    public string? LinkTarget(string path) => _links.TryGetValue(path, out var t) ? t : null;

    public (long total, long free) FsSize(string mountPoint) =>
        _statvfs.TryGetValue(mountPoint, out var v) ? v : (0, 0);
}
