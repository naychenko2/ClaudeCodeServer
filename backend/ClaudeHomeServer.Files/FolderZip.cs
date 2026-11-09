using System.IO.Compression;

namespace ClaudeHomeServer.Services.Files;

// Упаковка папки проекта в zip для «Скачать архивом». Архив пишется во временный файл,
// а не прямо в ответ: ZipArchive пишет синхронно, и на медленном клиенте из интернета
// синхронная запись в Response держала бы поток пула всё время скачивания.
public static class FolderZip
{
    // Потолки на исходные байты и число файлов: архив собирается на диске сервера,
    // и случайный клик по корню большого репозитория не должен его забить
    public const long MaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxFiles = 50_000;

    // Что в архив не кладём. Список намеренно уже TreeExcludes: тот режет и build/, vendor/,
    // dist/, target/ — а в них у пользователя бывают нужные файлы (скрипты сборки, вендоринг).
    // Здесь только заведомый мусор: VCS, зависимости, выходы сборки .NET, кэши IDE и Python
    public static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".idea",
        ".venv", "venv", "__pycache__", TreeExcludes.AttachmentsDir,
    };

    // Формат zip хранит дату в DOS-виде (1980–2107); сеттер LastWriteTime за пределами
    // бросает ArgumentOutOfRangeException — файл с mtime 1970 ронял бы весь архив
    private static readonly DateTime DosMin = new(1980, 1, 1, 0, 0, 0);
    private static readonly DateTime DosMax = new(2107, 12, 31, 23, 59, 58);

    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = true, // нечитаемый подкаталог пропускаем, а не роняем всю папку в 403
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    public sealed class TooLargeException(string message) : Exception(message);

    public sealed record Result(FileStream Stream, int Skipped);

    // Есть ли символическая ссылка (или junction) на пути от корня проекта до папки,
    // включая саму папку. SafePath проверяет путь лексически, а ссылка внутри проекта может
    // вести куда угодно на хосте: архив по ней унёс бы чужое дерево одним запросом.
    // Сам корень проекта не проверяем — его выбрал владелец.
    public static bool HasLinkOnPath(string root, string folder)
    {
        var rel = Path.GetRelativePath(root, folder);
        if (rel == ".") return false;
        var current = root;
        foreach (var seg in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, seg);
            if (new DirectoryInfo(current).LinkTarget is not null) return true;
        }
        return false;
    }

    // Файлы папки для архива: исключённые каталоги и символические ссылки пропускаются —
    // по ссылке обход увёл бы за пределы проекта. Облачные reparse point (OneDrive)
    // ссылками не считаются и в архив попадают. Пути — относительные от folder, через «/».
    public static List<(string FullPath, string EntryName)> Collect(string folder, CancellationToken ct = default)
    {
        var result = new List<(string, string)>();
        long total = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(folder));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            // Перепроверка при входе, включая стартовый каталог: между HasLinkOnPath и обходом
            // каталог могли подменить ссылкой (ln -s / src) — по ней не идём
            if (dir.LinkTarget is not null) continue;
            IEnumerable<FileSystemInfo> children;
            try { children = dir.EnumerateFileSystemInfos("*", Enumeration).ToList(); }
            // Каталог исчез между обходом родителя и своим — просто нечего паковать
            catch (DirectoryNotFoundException) { continue; }
            foreach (var info in children)
            {
                if (info.LinkTarget is not null) continue;
                if (info is DirectoryInfo sub)
                {
                    if (!ExcludedDirs.Contains(sub.Name)) stack.Push(sub);
                    continue;
                }
                var file = (FileInfo)info;
                total += file.Length;
                if (result.Count + 1 > MaxFiles)
                    throw new TooLargeException($"В папке больше {MaxFiles} файлов — архивом её не скачать");
                if (total > MaxTotalBytes)
                    throw new TooLargeException("Папка больше 2 ГБ — архивом её не скачать");
                result.Add((file.FullName, Path.GetRelativePath(folder, file.FullName).Replace('\\', '/')));
            }
        }
        return result;
    }

    // Собрать архив во временный файл; поток удалит файл сам при закрытии (и при отмене).
    // Файл, пропавший или занятый между обходом и упаковкой, пропускается и считается в
    // Skipped. Открываем источник ДО CreateEntry, так что пропуск открытия не оставляет
    // пустой записи; сбой посреди копирования — редкость, и запись тогда остаётся усечённой.
    public static Result Build(string folder, CancellationToken ct = default)
    {
        var entries = Collect(folder, ct);
        var tmp = Path.Combine(Path.GetTempPath(), $"ccs-zip-{Guid.NewGuid():N}.zip");
        var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.DeleteOnClose);
        var skipped = 0;
        try
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (full, name) in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    FileStream src;
                    try { src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; continue; }
                    using (src)
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                        entry.LastWriteTime = ClampDosTime(File.GetLastWriteTime(full));
                        try
                        {
                            using var dst = entry.Open();
                            src.CopyTo(dst);
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
                    }
                }
            }
            stream.Position = 0;
            return new Result(stream, skipped);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static DateTime ClampDosTime(DateTime t) => t < DosMin ? DosMin : t > DosMax ? DosMax : t;
}
