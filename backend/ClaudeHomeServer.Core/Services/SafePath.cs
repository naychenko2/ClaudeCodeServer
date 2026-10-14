namespace ClaudeHomeServer.Services;

// Защита от path traversal — единственный примитив спинки, на котором держится
// граница проекта. ВАЖНО: второй аргумент — путь ОТНОСИТЕЛЬНО корня; ведущие
// разделители срезаются. Абсолютный путь сюда передавать нельзя: на Linux «/a/b»
// станет относительным «a/b» и приклеится к корню — вместо отказа получится
// путь внутри проекта, то есть проверка «ссылка наружу» молча исчезнет. На Windows
// подмена незаметна (Path.Combine отдаёт приоритет второму абсолютному пути),
// поэтому такое ловится только в CI на Linux. Есть абсолютный путь — сначала
// Path.GetRelativePath(root, full).
//
// Вынесен из `FileService.SafeJoin` (Этап 3, уборка Git): вертикали (Git, Team,
// Tasks, Llm, Notes, ProjectServices, Mcp, Reader, Backgrounds, Changelog и др.)
// тянут примитив безопасности путей из чужой вертикали через цепочку namespace,
// минуя `FileService` — это «вертикаль → спинка», по прецеденту `PathNormalizer`/
// `Slugifier`/`ExecutableResolver`. `FileService.SafeJoin`/`SafeJoinPublic` остаются
// тонкими форвардерами — у `SafeJoin` десятки вызывающих по всему Main, и смена
// имени там сломала бы половину тестов.
//
// Покрытие граничных случаев (`..`, абсолютный путь, символы диска, регистр)
// живёт в `FileServiceTests` и запускается на обоих форвардерах.
//
// Регистр сравнивается по правилам ФС хоста (S8 ревью F2-15): на Linux «/tmp/x/proj» —
// другой каталог, чем «/tmp/x/Proj», и `OrdinalIgnoreCase` пропускал бы «../proj/a»
// как путь внутри корня. На Windows ФС регистронезависима — там игнор регистра верен.
public static class SafePath
{
    public static string Join(string root, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, relativePath.TrimStart('/', '\\')));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = ComparisonFor(OperatingSystem.IsWindows());
        // Сравнение с разделителем на конце: иначе root "C:\Data\Proj" пропускает "C:\Data\Proj2\..."
        if (!full.Equals(rootFull, comparison) &&
            !full.StartsWith(rootFull + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("Доступ за пределы проекта запрещён");
        return full;
    }

    // Выбор компаратора вынесен ради теста: ветку чужой ОС иначе на CI не проверить.
    public static StringComparison ComparisonFor(bool isWindows) =>
        isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}