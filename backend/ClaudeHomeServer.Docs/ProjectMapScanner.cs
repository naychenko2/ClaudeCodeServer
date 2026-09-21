using System.Security.Cryptography;
using System.Text;
using ClaudeHomeServer.Services.Llm.Claude;

namespace ClaudeHomeServer.Services.Docs;

// ---------- форма отчёта ----------

// Бюджет карты: ориентир Anthropic (≤200 строк) и факт превышения
public record MapBudget(int RecommendedLines, bool OverBudget);

// Секция карты (заголовок «##») — кандидат на уборку.
// FirstLine, а не тело: отчёт целиком уезжает в модель, и тела секций в окно не влезут.
// DocsRefs — сколько внутри живых ссылок на docs/*: признак пересказа готового документа.
public record MapSection(string Title, int StartLine, int Lines, int DocsRefs, string FirstLine);

// Находка по ссылке или импорту. Path — карта, в которой она стоит (их в отчёте несколько).
// Reason: notFound — цели нет нигде; rootRelativeOnly — резолвится только от корня проекта;
// absolute — абсолютный путь, не проверяли; outsideProject — уводит за пределы проекта;
// deadImport — @-импорт не раскрылся, файла нет (правило в контекст не едет вовсе).
// Candidates — механическая починка: путь заполнен, только если одноимённый файл в проекте
// ровно один. Ноль или два — решение человека, кандидатов не предлагаем.
public record MapLinkFinding(string Path, string? Section, int Line, string Target,
    string Reason, IReadOnlyList<string> Candidates);

// Справочная строка о файле: путь от корня проекта, строки, байты
public record MapFileRef(string Path, int Lines, long Bytes);

// Отчёт сканера — факты без единого суждения модели (Р1 плана уборки карты).
//
// У КАЖДОГО списка находок есть свой потолок и полный счётчик рядом: карта с сотней
// дефектов обязана уложиться в то же окно, что и здоровая, — иначе отчёт разнесёт ровно
// там, где фича нужнее всего (на нашем корпусе замер дал 161 мёртвую ссылку). Разница
// «счётчик минус длина списка» и есть честное «и ещё N».
//
// Свойства init, а не позиционный record: полей за два десятка, и в конструкторе по
// позициям их не прочитать — а ошибка порядка у однотипных int молча не заметится.
public record MapHygieneReport
{
    public required string Path { get; init; }
    public bool Exists { get; init; }
    // Отпечаток содержимого: едет обратно в review и apply
    public string? BaseSha { get; init; }

    public int Lines { get; init; }
    public long Bytes { get; init; }
    // ОЦЕНКА, а не факт: токенизатор у каждой модели свой. Показывать её как точное число
    // нельзя — первый же спор о цифре обесценит весь отчёт
    public int ApproxTokens { get; init; }
    public string ApproxTokensNote { get; init; } = TokensNote;

    // Размер с раскрытыми @-импортами: платится именно он
    public int ExpandedLines { get; init; }
    public int ImportCount { get; init; }
    // Раскрытие упёрлось в потолок объёма или вложенности — ExpandedLines занижен, и без
    // этих признаков отчёт сказал бы «всё хорошо» у самой запущенной карты
    public bool ExpansionTruncated { get; init; }
    public bool ImportDepthExceeded { get; init; }

    public required MapBudget Budget { get; init; }

    public int SectionCount { get; init; }
    public int LongSectionCount { get; init; }
    public IReadOnlyList<MapSection> Sections { get; init; } = [];

    public int DeadLinkCount { get; init; }
    public IReadOnlyList<MapLinkFinding> DeadLinks { get; init; } = [];

    public int DeadImportCount { get; init; }
    public IReadOnlyList<MapLinkFinding> DeadImports { get; init; } = [];

    public int RootRelativeCount { get; init; }
    public IReadOnlyList<MapLinkFinding> RootRelativeLinks { get; init; } = [];

    public int SkippedCount { get; init; }
    public IReadOnlyList<MapLinkFinding> Skipped { get; init; } = [];

    public MapFileRef? SecondMap { get; init; }
    public int NestedMapCount { get; init; }
    public IReadOnlyList<MapFileRef> NestedMaps { get; init; } = [];
    public MapFileRef? LocalMap { get; init; }

    // Хоть один список урезан потолком — читателю отчёта это видно одним полем,
    // без сверки четырёх пар «счётчик против длины»
    public bool Truncated { get; init; }

    internal const string TokensNote = "оценка: байты ÷ 3, точное число зависит от токенизатора модели";
}

/// <summary>
/// Гигиена карты проекта (CLAUDE.md): размер, длинные секции, мёртвые ссылки.
///
/// Детерминированный сканер без модели — фаза 1 уборки карты. Модель не читает карту
/// целиком: 100 КБ не влезают в окно дешёвого профиля, а факт «существует ли путь»
/// она обязана получать, а не угадывать.
///
/// Разбор markdown переиспользует регулярки индекса доков (`DocsIndexService`) — третий
/// комплект правил CommonMark в одной сборке неминуемо разошёлся бы с двумя первыми.
/// Содержимое ```-заборов не разбирается вовсе: в документации про документацию примеры
/// ссылок и заголовков — норма, и сканер, ругающийся на них, обесценивает себя на первом
/// же прогоне (проверено самопроверкой прототипа на тексте плана).
/// </summary>
public sealed class ProjectMapScanner(IConfiguration? config = null)
{
    // Пороги — настройка, а не константа: разные проекты живут с разной картой (Р6)
    private readonly int _sectionLineThreshold =
        int.TryParse(config?["ProjectMap:SectionLineThreshold"], out var t) && t > 0 ? t : 40;
    private readonly int _totalLineBudget =
        int.TryParse(config?["ProjectMap:TotalLineBudget"], out var b) && b > 0 ? b : 200;
    private readonly int _maxSectionsInReport =
        int.TryParse(config?["ProjectMap:MaxSectionsInReport"], out var m) && m > 0 ? m : 15;
    // Потолок КАЖДОГО списка находок по ссылкам и импортам (dead, deadImports,
    // rootRelative, skipped считаются отдельно): сотня дефектов не имеет права разнести отчёт
    private readonly int _maxLinkFindingsInReport =
        int.TryParse(config?["ProjectMap:MaxDeadLinksInReport"], out var d) && d > 0 ? d : 20;
    private readonly int _maxNestedMaps =
        int.TryParse(config?["ProjectMap:MaxNestedMaps"], out var n) && n > 0 ? n : 20;

    // Карта проекта — то, что реально собирает продукт (ClaudeSession): корневой файл и
    // его двойник в .claude/. Личный CLAUDE.md профиля CLI в отчёт не входит — он не про проект.
    private const string MainMapName = "CLAUDE.md";
    // Компактная карта для BareMode: в счёт контекста не входит, но показывает, что
    // компактная версия того же проекта уже написана и цель достижима (Р4)
    private const string LocalMapPath = "docs/CLAUDE-local.md";

    // Папки, которых нет ни в одном обходе: рабочие деревья дают копии карт (наивный find
    // нашёл 8 файлов CLAUDE.md вместо 4), bin/obj и node_modules — чистый мусор
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", ".vs", ".idea" };

    // Предохранитель обхода: у чужого проекта дерево может быть любым, а отчёт нужен быстро.
    // Упёрлись в потолок — вложенные карты и кандидаты неполны, но отчёт честно отдаётся
    private const int MaxWalkFiles = 100_000;

    // Длина первой строки секции в отчёте: дальше начинается пересказ тела
    private const int FirstLineMax = 120;

    public MapHygieneReport Scan(string root)
    {
        var mainFull = Path.Combine(root, MainMapName);
        var walk = Walk(root);

        // Карты нет вовсе — штатный ответ, а не ошибка: у большинства проектов её и не будет
        var main = SafeExists(mainFull) ? ParseMap(root, MainMapName) : null;
        if (main is null)
            return new MapHygieneReport
            {
                Path = MainMapName,
                Exists = false,
                Budget = new MapBudget(_totalLineBudget, OverBudget: false),
                NestedMapCount = walk.NestedMaps.Count,
                NestedMaps = Cap(walk.NestedMaps, _maxNestedMaps),
                LocalMap = FileRef(root, LocalMapPath),
                Truncated = walk.NestedMaps.Count > _maxNestedMaps,
            };

        var dead = new List<MapLinkFinding>();
        var rootRelative = new List<MapLinkFinding>();
        var skipped = new List<MapLinkFinding>();

        // Ссылки проверяются во ВСЕХ картах проекта: именно во вложенных живёт третий исход
        // резолва — там их пишут от корня репозитория, и по стандарту markdown они битые
        var maps = new List<ParsedMap> { main };
        var secondPath = $".claude/{MainMapName}";
        var second = ParseMap(root, secondPath);
        if (second is not null) maps.Add(second);
        foreach (var nested in walk.NestedMaps)
            if (ParseMap(root, nested.Path) is { } parsed) maps.Add(parsed);

        foreach (var map in maps)
            CheckLinks(root, map, walk, dead, rootRelative, skipped);

        // Живые ссылки на docs/* внутри секции — признак пересказа готового документа
        var docsRefs = CountDocsRefs(root, main);

        var longSections = main.Sections
            .Where(s => s.Lines > _sectionLineThreshold)
            .OrderByDescending(s => s.Lines)
            .ToList();

        var sections = longSections
            .Take(_maxSectionsInReport)
            .Select(s => new MapSection(s.Title, s.StartLine, s.Lines,
                docsRefs.GetValueOrDefault(s.StartLine), s.FirstLine))
            .ToList();

        var expansion = ClaudeMdExpander.Expand(Path.Combine(root, MainMapName));
        var deadImports = DeadImportFindings(root, maps, expansion);

        return new MapHygieneReport
        {
            Path = MainMapName,
            Exists = true,
            BaseSha = Sha256(main.Text),

            Lines = main.Lines,
            Bytes = main.Bytes,
            ApproxTokens = (int)(main.Bytes / 3),

            // Ничего не раскрылось — размер равен исходному. Считать по тексту раскрытия
            // и в этом случае нельзя: оно нормализует концовку файла и добавило бы строку,
            // из-за чего отчёт показывал бы expandedLines > lines у карты без импортов
            ExpandedLines = expansion.Text is null || expansion.ImportCount == 0
                ? main.Lines
                : CountLines(expansion.Text),
            ImportCount = expansion.ImportCount,
            ExpansionTruncated = expansion.Truncated,
            ImportDepthExceeded = expansion.DepthExceeded,

            Budget = new MapBudget(_totalLineBudget, main.Lines > _totalLineBudget),

            SectionCount = main.Sections.Count,
            LongSectionCount = longSections.Count,
            Sections = sections,

            DeadLinkCount = dead.Count,
            DeadLinks = Cap(dead, _maxLinkFindingsInReport),
            DeadImportCount = deadImports.Count,
            DeadImports = Cap(deadImports, _maxLinkFindingsInReport),
            RootRelativeCount = rootRelative.Count,
            RootRelativeLinks = Cap(rootRelative, _maxLinkFindingsInReport),
            SkippedCount = skipped.Count,
            Skipped = Cap(skipped, _maxLinkFindingsInReport),

            SecondMap = second is null ? null : new MapFileRef(secondPath, second.Lines, second.Bytes),
            NestedMapCount = walk.NestedMaps.Count,
            NestedMaps = Cap(walk.NestedMaps, _maxNestedMaps),
            LocalMap = FileRef(root, LocalMapPath),

            Truncated = longSections.Count > _maxSectionsInReport ||
                dead.Count > _maxLinkFindingsInReport ||
                deadImports.Count > _maxLinkFindingsInReport ||
                rootRelative.Count > _maxLinkFindingsInReport ||
                skipped.Count > _maxLinkFindingsInReport ||
                walk.NestedMaps.Count > _maxNestedMaps,
        };
    }

    private static IReadOnlyList<T> Cap<T>(IReadOnlyList<T> all, int max) =>
        all.Count <= max ? all : [.. all.Take(max)];

    // Импорт, который не раскрылся: файла нет. Отдельная находка, а не «пропущено» —
    // человек уверен, что правило едет в контекст, и молчание тут дороже всего.
    // Номер строки ищется в сыром тексте той карты, где импорт написан (вложенные импорты
    // живут в чужих файлах — у них Line остаётся нулём, а Path указывает на источник)
    private static List<MapLinkFinding> DeadImportFindings(string root,
        IReadOnlyList<ParsedMap> maps, ClaudeMdExpansion expansion)
    {
        var result = new List<MapLinkFinding>();
        foreach (var missing in expansion.MissingImports)
        {
            var sourceRelative = Relative(root, missing.SourceFile);
            var map = maps.FirstOrDefault(m =>
                string.Equals(m.Path, sourceRelative, StringComparison.OrdinalIgnoreCase));
            var line = map is null ? 0 : ImportLine(map.Text, missing.Target);
            result.Add(new MapLinkFinding(sourceRelative, null, line, missing.Target, "deadImport", []));
        }
        return result;
    }

    private static int ImportLine(string text, string target)
    {
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var trimmed = raw.TrimEnd('\r').Trim();
            if (trimmed.Length > 1 && trimmed[0] == '@' && trimmed[1..] == target) return lineNo;
        }
        return 0;
    }

    // Путь файла от корня проекта с прямыми слэшами; файл вне проекта отдаётся как есть
    private static string Relative(string root, string fullPath)
    {
        try
        {
            var rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            return rel.StartsWith("../", StringComparison.Ordinal) ? fullPath : rel;
        }
        catch (ArgumentException) { return fullPath; }
    }

    // ---------- ссылки ----------

    private static void CheckLinks(string root, ParsedMap map, WalkResult walk,
        List<MapLinkFinding> dead, List<MapLinkFinding> rootRelative, List<MapLinkFinding> skipped)
    {
        foreach (var link in map.Links)
        {
            var (target, _) = DocsIndexService.SplitAnchor(link.Target);
            if (target.Length == 0) continue;                       // якорь внутри документа
            if (DocsIndexService.IsExternal(target)) continue;      // в сеть не ходим

            // Абсолютный путь в SafePath.Join не отдаём: на Linux «/a/b» станет
            // относительным и приклеится к корню — проверка «ссылка наружу» исчезла бы молча
            if (IsAbsolute(target))
            {
                skipped.Add(Finding(map, link, target, "absolute"));
                continue;
            }

            // Как GitHub и панель «Документация»: от каталога файла-источника
            var resolved = DocsIndexService.ResolveRelative(map.Path, target);
            if (resolved is null)
            {
                skipped.Add(Finding(map, link, target, "outsideProject"));
                continue;
            }
            if (DocsIndexService.RepoTargetExists(root, resolved)) continue;

            // Третий исход: от каталога не нашлось, а от корня проекта нашлось. Не «живая»
            // (по стандарту markdown она битая) и не «мёртвая» (файл на месте) — показываем
            // как есть, решение за человеком
            var fromRoot = DocsIndexService.ResolveRelative("", target);
            if (fromRoot is not null &&
                !string.Equals(fromRoot, resolved, StringComparison.OrdinalIgnoreCase) &&
                DocsIndexService.RepoTargetExists(root, fromRoot))
            {
                rootRelative.Add(Finding(map, link, target, "rootRelativeOnly"));
                continue;
            }

            dead.Add(Finding(map, link, target, "notFound", walk.Candidates(target)));
        }
    }

    private static MapLinkFinding Finding(ParsedMap map, RawLink link, string target, string reason,
        IReadOnlyList<string>? candidates = null) =>
        new(map.Path, link.Section, link.Line, target, reason, candidates ?? []);

    // «/x», «\x», «C:\x» — абсолютные. Однобуквенная «схема» это диск Windows, поэтому
    // IsExternal её и не ловит (см. SchemeRegex)
    private static bool IsAbsolute(string target) =>
        target.StartsWith('/') || target.StartsWith('\\') ||
        (target.Length >= 2 && char.IsLetter(target[0]) && target[1] == ':');

    // Сколько в каждой секции живых ссылок на docs/*. Ключ — номер строки заголовка секции
    private static Dictionary<int, int> CountDocsRefs(string root, ParsedMap map)
    {
        var result = new Dictionary<int, int>();
        foreach (var link in map.Links)
        {
            if (link.SectionStart is not { } start) continue;
            var (target, _) = DocsIndexService.SplitAnchor(link.Target);
            if (target.Length == 0 || DocsIndexService.IsExternal(target) || IsAbsolute(target)) continue;
            var resolved = DocsIndexService.ResolveRelative(map.Path, target);
            if (resolved is null || !resolved.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DocsIndexService.RepoTargetExists(root, resolved)) continue;
            result[start] = result.GetValueOrDefault(start) + 1;
        }
        return result;
    }

    // ---------- разбор карты ----------

    private readonly record struct RawLink(string Target, int Line, string? Section, int? SectionStart);

    private sealed record RawSection(string Title, int StartLine, int Lines, string FirstLine);

    private sealed record ParsedMap(string Path, string Text, int Lines, long Bytes,
        IReadOnlyList<RawSection> Sections, IReadOnlyList<RawLink> Links);

    // relativePath — путь карты от корня проекта; null, если файла нет или он не читается
    private static ParsedMap? ParseMap(string root, string relativePath)
    {
        string text;
        long bytes;
        try
        {
            var full = SafePath.Join(root, relativePath);
            if (!File.Exists(full)) return null;
            bytes = new FileInfo(full).Length;
            text = File.ReadAllText(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var sections = new List<RawSection>();
        var links = new List<RawLink>();
        var inFence = false;
        var lineNo = 0;

        string? curTitle = null;
        var curStart = 0;
        var curFirstLine = "";
        var curEnd = 0;

        void CloseSection(int endLine)
        {
            if (curTitle is null) return;
            sections.Add(new RawSection(curTitle, curStart, endLine - curStart + 1, curFirstLine));
            curTitle = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            curEnd = lineNo;

            // Забор кода закрывает разбор ЦЕЛИКОМ — и заголовков, и ссылок. «## Пример»
            // внутри примера markdown не секция, а [текст](путь) там — не связь
            if (DocsIndexService.FenceRegex().IsMatch(line)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var h = DocsIndexService.HeadingRegex().Match(line);
            if (h.Success)
            {
                var level = h.Groups[1].Value.Length;
                if (level <= 2)
                {
                    CloseSection(lineNo - 1);
                    if (level == 2)
                    {
                        curTitle = DocsIndexService.StripMarkdown(h.Groups[2].Value);
                        curStart = lineNo;
                        curFirstLine = "";
                    }
                }
                continue;
            }

            if (curTitle is not null && curFirstLine.Length == 0 && line.Trim().Length > 0)
            {
                var first = DocsIndexService.StripMarkdown(line.Trim());
                curFirstLine = first.Length > FirstLineMax ? first[..FirstLineMax] + "…" : first;
            }

            foreach (System.Text.RegularExpressions.Match m in DocsIndexService.LinkRegex().Matches(line))
            {
                var target = m.Groups[2].Value.Trim();
                if (target.Length == 0) continue;
                links.Add(new RawLink(target, lineNo, curTitle, curTitle is null ? null : curStart));
            }
        }

        CloseSection(curEnd);

        return new ParsedMap(relativePath, text, CountLines(text), bytes, sections, links);
    }

    // Строки как их считает wc -l: завершающий перевод строки новой строки не создаёт
    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var n = text.Count(c => c == '\n');
        return text[^1] == '\n' ? n : n + 1;
    }

    // ---------- обход дерева ----------

    // Вложенные карты и указатель «имя файла → где он лежит» для кандидатов починки
    private sealed record WalkResult(IReadOnlyList<MapFileRef> NestedMaps,
        IReadOnlyDictionary<string, List<string>> ByName)
    {
        // Ровно один одноимённый файл ⇒ механическая починка возможна; ноль или два ⇒ пусто.
        // Кандидата ищет СКАНЕР, а не модель: обе мёртвые ссылки нашего репозитория
        // чинятся при неработающей LLM
        public IReadOnlyList<string> Candidates(string target)
        {
            var name = target.Replace('\\', '/');
            var i = name.LastIndexOf('/');
            if (i >= 0) name = name[(i + 1)..];
            if (name.Length == 0 || !ByName.TryGetValue(name, out var paths) || paths.Count != 1)
                return [];
            return string.Equals(paths[0], target, StringComparison.OrdinalIgnoreCase) ? [] : paths;
        }
    }

    private static WalkResult Walk(string root)
    {
        var nested = new List<MapFileRef>();
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;

        void Visit(string dir, string relativeDir)
        {
            if (seen >= MaxWalkFiles) return;

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                if (seen >= MaxWalkFiles) return;
                var name = Path.GetFileName(entry);
                var rel = relativeDir.Length == 0 ? name : $"{relativeDir}/{name}";

                bool isDir;
                try { isDir = Directory.Exists(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                if (isDir)
                {
                    if (SkipDirNames.Contains(name)) continue;
                    // Рабочие деревья агентов — копии репозитория целиком: в отчёте они
                    // дали бы вторые экземпляры всех карт и всех кандидатов
                    if (rel.Equals(".claude/worktrees", StringComparison.OrdinalIgnoreCase)) continue;
                    Visit(entry, rel);
                    continue;
                }

                seen++;
                if (!byName.TryGetValue(name, out var paths)) byName[name] = paths = [];
                paths.Add(rel);

                // Корневая карта и её двойник в .claude/ — не «вложенные»: они и есть предмет отчёта
                if (!name.Equals(MainMapName, StringComparison.OrdinalIgnoreCase)) continue;
                if (relativeDir.Length == 0 || relativeDir.Equals(".claude", StringComparison.OrdinalIgnoreCase))
                    continue;
                try { nested.Add(new MapFileRef(rel, CountLines(File.ReadAllText(entry)), new FileInfo(entry).Length)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        try { Visit(root, ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new WalkResult([.. nested.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)], byName);
    }

    // ---------- мелочи ----------

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static MapFileRef? FileRef(string root, string relativePath)
    {
        try
        {
            var full = SafePath.Join(root, relativePath);
            if (!File.Exists(full)) return null;
            return new MapFileRef(relativePath, CountLines(File.ReadAllText(full)), new FileInfo(full).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // Отпечаток содержимого: едет обратно в review и apply — по нему видно, что файл
    // не подменили между проверкой и записью (карту параллельно правят исполнители задач)
    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
