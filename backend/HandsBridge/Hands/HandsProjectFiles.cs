using System.IO;
using ClaudeHomeServer.HandsBridge.Policy;

namespace ClaudeHomeServer.HandsBridge;

/// <summary>
/// Единственный путь записи файлов в проект моста (D2d «руки без UIA»): вся запись — только
/// через этот класс, прямой <c>File.WriteAllBytes</c>/<c>FileStream</c> в инструменте сторож
/// красит. Корень проекта задаёт агент (<c>--project-root</c>); без него запись недоступна.
/// <para>
/// <see cref="WriteNew"/> — только НОВЫЙ файл (<c>FileMode.CreateNew</c>): имя занято — следующее
/// свободное <c>.vN</c> (<see cref="HandsPolicy.NextFreeName"/>); путь не несёт точек соединения и
/// симлинков; за ход не больше <see cref="HandsPolicy.MaxFilesPerTurn"/> файлов; размер — в пределах
/// потолка <see cref="HandsPolicy.CheckOutputPath"/>. <see cref="AppendOwn"/> — дописывает только файл,
/// который этот же экземпляр создал <see cref="WriteNew"/> в этом ходу; чужой файл — отказ.
/// </para>
/// </summary>
internal sealed class HandsProjectFiles
{
    /// <summary>Без корня проекта (нет <c>--project-root</c>) запись недоступна.</summary>
    private const string NoRoot =
        "No project root (--project-root) is set: the file cannot be saved into the project.";

    private readonly string? _projectRoot;
    private int _filesWrittenThisTurn;
    private readonly HashSet<string> _ownFiles = new(StringComparer.OrdinalIgnoreCase);

    public HandsProjectFiles(string? projectRoot)
    {
        _projectRoot = projectRoot;
    }

    /// <summary>Корень проекта на устройстве; null — запись недоступна (без <c>--project-root</c>).</summary>
    public string? ProjectRoot => _projectRoot;

    /// <summary>Сколько новых файлов этот экземпляр (ход) уже создал.</summary>
    public int FilesWrittenThisTurn => _filesWrittenThisTurn;

    /// <summary>
    /// Новый файл: <c>FileMode.CreateNew</c>; имя занято — следующее свободное <c>.vN</c>.
    /// Возвращает (фактический относительный путь, причина отказа): путь null — отказ, причина —
    /// текст для модели.
    /// </summary>
    public (string? Path, string? Reason) WriteNew(string tool, string relativePath, byte[] bytes)
    {
        if (_projectRoot is null)
            return (null, NoRoot);

        var decision = HandsGate.Policy.CheckOutputPath(tool, _projectRoot, relativePath, bytes.Length,
            _filesWrittenThisTurn, appendOwn: false, ownFiles: null);
        if (!decision.Allowed)
            return (null, decision.Reason);

        var normalized = decision.Path!;
        var free = HandsPolicy.NextFreeName(normalized, n => File.Exists(ToFull(n)));
        var freeFull = ToFull(free);

        // Точки соединения и симлинки на всём пути: запись через ссылку может уйти за корень.
        var link = LinkOnPath(free);
        if (link is not null)
            return (null, link);

        Directory.CreateDirectory(Path.GetDirectoryName(freeFull)!);

        // FileMode.CreateNew: файл (или ссылка) уже есть — IOException, а не тихая перезапись.
        using (var stream = new FileStream(freeFull, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        _filesWrittenThisTurn++;
        _ownFiles.Add(free);
        return (free, null);
    }

    /// <summary>
    /// Дописывает <paramref name="bytes"/> в файл, который этот же экземпляр создал
    /// <see cref="WriteNew"/> в этом ходу; чужой файл — отказ. Возвращает (путь, причина отказа).
    /// </summary>
    public (string? Path, string? Reason) AppendOwn(string tool, string relativePath, byte[] bytes)
    {
        if (_projectRoot is null)
            return (null, NoRoot);

        var decision = HandsGate.Policy.CheckOutputPath(tool, _projectRoot, relativePath, bytes.Length,
            _filesWrittenThisTurn, appendOwn: true, ownFiles: _ownFiles);
        if (!decision.Allowed)
            return (null, decision.Reason);

        var normalized = decision.Path!;
        var full = ToFull(normalized);

        using (var stream = new FileStream(full, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        return (normalized, null);
    }

    /// <summary>Полный путь: корень + нормализованный относительный путь (оба разделителя → локальный).</summary>
    private string ToFull(string normalized) =>
        Path.Combine(_projectRoot!, normalized.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Точка соединения или симлинк на пути от корня до файла: запись через ссылку может уйти за
    /// корень проекта. Проверяет каждый существующий сегмент; null — ссылок нет.
    /// </summary>
    private string? LinkOnPath(string normalized)
    {
        var current = _projectRoot!;
        foreach (var segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                continue;
            if (IsLink(current))
                return $"'{segment}' is a symlink or a junction: hands do not write through links into the project.";
        }

        return null;
    }

    /// <summary>Каталог или файл — ссылка (симлинк/точка соединения); .NET читает оба по <c>LinkTarget</c>.</summary>
    private static bool IsLink(string path)
    {
        if (Directory.Exists(path))
            return new DirectoryInfo(path).LinkTarget is not null;
        if (File.Exists(path))
            return new FileInfo(path).LinkTarget is not null;
        return false;
    }
}
