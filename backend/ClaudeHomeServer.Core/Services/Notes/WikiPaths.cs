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
// Инвариант: каноническая форма гейта == реальный путь ФС — «..» в ЦЕЛИ ссылки тоже
// применяется к физически разыменованному каталогу, а не к строке цели (ревью S1a, MAJOR 1).
// Разобрать не удалось (цикл ссылок, превышение глубины) — IsWikiPath отвечает fail-closed:
// путь считается принадлежащим вики, а не молчит: отказ лишнего места безопаснее записи
// в вику мимо гейта. Как SafePath, предикат чистый и не бросает: отказ формирует вызывающий.
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
        // Разобрать не удалось (цикл ссылок, превышение глубины) — fail-closed: считаем путь
        // в вики. Ни один вызывающий не пишет при true; отказ лишнего места безопаснее, чем
        // запись в вику мимо гейта (ревью S1a, MAJOR 1).
        if (full is null || wikiRoot is null) return true;
        return full.Equals(wikiRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(wikiRoot + "/", StringComparison.OrdinalIgnoreCase);
    }

    // Корень вики канонизируется тем же обходом, что и путь: «data/wiki» как символьная
    // ссылка (в том числе висячая) задаёт корень своей целью — иначе файл через ссылку
    // канонизируется в одно пространство имён, а корень — в другое, и гейт молчит (ревью S1a).
    // Предикат зовётся в циклах побочных писателей на каждый файл, но ответ обязан зависеть
    // только от состояния ФС, не от истории вызовов: корень считается тем же свежим
    // сегментным обходом, что и путь (тот же Canonical: разыменует и префикс dataDir, и
    // «wiki»), — без кэша между вызовами (ревью S1a, MAJOR 2: кэш корня на 60 с давал окно
    // fail-open, когда data/wiki становился ссылкой после первого вызова; MAJOR 3: бессрочный
    // кэш канонического dataDir — тот же класс на уровень выше: когда dataDir сам становился
    // ссылкой, закешированный корень и свежий путь описывали разные деревья). Замер ревью
    // S1a: 1000 вызовов = 42 мс — кэш не нужен; допустим только кэш на время одного прохода,
    // переданный вызывающим явно (такого вызывающего нет).
    private static string? WikiRootOf(string dataDir) => Canonical(Path.Combine(dataDir, DirName));

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
    // сегментов сверху вниз: на каждом шаге разыменование символьных ссылок (цель — тоже
    // путь, обходится этим же обходом), «..» применяется к уже разыменованному каталогу,
    // «.» пропускается. GetFullPath сворачивал «..» ДО разыменования — «link/../x» давал
    // путь наружу вики, хотя запись шла в вику (ревью S1a); теперь — как realpath -m,
    // в том числе внутри цели ссылки: «sub/../evil», где sub ведёт в вику, даёт вику
    // (ревью S1a, MAJOR 1). null — разбор не удался (превышен бюджет ссылок): вызывающий
    // трактает fail-closed. Только для сравнения, не для открытия файла: несуществующий
    // путь остаётся путём в той же форме. Хвост: разделители «/» → NFC.
    internal static string? Canonical(string path)
    {
        // GetFullPath здесь только за корень; его свёрнутые «..» ниже не используются
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) return Normalize(path); // ветка — на случай чужих форм

        var full = Path.IsPathFullyQualified(path) ? path : Path.Combine(Environment.CurrentDirectory, path);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            full = Path.GetFullPath(full); // drive-relative («C:x») — редкая форма, сворачивание допустимо

        var hops = 0; // общий бюджет разыменования на весь разбор этого пути (ревью S1a)
        var cur = WalkSegments(root, full[root.Length..], ref hops);
        return cur is null ? null : Normalize(cur);
    }

    // Глубина цепочки ссылок: цикл a→b→a не должен гнать бесконечно.
    private const int MaxLinkHops = 40;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    // Обход сегментов от уже разыменованного каталога cur: «.» пропускается, «..» — к текущему
    // (физически разыменованному) каталогу, каждый сегмент — цепочка ссылок. hops — общий
    // бюджет на весь разбор, вложенные цели ссылок тратят его же (не отдельный на каждую
    // цель). null — бюджет исчерпан: вызывающий трактает fail-closed.
    private static string? WalkSegments(string cur, string rel, ref int hops)
    {
        foreach (var seg in rel.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                var parent = Path.GetDirectoryName(cur); // над корнем «..» остаётся корнем
                if (parent is not null) cur = parent;
                continue;
            }
            var resolved = ResolveLinkChain(Path.Combine(cur, seg), ref hops);
            if (resolved is null) return null;
            cur = resolved;
        }
        return cur;
    }

    // Символьная ссылка каталога или файла — вся цепочка целей. Висячая ссылка (цель ещё
    // не существует) — тоже ссылка: её цель и есть путь (fail-closed: первая запись в такую
    // вику не должна уводиться за её пределы). Цель — путь: абсолютная считается от своего
    // корня, относительная — от каталога со ссылкой (POSIX), и обходится тем же сегментным
    // обходом: «..» в цели применяется к физически разыменованному каталогу, как ядро ФС
    // (ревью S1a, MAJOR 1: Path.GetFullPath схлопывал «..» в строке цели — гейт расходился
    // с тем, куда реально пойдёт запись). Не ссылка или нет на диске — сам candidate.
    // Бюджет hops общий на весь разбор; исчерпан (цикл) — null: такой путь всё равно
    // не запишется (ELOOP), но ответ обязан быть fail-closed, а не зависшим или молчащим.
    private static string? ResolveLinkChain(string candidate, ref int hops)
    {
        var current = candidate;
        while (true)
        {
            var raw = LinkTargetOf(current);
            if (raw is null) return current; // не ссылка или нет на диске — остаётся путём
            if (++hops > MaxLinkHops) return null;
            var resolved = Path.IsPathRooted(raw)
                ? ResolveRooted(raw, ref hops)
                : WalkSegments(Path.GetDirectoryName(current)!, raw, ref hops);
            if (resolved is null) return null;
            current = resolved; // цель уже разыменована целиком; своя ссылка продолжит цикл выше
        }
    }

    private static string? ResolveRooted(string rooted, ref int hops)
    {
        var root = Path.GetPathRoot(rooted);
        if (string.IsNullOrEmpty(root)) return null; // абсолютной без корня не бывает — не молчим
        return WalkSegments(root, rooted[root.Length..], ref hops);
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
        // недоступный элемент — не ссылка; шапка обещает «предикат не бросает», поэтому
        // и отказ доступа, и неподдерживаемая форма (хвост ревью S1a, минор 1) — тоже null:
        // путь остаётся как есть, дальше решает вызывающий (для пути под data/wiki — fail-closed)
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
}
