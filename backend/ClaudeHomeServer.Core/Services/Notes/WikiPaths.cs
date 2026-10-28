using System.Collections.Concurrent;
using System.Text;

namespace ClaudeHomeServer.Services.Notes;

// Корень вики владельца: {dataDir}/wiki/{ownerId}/ с pages/ и keeper/ (ADR-035, решение 1).
// Одна константа на весь сервер: гейт «в вики пишет только Хранитель» ключуется по пути под
// data/wiki/, а не по имени папки — notes/Вики/ в проектах больше не защищается ничем.
//
// Сравнение — по каноническому пути: абсолютный путь БЕЗ лексического сворачивания «..»,
// затем сегментный обход сверху вниз с разыменованием символьных ссылок на каждом шаге
// (включая висячие — цель может не существовать), «..» применяется к уже разыменованному
// каталогу (POSIX: link/.. — родитель цели ссылки, как realpath -m), нормализация Unicode
// NFC, без учёта регистра. Путь через ссылку, ведущую в вики, — путь в вики; ссылка,
// ведущая наружу, — нет. Корень вики канонизируется этим же кодом: data/wiki как ссылка
// (в том числе висячая) задаёт корень своей целью — обе стороны в одном пространстве имён.
// Как SafePath, предикат чистый и не бросает: отказ формирует вызывающий.
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
        var wikiRoot = WikiRootOf(dataDir);
        return full.Equals(wikiRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(wikiRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    // Корень вики канонизируется тем же Canonical, что и путь: «data/wiki» как символьная
    // ссылка (в том числе висячая) задаёт корень своей целью — иначе файл через ссылку
    // канонизируется в одно пространство имён, а корень — в другое, и гейт молчит (ревью S1a).
    // Предикат зовётся в циклах побочных писателей на каждый файл — корень кэшируется на
    // dataDir: разыменяется не чаще раза в RootRecheckMs (смена symlink за время жизни
    // процесса не ожидается; период — только чтобы не держать устаревшее навсегда).
    private static string WikiRootOf(string dataDir)
    {
        var key = Normalize(dataDir);
        var now = Environment.TickCount64;
        if (_rootCache.TryGetValue(key, out var hit) && now - hit.stamp < RootRecheckMs)
            return hit.root;
        var root = Canonical(Path.Combine(dataDir, DirName));
        _rootCache[key] = (root, now);
        return root;
    }

    private static readonly ConcurrentDictionary<string, (string root, long stamp)> _rootCache = new();
    private const long RootRecheckMs = 60_000;

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

    // Каноническая форма для сравнения: абсолютный путь без сворачивания «..» → обход
    // сегментов сверху вниз: на каждом шаге разыменование символьных ссылок (цель — вся
    // цепочка), «..» применяется к уже разыменованному каталогу, «.» пропускается.
    // GetFullPath сворачивал «..» ДО разыменования — «link/../x» давал путь наружу вики,
    // хотя запись шла в вику (ревью S1a, MAJOR 2); теперь — как realpath -m.
    // Только для сравнения, не для открытия файла: несуществующий путь остаётся путём
    // в той же форме. Хвост: разделители «/» → NFC.
    internal static string Canonical(string path)
    {
        // GetFullPath здесь только за корень; его свёрнутые «..» ниже не используются
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return Normalize(path); // ветка — на случай чужих форм

        var full = Path.IsPathFullyQualified(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            full = Path.GetFullPath(full); // drive-relative («C:x») — редкая форма, сворачивание допустимо

        var cur = root;
        foreach (var seg in full[root.Length..]
                     .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                            StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                var parent = Path.GetDirectoryName(cur); // над корнем «..» остаётся корнем
                if (parent is not null) cur = parent;
                continue;
            }
            var candidate = Path.Combine(cur, seg);
            cur = ResolveLinkChain(candidate) ?? candidate;
        }
        return Normalize(cur);
    }

    // Глубина цепочки ссылок: цикл a→b→a не должен гнать бесконечно.
    private const int MaxLinkHops = 40;

    // Символьная ссылка каталога или файла — вся цепочка целей. Висячая ссылка (цель ещё
    // не существует) — тоже ссылка: её цель и есть путь (fail-closed: первая запись в такую
    // вику не должна уводиться за её пределы). Цель не абсолютная — считается от каталога
    // со ссылкой (POSIX). Не ссылка или нет на диске — null. Цикл — стоп на MaxLinkHops
    // (такой путь всё равно не запишется: ELOOP).
    private static string? ResolveLinkChain(string candidate)
    {
        var current = candidate;
        for (var hop = 0; hop < MaxLinkHops; hop++)
        {
            var raw = LinkTargetOf(current);
            if (raw is null) return hop == 0 ? null : current;
            var target = Path.IsPathRooted(raw) ? raw : Path.Combine(Path.GetDirectoryName(current)!, raw);
            current = Path.GetFullPath(target); // сворачиваем «.»/«..» в строке цели
        }
        return current;
    }

    // Свойство LinkTarget работает по lstat самого элемента: висячая ссылка даёт цель,
    // отсутствующий путь — null. Тип (каталог/файл) значения не имеет — пробуем оба.
    private static string? LinkTargetOf(string path)
    {
        try
        {
            var target = new DirectoryInfo(path).LinkTarget;
            return target ?? new FileInfo(path).LinkTarget;
        }
        catch (IOException) { return null; } // недоступный элемент — не ссылка
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
}
