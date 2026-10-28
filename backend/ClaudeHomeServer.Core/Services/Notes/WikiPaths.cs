using System.Text;

namespace ClaudeHomeServer.Services.Notes;

// Корень вики владельца: {dataDir}/wiki/{ownerId}/ с pages/ и keeper/ (ADR-035, решение 1).
// Одна константа на весь сервер: гейт «в вики пишет только Хранитель» ключуется по пути под
// data/wiki/, а не по имени папки — notes/Вики/ в проектах больше не защищается ничем.
//
// Сравнение — по каноническому пути: GetFullPath (сворачивает «..»), разрешение символьных
// ссылок существующих компонентов, нормализация Unicode NFC, без учёта регистра. Путь через
// ссылку, ведущую в вики, — путь в вики; ссылка, ведущая наружу, — нет. Как SafePath,
// предикат чистый и не бросает: отказ формирует вызывающий.
public static class WikiPaths
{
    // Имя папки под data/ — одно на всех владельцев (ADR-035, решение 1).
    public const string DirName = "wiki";

    // Признак заведённой вики в pages/ (ADR-035, решение 1): гейт привязан к нему, а не к фич-флагу.
    public const string SchemaFileName = "_schema.md";

    public static string Root(string dataDir, string ownerId)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
            throw new ArgumentException("Нужен dataDir", nameof(dataDir));
        if (string.IsNullOrWhiteSpace(ownerId))
            throw new ArgumentException("Нужен ownerId", nameof(ownerId));
        // ownerId — одно имя, не путь: иначе ключ вылезал бы за пределы поддерева владельца
        if (ownerId.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0
            || ownerId == "." || ownerId == "..")
            throw new ArgumentException("ownerId не может быть путём", nameof(ownerId));
        return Path.Combine(dataDir, DirName, ownerId);
    }

    public static string Pages(string dataDir, string ownerId) => Path.Combine(Root(dataDir, ownerId), "pages");

    public static string Keeper(string dataDir, string ownerId) => Path.Combine(Root(dataDir, ownerId), "keeper");

    // Путь (абсолютный или относительный к cwd) лежит под data/wiki/ этого dataDir?
    // Каноническая форма — см. Canonical; регистр не учитывается: «data/Wiki/…» не способ
    // обойти гейт. data/notes/ и «wikipedia» — не вики.
    public static bool IsWikiPath(string path, string dataDir)
    {
        var full = Canonical(path);
        var wikiRoot = Canonical(dataDir).TrimEnd('/') + "/" + DirName;
        return full.Equals(wikiRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(wikiRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    // Вики заведена: есть pages/_schema.md. Признак на диске, а не фич-флаг (ADR-035, решение 1).
    public static bool IsInitialized(string dataDir, string ownerId) =>
        File.Exists(Path.Combine(Pages(dataDir, ownerId), SchemaFileName));

    // Служебные проекции раздела (_index/_log/_health): файл с именем на «_».
    // relPath — относительный путь внутри pages/; чистая функция, без обращения к диску.
    public static bool IsProjection(string relPath)
    {
        var rel = relPath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        var name = rel[(rel.LastIndexOf('/') + 1)..];
        return name.Length > 0 && name[0] == '_';
    }

    // Каноническая форма для сравнения: GetFullPath → разрешение символьных ссылок
    // существующих компонентов сверху вниз (ссылка среднего уровня меняет реальный
    // адрес файла) → разделители «/» → NFC. Только для сравнения, не для открытия файла:
    // несуществующий путь остаётся путём в той же форме.
    internal static string Canonical(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return Normalize(full); // GetFullPath даёт корень; ветка — на случай чужих форм

        var cur = root;
        foreach (var seg in full[root.Length..]
                     .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                            StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(cur, seg);
            cur = ResolveLinkTarget(candidate) ?? candidate;
        }
        return Normalize(cur);
    }

    // Символьная ссылка каталога или файла; не существует или не ссылка — null.
    // finalLinkTarget: true — вся цепочка ссылок. Неразрешённый хвост (несуществующий
    // файл под существующей ссылкой) дописывается вызывающим путём ниже.
    private static string? ResolveLinkTarget(string candidate)
    {
        if (Directory.Exists(candidate)) return new DirectoryInfo(candidate).ResolveLinkTarget(true)?.FullName;
        if (File.Exists(candidate)) return new FileInfo(candidate).ResolveLinkTarget(true)?.FullName;
        return null;
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
}
