namespace ClaudeHomeServer.Services.ImageEditor;

// Граница проекта для путей редактора картинок (ADR-017, раздел 8). SafePath.Join
// сравнивает строки и символическую ссылку наружу не видит: «refs» → /etc внутри проекта
// лексически лежит в корне. Поэтому ни один существующий сегмент пути ниже корня не может
// быть ссылкой — ни каталог, ни сам файл. Ссылки внутрь проекта тоже отсекаются: отличать
// их дороже (цепочки ссылок, ссылки в целевом пути), а редактору они не нужны. Сам корень
// не проверяется: проект вправе лежать под ссылкой.
public static class ProjectLinkGuard
{
    // Путь из запроса — только относительный: SafePath срезает ведущий «/» и приклеил бы
    // «/etc/passwd» к корню вместо отказа. null — путь вне проекта или идёт через ссылку.
    public static string? ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
            return null;
        string full;
        try { full = SafePath.Join(root, relativePath); }
        catch (UnauthorizedAccessException) { return null; }
        return HasLink(root, full) ? null : full;
    }

    // Бросает то же исключение, что SafePath.Join, — вызывающие уже переводят его в «вне проекта»
    public static void EnsureNoLink(string root, string fullPath)
    {
        if (HasLink(root, fullPath))
            throw new UnauthorizedAccessException("Путь в проекте идёт через символическую ссылку");
    }

    // fullPath уже прошёл SafePath.Join, то есть лежит в корне лексически
    public static bool HasLink(string root, string fullPath)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = rootFull;
        var rest = Path.GetRelativePath(rootFull, Path.GetFullPath(fullPath));
        if (rest == ".") return false;
        foreach (var segment in rest.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            // Висячая ссылка не Exists, но LinkTarget у неё есть — её тоже ловим
            if (info.LinkTarget is not null) return true;
            if (!info.Exists) return false;
        }
        return false;
    }
}
