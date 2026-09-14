namespace ClaudeHomeServer.Services;

// Нормализация абсолютного пути к канонической форме, используемой как ключ
// для всех per-rootPath сторов (граф кода, знания, рабочее дерево): `Path.GetFullPath`
// (разворачивает `..`, нормализует разделители под текущую ОС), затем отрезаем
// хвостовой сегмент-разделитель, чтобы `C:\foo\` и `C:\foo` давали одинаковый ключ;
// в конце — нижний регистр, иначе на Windows сравнение `/Foo` и `/foo` разъезжалось
// бы даже при прочих равных.
//
// Вынесен из `WorkspaceKnowledgeStore.NormalizePath` (Этап 3, волна 1): вертикали
// (CodeGraph, Knowledge) собираются в отдельные `.csproj`, и общий примитив должен
// жить в спинке `Core`, чтобы не тащить `Knowledge/WorkspaceKnowledgeStore` в каждый
// потребитель. Старый `WorkspaceKnowledgeStore.NormalizePath` оставлен тонким
// forwarding'ом — внешний код (`ProjectManager`, `SessionManager`, контроллеры) пока
// ссылается на привычное имя и переезд не блокирует.
public static class PathNormalizer
{
    public static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
}