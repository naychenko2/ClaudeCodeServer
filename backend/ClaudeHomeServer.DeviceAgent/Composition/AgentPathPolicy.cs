using ClaudeHomeServer.Services.Files;

namespace ClaudeHomeServer.DeviceAgent.Composition;

/// <summary>Корни, разрешённые пользователем на машине. Боевой — <c>Hosting.AgentRootsStore</c>.</summary>
internal interface IAgentRoots
{
    IReadOnlyList<string> Roots { get; }
}

/// <summary>Потолки размера localhost-API (ADR-016 §5).</summary>
internal sealed record AgentLimits
{
    /// <summary>Чтение в память (текст, base64): как потолок документа на сервере.</summary>
    public long MaxReadBytes { get; init; } = FileService.MaxDocumentBytes;
    /// <summary>Запись одним запросом.</summary>
    public long MaxWriteBytes { get; init; } = FileService.MaxDocumentBytes;
    /// <summary>Отдача потоком: в память не читается, но и бесконечной быть не должна.</summary>
    public long MaxStreamBytes { get; init; } = 4L * 1024 * 1024 * 1024;
}

/// <summary>Отказ агента по пути: вне корней, симлинк наружу, абсолютный путь, потолок размера.</summary>
internal sealed class AgentPathRefusedException(string message) : UnauthorizedAccessException(message);

/// <summary>Файл больше потолка — отказ, а не частичная отдача.</summary>
internal sealed class AgentFileTooLargeException(string message) : IOException(message);

/// <summary>
/// Граница агента (ADR-016 §5, сторож G7): файлы — только под корнями, явно разрешёнными на
/// машине, по РЕАЛЬНОМУ пути. Лексической проверки (<c>SafePath.Join</c> вертикали Files) мало:
/// симлинк внутри проекта, указывающий наружу, она пропускает, а абсолютный путь на Linux
/// молча приклеивает к корню. Серверу агент не доверяет: корень проекта из билета сверяется
/// с разрешёнными корнями здесь же.
///
/// Реальный путь строится обходом по сегментам: каждый существующий сегмент, оказавшийся
/// ссылкой, заменяется своей целью (рекурсивно, с потолком глубины против петель). Сегменты,
/// которых ещё нет (создаваемый файл), дописываются как есть — их родитель уже проверен.
/// Остаток: проверка и открытие файла — два шага, подмена ссылки между ними (TOCTOU) не
/// закрыта; жёсткие ссылки по построению неотличимы от файла.
/// </summary>
internal sealed class AgentPathPolicy(IAgentRoots roots, AgentLimits? limits = null)
{
    private const int MaxLinkDepth = 40;

    public AgentLimits Limits { get; } = limits ?? new AgentLimits();

    public int RootCount => roots.Roots.Count;

    /// <summary>Сравнение путей по регистру ФС: Linux различает регистр, Windows и macOS — нет.</summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Реальный корень проекта — или отказ, если он не под разрешённым корнем машины.
    /// </summary>
    public string ProjectRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Path.IsPathFullyQualified(rootPath))
            throw new AgentPathRefusedException("Корень проекта должен быть абсолютным путём");
        var real = RealPath(rootPath);
        if (!Directory.Exists(real))
            throw new DirectoryNotFoundException("Папки проекта на этой машине нет");
        foreach (var allowed in roots.Roots)
        {
            string realAllowed;
            try { realAllowed = RealPath(allowed); }
            catch (IOException) { continue; }
            if (IsUnder(real, realAllowed)) return real;
        }
        throw new AgentPathRefusedException(
            "Папка проекта не под разрешёнными корнями этой машины: добавь её командой «ai-home-agent roots add <путь>»");
    }

    /// <summary>
    /// Относительный путь внутри реального корня проекта: не абсолютный, без «..», реальный
    /// путь не выходит за корень. Возвращает полный реальный путь.
    /// </summary>
    public string Resolve(string realRoot, string relativePath)
    {
        var rel = relativePath ?? "";
        if (rel.Length > 0 && (Path.IsPathRooted(rel) || rel[0] is '/' or '\\' || (rel.Length >= 2 && rel[1] == ':')))
            throw new AgentPathRefusedException("Абсолютный путь вместо пути внутри проекта");
        var segments = rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s == ".."))
            throw new AgentPathRefusedException("Путь с «..» за пределы проекта");

        var real = RealPath(segments.Length == 0 ? realRoot : Path.Combine([realRoot, .. segments]));
        if (!IsUnder(real, realRoot))
            throw new AgentPathRefusedException("Путь ведёт за пределы проекта (ссылка наружу)");
        return real;
    }

    /// <summary>Файл можно прочитать в память целиком.</summary>
    public void EnsureReadable(string fullPath) => EnsureSize(fullPath, Limits.MaxReadBytes);

    /// <summary>Файл можно отдать потоком.</summary>
    public void EnsureStreamable(string fullPath) => EnsureSize(fullPath, Limits.MaxStreamBytes);

    /// <summary>Запись такого объёма допустима.</summary>
    public void EnsureWritable(long bytes)
    {
        if (bytes > Limits.MaxWriteBytes)
            throw new AgentFileTooLargeException($"Запись больше потолка {Limits.MaxWriteBytes} байт");
    }

    /// <summary>
    /// Записи листинга без тех, что уводят за корень: ссылка наружу и всё под ней. Записи
    /// дерева идут родитель раньше детей, поэтому ушедшие наружу каталоги отсекают своих
    /// потомков по префиксу; <paramref name="checkEach"/> — для поиска, где родителей в
    /// выдаче нет и каждый результат проверяется целиком.
    /// </summary>
    public IReadOnlyList<FileEntry> Filter(string realRoot, IEnumerable<FileEntry> entries, bool checkEach = false)
    {
        var escaped = new List<string>();
        var result = new List<FileEntry>();
        foreach (var e in entries)
        {
            if (escaped.Any(prefix => e.Path.StartsWith(prefix, PathComparison))) continue;
            var full = Path.Combine(realRoot, e.Path);
            bool inside;
            try
            {
                inside = checkEach || IsLink(full) ? IsUnder(RealPath(full), realRoot) : true;
            }
            catch (IOException) { inside = false; }
            if (inside) result.Add(e);
            else if (e.IsDirectory) escaped.Add(e.Path.TrimEnd('/') + "/");
        }
        return result;
    }

    private static void EnsureSize(string fullPath, long max)
    {
        var info = new FileInfo(fullPath);
        if (info.Exists && info.Length > max)
            throw new AgentFileTooLargeException($"Файл больше потолка {max} байт");
    }

    private static bool IsLink(string path) => LinkTarget(path) is not null;

    // Цель ссылки без перехода по ней (readlink / точка повторного анализа); null — не ссылка
    // или пути нет. Висячая ссылка тоже ссылка: её цель проверяется, как у живой.
    private static string? LinkTarget(string path) =>
        new FileInfo(path).LinkTarget ?? new DirectoryInfo(path).LinkTarget;

    public static bool IsUnder(string path, string root)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (r.Length == 0) r = root; // корень ФС
        return path.Equals(r, PathComparison)
               || path.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Реальный путь: все существующие ссылки по дороге раскрыты.</summary>
    public static string RealPath(string path) => RealPath(Path.GetFullPath(path), 0);

    private static string RealPath(string full, int depth)
    {
        if (depth > MaxLinkDepth) throw new IOException("Слишком длинная цепочка ссылок");
        var root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            var target = LinkTarget(next);
            if (target is null)
            {
                // Несуществующий сегмент (создаваемый файл) и обычный — как есть
                current = next;
                continue;
            }
            var targetFull = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(current, target));
            var resolved = RealPath(targetFull, depth + 1);
            current = i == segments.Length - 1
                ? resolved
                : RealPath(Path.Combine([resolved, .. segments[(i + 1)..]]), depth + 1);
            return current;
        }
        return current;
    }
}
