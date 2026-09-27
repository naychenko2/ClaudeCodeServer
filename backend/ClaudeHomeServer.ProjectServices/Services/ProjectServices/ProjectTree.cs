using System.IO.Enumeration;
using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Composition;

namespace ClaudeHomeServer.Services.ProjectServices;

/// <summary>
/// Чтение дерева проекта для обнаружения сервисов (ADR-016, задача 4.3): разбор манифестов
/// один, а источник файлов — по месту исполнения. Сервер читает свою папку напрямую
/// (<see cref="PhysicalProjectTree"/>), агент устройства — через шов <see cref="IProjectFiles"/>
/// (<see cref="ProjectFilesTree"/>): там политика корней машины и сверка открытого дескриптора.
/// Пути — абсолютные, как их строит разбор от корня проекта.
/// </summary>
public interface IProjectTree
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
    IReadOnlyList<string> GetFiles(string directory, string pattern = "*");
    IReadOnlyList<string> GetDirectories(string directory);
}

/// <summary>Папка проекта на этой же машине: поведение сервера до ADR-016.</summary>
public sealed class PhysicalProjectTree : IProjectTree
{
    public static readonly PhysicalProjectTree Instance = new();

    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public IReadOnlyList<string> GetFiles(string directory, string pattern = "*") => Directory.GetFiles(directory, pattern);
    public IReadOnlyList<string> GetDirectories(string directory) => Directory.GetDirectories(directory);
}

/// <summary>
/// Дерево проекта через шов <see cref="IProjectFiles"/>. Разбор манифестов синхронный, а шов
/// асинхронный под реализацию «по сети»; у агента он исполняется на месте, поэтому ожидание
/// здесь не держит поток дольше самой файловой операции. Путь вне корня проекта — «нет
/// такого файла», а не исключение: так же ведёт себя разбор, наткнувшись на чужое.
/// </summary>
public sealed class ProjectFilesTree(IProjectFiles files, Project project) : IProjectTree
{
    private static readonly bool IgnoreCase = !OperatingSystem.IsLinux();

    public bool FileExists(string path) => Entry(path) is { IsDirectory: false };

    public bool DirectoryExists(string path) =>
        Relative(path) is "" || Entry(path) is { IsDirectory: true };

    public string ReadAllText(string path) =>
        files.ReadFileAsync(project, Relative(path) ?? throw Outside(path)).GetAwaiter().GetResult();

    public IReadOnlyList<string> GetFiles(string directory, string pattern = "*") =>
        List(directory).Where(e => !e.IsDirectory && FileSystemName.MatchesSimpleExpression(pattern, e.Name, IgnoreCase))
            .Select(e => Path.Combine(directory, e.Name)).ToList();

    public IReadOnlyList<string> GetDirectories(string directory) =>
        List(directory).Where(e => e.IsDirectory).Select(e => Path.Combine(directory, e.Name)).ToList();

    private IReadOnlyList<Files.FileEntry> List(string directory) =>
        files.ListAsync(project, Relative(directory) ?? throw Outside(directory), showHidden: true).GetAwaiter().GetResult();

    private Files.FileEntry? Entry(string path)
    {
        var rel = Relative(path);
        if (string.IsNullOrEmpty(rel)) return null;
        var slash = rel.LastIndexOf('/');
        var (parent, name) = slash < 0 ? ("", rel) : (rel[..slash], rel[(slash + 1)..]);
        try
        {
            return files.ListAsync(project, parent, showHidden: true).GetAwaiter().GetResult()
                .FirstOrDefault(e => string.Equals(e.Name, name, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // Путь относительно корня через «/»; null — путь вне проекта
    private string? Relative(string path)
    {
        var rel = Path.GetRelativePath(project.RootPath, path);
        if (rel == ".") return "";
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return rel.Replace('\\', '/');
    }

    private static UnauthorizedAccessException Outside(string path) => new($"Путь вне проекта: {path}");
}
