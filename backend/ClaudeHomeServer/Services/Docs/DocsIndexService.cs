using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Docs;

// Индекс документации проекта для панели «Доки»: README.md в корне + docs/**/*.md.
//
// Зачем отдельный сервис, а не files/tree + files/content: панели нужна документация как
// СВЯЗНЫЙ КОРПУС — заголовки, слаги якорей, ссылки между документами и обратные ссылки.
// Собрать это с фронта означало бы скачать все документы и разобрать их в браузере на
// каждое открытие панели.
//
// Кеш живёт на КОРЕНЬ ПАПКИ, а не на проект: у соседей по папке (один RootPath, разные
// владельцы) документация одна и та же, разбирать её дважды незачем. Доступ проверяет
// контроллер — сюда попадает уже разрешённый корень.
public sealed partial class DocsIndexService
{
    // Область документации. Имена точные: на Linux файловая система регистрозависима,
    // и «Docs/» — это другая папка, а не та же самая.
    private const string ReadmeName = "README.md";

    // Папки по умолчанию, пока проект не настроил свои (Project.DocsFolders == null)
    public static readonly IReadOnlyList<string> DefaultFolders = ["docs"];

    // Предохранители: область не должна превращаться в обход всего репозитория
    private const int MaxDocs = 2000;
    private const long MaxDocBytes = 2 * 1024 * 1024;
    // Больше папок в области — это уже «весь репозиторий», а список в диалоге перестаёт читаться
    private const int MaxFolders = 30;

    // Глубина и объём поиска кандидатов в папки: диалогу нужны обозримые варианты,
    // а не полный обход репозитория с node_modules
    private const int SuggestMaxDepth = 3;
    private const int SuggestMaxFolders = 200;

    // Папки, которые не документация ни в одном проекте: обходить их дорого, а .md внутри
    // (README пакетов, шаблоны генераторов) только зашумили бы и область, и список кандидатов
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor",
        "packages", "venv", "__pycache__", "coverage", "TestResults",
    };

    // Сколько символов текста показываем вокруг совпадения в поиске
    private const int SnippetRadius = 60;

    private readonly ConcurrentDictionary<string, CachedIndex> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Разобранный корпус + отпечаток файлов, по которому решаем, не устарел ли он
    private sealed record CachedIndex(string Fingerprint, DocsCorpus Corpus);

    internal sealed class DocsCorpus
    {
        public required IReadOnlyList<DocEntry> Docs { get; init; }
        // Ключи всех словарей — относительный путь документа с прямыми слэшами.
        // Сравнение без учёта регистра: ссылки в доках пишут вольно, а на Windows
        // (среда разработки) регистр и так не различается. Коллизии на регистро-
        // зависимой ФС не роняют разбор — вторая запись просто не добавляется.
        public required Dictionary<string, DocEntry> ByPath { get; init; }
        public required Dictionary<string, string> Texts { get; init; }
        public required Dictionary<string, List<DocLink>> OutLinks { get; init; }
        public required Dictionary<string, List<DocBacklink>> Backlinks { get; init; }
    }

    // ---------- публичное API ----------

    // folders во всех методах: null — папки по умолчанию (docs/), пустой список — только
    // README.md. Настройка приходит из Project.DocsFolders, разбирать её здесь незачем —
    // сервис не знает про проекты и работает от корня папки.
    public IReadOnlyList<DocEntry> GetIndex(string rootPath, IReadOnlyList<string>? folders = null) =>
        GetCorpus(rootPath, folders).Docs;

    // Документ с содержимым и связями. null — путь вне области документации: это и есть
    // гейт эндпоинта. Проверяем ВХОЖДЕНИЕМ В ИНДЕКС, а не сравнением строки с «docs/»:
    // индекс построен обходом реальной файловой системы, поэтому вопрос регистра и
    // разделителей решается один раз здесь, одинаково для Windows и Linux.
    public DocDetail? GetDoc(string rootPath, string relativePath, IReadOnlyList<string>? folders = null)
    {
        var corpus = GetCorpus(rootPath, folders);
        var key = NormalizePath(relativePath);
        if (key is null || !corpus.ByPath.TryGetValue(key, out var entry)) return null;
        if (!corpus.Texts.TryGetValue(key, out var text)) return null;
        return new DocDetail(
            entry.Path, entry.Title, text,
            corpus.OutLinks.TryGetValue(key, out var outs) ? outs : [],
            corpus.Backlinks.TryGetValue(key, out var backs) ? backs : []);
    }

    public IReadOnlyList<DocSearchHit> Search(string rootPath, string query,
        IReadOnlyList<string>? folders = null, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var corpus = GetCorpus(rootPath, folders);
        var q = query.Trim();
        var hits = new List<DocSearchHit>();

        foreach (var doc in corpus.Docs)
        {
            if (hits.Count >= limit) break;

            // Заголовок документа и путь — совпадение без фрагмента текста
            if (doc.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                doc.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(new DocSearchHit(doc.Path, doc.Title, null, doc.Title));
                continue;
            }

            // Подзаголовок — ведём сразу к разделу
            var heading = doc.Headings.FirstOrDefault(h => h.Text.Contains(q, StringComparison.OrdinalIgnoreCase));
            if (heading is not null)
            {
                hits.Add(new DocSearchHit(doc.Path, doc.Title, heading.Slug, heading.Text));
                continue;
            }

            // Тело документа — фрагмент вокруг совпадения + якорь ближайшего заголовка выше
            if (!corpus.Texts.TryGetValue(doc.Path, out var text)) continue;
            var idx = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            hits.Add(new DocSearchHit(doc.Path, doc.Title, HeadingAbove(text, idx, doc.Headings), Snippet(text, idx, q.Length)));
        }

        return hits;
    }

    // ---------- настройка области ----------

    // Папки настройки к каноничному виду: прямые слэши, без краёв-разделителей, без дублей
    // и без выходов за корень. Мусор молча отбрасывается — настройка приходит с фронта, и
    // ронять из-за неё индекс всего проекта незачем.
    // Пустой список НЕ подменяется дефолтом: «снял все галки» — это осознанное «только README».
    public static IReadOnlyList<string> NormalizeFolders(IReadOnlyList<string>? folders)
    {
        if (folders is null) return DefaultFolders;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in folders)
        {
            var folder = NormalizeFolder(raw);
            if (folder is null || !seen.Add(folder)) continue;
            result.Add(folder);
            if (result.Count >= MaxFolders) break;
        }
        return result;
    }

    // Одна папка настройки. null — значение непригодно: пустое, абсолютное («C:\…», «/etc»)
    // или уводящее выше корня. Корень проекта («.», «/») тоже отбрасываем: выбор корня
    // означал бы обход всего репозитория, а README и так в области всегда.
    private static string? NormalizeFolder(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Replace('\\', '/');
        if (s.Contains(':')) return null;               // «C:/…» и alternate data stream
        var segments = new List<string>();
        foreach (var seg in s.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") return null;               // выход за корень проекта
            segments.Add(seg);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    // ---------- сборка корпуса ----------

    private DocsCorpus GetCorpus(string rootPath, IReadOnlyList<string>? folders)
    {
        var root = Path.GetFullPath(rootPath);
        var scope = NormalizeFolders(folders);
        // Ключ кеша — корень ВМЕСТЕ с областью: у соседей по папке (один RootPath, разные
        // владельцы) настройки свои, и без области в ключе они вытесняли бы корпус друг друга
        var key = root + "\n" + string.Join('\n', scope);
        var files = CollectFiles(root, scope);
        var fingerprint = Fingerprint(root, files);

        if (_cache.TryGetValue(key, out var cached) && cached.Fingerprint == fingerprint)
            return cached.Corpus;

        var corpus = BuildCorpus(root, files);
        _cache[key] = new CachedIndex(fingerprint, corpus);
        return corpus;
    }

    // Файлы области. README ищем точным именем, выбранные папки обходим целиком.
    private static List<string> CollectFiles(string root, IReadOnlyList<string> folders)
    {
        var files = new List<string>();
        // Вложенные друг в друга папки настройки («docs» и «docs/adr») дают один файл дважды
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var readme = Path.Combine(root, ReadmeName);
            if (File.Exists(readme) && seen.Add(readme)) files.Add(readme);

            foreach (var folder in folders)
            {
                if (files.Count >= MaxDocs) break;
                var dir = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) continue;
                foreach (var file in EnumerateMarkdown(dir))
                {
                    if (files.Count >= MaxDocs) break;
                    if (seen.Add(file)) files.Add(file);
                }
            }
        }
        catch (DirectoryNotFoundException) { /* папку удалили между проверкой и обходом */ }
        catch (UnauthorizedAccessException) { /* нет прав на часть дерева — отдаём что смогли */ }
        return files;
    }

    // Обход .md вручную, а не EnumerateFiles(AllDirectories): нужен пропуск служебных
    // подпапок. Выбранной может оказаться папка с node_modules внутри, и рекурсия туда
    // затянула бы тысячи чужих README.
    private static IEnumerable<string> EnumerateMarkdown(string dir)
    {
        var queue = new Queue<string>();
        queue.Enqueue(dir);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(current, "*.md");
                subdirs = Directory.GetDirectories(current);
            }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in files) yield return f;
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                queue.Enqueue(sub);
            }
        }
    }

    // Кандидаты в папки документации: папки с .md внутри, неглубоко и без служебных.
    // Выбранные добавляются всегда — в том числе несуществующие, иначе галка на удалённой
    // папке пропала бы из диалога, и пустой список документов выглядел бы поломкой.
    public IReadOnlyList<DocFolderCandidate> SuggestFolders(string rootPath, IReadOnlyList<string>? folders = null)
    {
        var root = Path.GetFullPath(rootPath);
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Walk(root, "", 0);

        foreach (var folder in NormalizeFolders(folders))
            found.TryAdd(folder, 0);

        return found
            .Select(kv => new DocFolderCandidate(kv.Key, kv.Value,
                Directory.Exists(Path.Combine(root, kv.Key.Replace('/', Path.DirectorySeparatorChar)))))
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Возвращает число .md в поддереве: родительская папка показывает суммарный счётчик,
        // даже когда её собственные .md лежат уровнем ниже (docs/ со всем внутри adr/)
        int Walk(string dir, string rel, int depth)
        {
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir, "*.md");
                subdirs = Directory.GetDirectories(dir);
            }
            catch (DirectoryNotFoundException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }

            var count = files.Length;
            if (depth < SuggestMaxDepth)
            {
                foreach (var sub in subdirs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                    count += Walk(sub, rel.Length == 0 ? name : $"{rel}/{name}", depth + 1);
                }
            }
            // Корень проекта папкой-кандидатом не бывает: его выбор = обход всего репозитория
            if (rel.Length > 0 && count > 0 && found.Count < SuggestMaxFolders) found[rel] = count;
            return count;
        }
    }

    // Отпечаток области: путь + время правки + размер каждого файла. Не «максимальный
    // mtime + количество»: удаление одного файла с добавлением другого в ту же секунду
    // такой ключ не заметил бы, и панель показывала бы устаревший корпус.
    private static string Fingerprint(string root, List<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.OrderBy(x => x, StringComparer.Ordinal))
        {
            var info = new FileInfo(f);
            sb.Append(Relative(root, f)).Append('|')
              .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('|')
              .Append(info.Exists ? info.Length : -1).Append('\n');
        }
        return sb.ToString();
    }

    private static DocsCorpus BuildCorpus(string root, List<string> files)
    {
        var docs = new List<DocEntry>();
        var byPath = new Dictionary<string, DocEntry>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Ссылки собираем сырыми: класс Doc/Repo можно определить только когда известен
        // ВЕСЬ состав области, поэтому классификация — вторым проходом ниже.
        var rawLinks = new Dictionary<string, List<ParsedLink>>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var rel = Relative(root, file);
            string text;
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxDocBytes) continue;
                text = File.ReadAllText(file);
            }
            catch (IOException) { continue; }             // файл переписывают прямо сейчас
            catch (UnauthorizedAccessException) { continue; }

            var parsed = ParseDocument(text);
            var title = parsed.Title ?? Path.GetFileNameWithoutExtension(file);
            var entry = new DocEntry(rel, title, info.LastWriteTimeUtc, info.Length, parsed.Headings);

            // TryAdd, а не индексатор: на регистрозависимой ФС рядом могут лежать
            // docs/api.md и docs/API.md — дубль по ключу не должен ронять разбор
            if (!byPath.TryAdd(rel, entry)) continue;
            docs.Add(entry);
            texts[rel] = text;
            rawLinks[rel] = parsed.Links;
        }

        var outLinks = new Dictionary<string, List<DocLink>>(StringComparer.OrdinalIgnoreCase);
        var backlinks = new Dictionary<string, List<DocBacklink>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (from, links) in rawLinks)
        {
            var resolved = new List<DocLink>();
            foreach (var link in links)
            {
                if (IsExternal(link.Target))
                {
                    resolved.Add(new DocLink(link.Target, null, DocLinkKind.External, link.Text));
                    continue;
                }

                // Ссылка-якорь внутри того же документа
                if (link.Target.Length == 0 && link.Anchor is not null)
                {
                    resolved.Add(new DocLink(from, link.Anchor, DocLinkKind.Doc, link.Text));
                    continue;
                }

                var target = ResolveRelative(from, link.Target);
                if (target is null) continue;   // ведёт за пределы проекта — не наша забота

                var kind = byPath.ContainsKey(target) ? DocLinkKind.Doc : DocLinkKind.Repo;
                resolved.Add(new DocLink(target, link.Anchor, kind, link.Text));

                if (kind != DocLinkKind.Doc || string.Equals(target, from, StringComparison.OrdinalIgnoreCase))
                    continue;
                // Обратные ссылки — разворот исходящих, отдельного хранилища нет (как в NotesService)
                var sourceTitle = byPath.TryGetValue(from, out var src) ? src.Title : from;
                if (!backlinks.TryGetValue(target, out var list))
                    backlinks[target] = list = [];
                if (!list.Any(b => string.Equals(b.Path, from, StringComparison.OrdinalIgnoreCase) && b.Anchor == link.Anchor))
                    list.Add(new DocBacklink(from, sourceTitle, link.Anchor));
            }
            outLinks[from] = resolved;
        }

        // README первым, дальше по папке и ЗАГОЛОВКУ: панель показывает дерево в этом же
        // порядке и подписывает строки заголовками — сортировка по имени файла выглядела бы
        // в ней произвольной (observability-audit.md с заголовком «Аудит…» вставал не туда).
        // Папка старше заголовка, иначе документы разных групп перемешались бы между собой.
        docs.Sort((a, b) =>
        {
            if (IsReadme(a.Path) != IsReadme(b.Path)) return IsReadme(a.Path) ? -1 : 1;
            var byFolder = string.Compare(Folder(a.Path), Folder(b.Path), StringComparison.OrdinalIgnoreCase);
            if (byFolder != 0) return byFolder;
            // Сравнение с учётом языка: заголовки русские, и ordinal ставил бы кириллицу
            // после латиницы, а внутри кириллицы — по кодам, а не по алфавиту
            var byTitle = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            return byTitle != 0 ? byTitle : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        return new DocsCorpus
        {
            Docs = docs, ByPath = byPath, Texts = texts,
            OutLinks = outLinks, Backlinks = backlinks,
        };
    }

    private static bool IsReadme(string relativePath) =>
        string.Equals(relativePath, ReadmeName, StringComparison.OrdinalIgnoreCase);

    // Папка документа («docs/adr/x.md» → «docs/adr»); корневые документы — пустая строка
    private static string Folder(string relativePath)
    {
        var i = relativePath.LastIndexOf('/');
        return i < 0 ? "" : relativePath[..i];
    }

    // ---------- разбор markdown ----------

    // Сырая ссылка до классификации: путь и якорь уже разделены
    internal readonly record struct ParsedLink(string Target, string? Anchor, string Text);

    internal sealed record ParsedDocument(string? Title, IReadOnlyList<DocHeading> Headings, List<ParsedLink> Links);

    // Заголовок вне блока кода: «## Текст» (до трёх пробелов отступа, как в CommonMark)
    [GeneratedRegex(@"^ {0,3}(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex HeadingRegex();

    // Ссылка [текст](цель) — но не картинка ![alt](src): у картинок навигации нет.
    // Хвостовой title в кавычках отбрасывается вместе с пробелом перед ним.
    [GeneratedRegex(@"(?<!\!)\[([^\]]*)\]\(\s*([^)\s]*)(?:\s+""[^""]*"")?\s*\)")]
    private static partial Regex LinkRegex();

    // Ограда блока кода: ``` или ~~~ (с отступом до трёх пробелов)
    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})")]
    private static partial Regex FenceRegex();

    internal static ParsedDocument ParseDocument(string markdown)
    {
        string? title = null;
        var headings = new List<DocHeading>();
        var links = new List<ParsedLink>();
        var inFence = false;

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Блоки кода пропускаем целиком: «# комментарий» в bash-примере — не заголовок,
            // а markdown-ссылка в примере кода — не связь между документами
            if (FenceRegex().IsMatch(line)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var h = HeadingRegex().Match(line);
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                var text = StripMarkdown(h.Groups[2].Value);
                if (level == 1 && title is null) title = text;
                else if (level is >= 2 and <= 3) headings.Add(new DocHeading(level, text, Slugify(text)));
            }

            foreach (Match m in LinkRegex().Matches(line))
            {
                var target = m.Groups[2].Value.Trim();
                if (target.Length == 0) continue;
                var (path, anchor) = SplitAnchor(target);
                links.Add(new ParsedLink(path, anchor, StripMarkdown(m.Groups[1].Value)));
            }
        }

        return new ParsedDocument(title, headings, links);
    }

    // Текст без markdown-разметки: код, выделение, ссылки и картинки схлопываются в текст.
    // От ЭТОГО текста считается слаг — фронт получает уже очищенный textContent DOM-узла,
    // и при разных входах одинаковая функция слагификации дала бы разные якоря.
    internal static string StripMarkdown(string text)
    {
        var s = text;
        s = Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1");   // картинка → alt
        s = Regex.Replace(s, @"\[([^\]]*)\]\([^)]*\)", "$1");    // ссылка → подпись
        s = s.Replace("`", "");
        s = Regex.Replace(s, @"\*\*|__|\*|_|~~", "");
        return s.Trim();
    }

    // Слаг якоря: нижний регистр, разделители в дефис, прочая пунктуация отброшена.
    // Буквы любых алфавитов сохраняются — заголовки в проекте русские.
    internal static string Slugify(string headingText)
    {
        var sb = new StringBuilder();
        foreach (var ch in StripMarkdown(headingText).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '\t' or '-' or '_' or '.' or '/') sb.Append('-');
        }
        // Схлопываем повторы дефисов и обрезаем края
        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        return slug;
    }

    // «foo.md#раздел» → ("foo.md", "раздел"); якорь нормализуется тем же слагом,
    // потому что в доках его пишут и словами, и уже готовым слагом.
    // Декодирование обязательно: в markdown кириллический якорь часто записан процент-
    // энкодингом («#%D1%81%D1%80%D0%BE%D0%BA»), и без decode слаг получался мусорный —
    // переход по такой ссылке открывал документ с начала вместо нужного раздела.
    internal static (string Path, string? Anchor) SplitAnchor(string target)
    {
        var i = target.IndexOf('#');
        if (i < 0) return (target, null);
        var raw = target[(i + 1)..];
        string decoded;
        try { decoded = Uri.UnescapeDataString(raw); }
        catch (UriFormatException) { decoded = raw; }   // битая %-последовательность
        var anchor = Slugify(decoded);
        return (target[..i], anchor.Length == 0 ? null : anchor);
    }

    internal static bool IsExternal(string target) =>
        target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        target.StartsWith("//", StringComparison.Ordinal);

    // Путь ссылки относительно документа-источника → путь от корня проекта.
    // null — ссылка уводит выше корня: такие в корпус не берём.
    internal static string? ResolveRelative(string fromDoc, string target)
    {
        var decoded = Uri.UnescapeDataString(target.Replace('\\', '/'));
        var baseDir = fromDoc.Contains('/') ? fromDoc[..fromDoc.LastIndexOf('/')] : "";
        var combined = decoded.StartsWith('/')
            ? decoded.TrimStart('/')
            : baseDir.Length > 0 ? $"{baseDir}/{decoded}" : decoded;

        var segments = new List<string>();
        foreach (var seg in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..")
            {
                if (segments.Count == 0) return null;   // выше корня проекта
                segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    // ---------- вспомогательное ----------

    // Путь от корня с прямыми слэшами: один формат для API, ссылок и ключей словарей
    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    // Нормализация пути из запроса к формату ключей корпуса
    private static string? NormalizePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return null;
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        return normalized.Length == 0 ? null : normalized;
    }

    // Якорь ближайшего заголовка выше совпадения — чтобы поиск вёл сразу в раздел
    private static string? HeadingAbove(string text, int matchIndex, IReadOnlyList<DocHeading> headings)
    {
        string? slug = null;
        foreach (var h in headings)
        {
            var pos = text.IndexOf(h.Text, StringComparison.Ordinal);
            if (pos < 0 || pos > matchIndex) continue;
            slug = h.Slug;
        }
        return slug;
    }

    private static string Snippet(string text, int matchIndex, int matchLength)
    {
        var start = Math.Max(0, matchIndex - SnippetRadius);
        var end = Math.Min(text.Length, matchIndex + matchLength + SnippetRadius);
        var body = text[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
        return (start > 0 ? "…" : "") + body + (end < text.Length ? "…" : "");
    }
}
