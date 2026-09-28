namespace ClaudeHomeServer.Services.Architecture;

/// <summary>
/// ЕДИНСТВЕННОЕ место правила свёртки путей в уровни C4 (контейнер → компонент).
/// Генератор, сканер проектных маркеров и всё, что захочет сопоставить файл с
/// элементом карты, обязаны ходить сюда, а не резать пути сами.
///
/// Правило:
/// <list type="number">
/// <item>Путь нормализуется: разделитель «/», без ведущих «./» и «/».</item>
/// <item>Служебные каталоги (bin/obj/node_modules/dist…, всё, что начинается с «.»)
///   выпадают из карты целиком — <see cref="IsIgnored"/>.</item>
/// <item>Контейнер — самый глубокий каталог проекта-маркера (.csproj / package.json),
///   внутри которого лежит файл. Нет маркера — каталог верхнего уровня
///   (общая свёртка для любых проектов); файл в корне — <see cref="RootName"/>.</item>
/// <item>Компонент — каталоги файла внутри контейнера, без «прозрачных» сегментов
///   в начале (src), не глубже <see cref="ComponentDepth"/>; файл прямо в корне
///   контейнера — <see cref="RootName"/>.</item>
/// </list>
/// </summary>
public sealed class ArchitecturePathFolding
{
    /// <summary>Глубина компонента в каталогах внутри контейнера.</summary>
    public const int ComponentDepth = 2;

    /// <summary>Имя «корня» — для файлов без каталога на своём уровне.</summary>
    public const string RootName = "(корень)";

    private static readonly HashSet<string> IgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "out", "packages", "artifacts",
        "TestResults", "wwwroot", "coverage", "vendor", "__pycache__",
        // data — рантайм-каталог (у CCS там профили CLI с чужими package.json), не код.
        "data",
    };

    private static readonly HashSet<string> TestSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "e2e", "__tests__", "__mocks__", "spec", "specs",
    };

    // Сегменты, не несущие смысла на карте: src/components → components.
    private static readonly HashSet<string> TransparentSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "src",
    };

    // Самые глубокие каталоги первыми: вложенный проект побеждает внешний.
    private readonly string[] _unitDirs;

    public ArchitecturePathFolding(IEnumerable<string> unitDirs)
    {
        _unitDirs = unitDirs
            .Select(Normalize)
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Length)
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Нормализация относительного пути: «/», без ведущих «./» и крайних «/».</summary>
    public static string Normalize(string path)
    {
        var p = (path ?? "").Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Trim('/');
    }

    /// <summary>Путь проходит через служебный каталог — на карту не попадает.</summary>
    public static bool IsIgnored(string relPath) =>
        Segments(relPath).Any(s => s.StartsWith('.') || IgnoredSegments.Contains(s));

    /// <summary>Путь относится к тестам (каталог тестов или тестовый проект *.Tests).</summary>
    public static bool IsTestPath(string relPath) =>
        Segments(relPath).Any(s => TestSegments.Contains(s)
            || s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ключ контейнера для файла (каталог проекта, каталог верхнего уровня или корень).</summary>
    public string ContainerOf(string relFile)
    {
        var file = Normalize(relFile);
        foreach (var dir in _unitDirs)
            if (file.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))
                return dir;

        var slash = file.IndexOf('/');
        return slash < 0 ? RootName : file[..slash];
    }

    /// <summary>Ключ компонента внутри контейнера (путь каталогов, не глубже <see cref="ComponentDepth"/>).</summary>
    public string ComponentOf(string relFile, string container)
    {
        var file = Normalize(relFile);
        var rest = container == RootName || !file.StartsWith(container + "/", StringComparison.OrdinalIgnoreCase)
            ? file
            : file[(container.Length + 1)..];

        var dirs = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1)
            .SkipWhile(s => TransparentSegments.Contains(s))
            .Take(ComponentDepth)
            .ToArray();
        return dirs.Length == 0 ? RootName : string.Join('/', dirs);
    }

    /// <summary>Свёртка файла в пару (контейнер, компонент).</summary>
    public (string Container, string Component) Fold(string relFile)
    {
        var container = ContainerOf(relFile);
        return (container, ComponentOf(relFile, container));
    }

    /// <summary>Человеческое имя контейнера по ключу: последний сегмент каталога.</summary>
    public static string DisplayName(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }

    private static IEnumerable<string> Segments(string relPath) =>
        Normalize(relPath).Split('/', StringSplitOptions.RemoveEmptyEntries);
}
