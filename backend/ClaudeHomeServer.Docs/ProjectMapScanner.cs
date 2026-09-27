using System.Security;
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
// AnchorText — якорная строка находки: markdown-ссылка ЦЕЛИКОМ («[текст](путь)») либо
// строка импорта («@rules/git.md»). Она же основание id предложения (Р8а.3) и она же
// будущий якорь замены: голый путь для этого не годится — один и тот же путь стоит в
// карте многократно, а полная ссылка с тем же текстом обычно один раз.
public record MapLinkFinding(string Path, string? Section, int Line, string Target,
    string Reason, IReadOnlyList<string> Candidates, string AnchorText = "");

// Справочная строка о файле: путь от корня проекта, строки, байты
public record MapFileRef(string Path, int Lines, long Bytes);

// Где находка стоит в карте: номер строки и заголовок секции (у длинной секции — её
// собственный заголовок, у ссылки — секция, внутри которой она написана)
public record MapSuggestionAnchor(int Line, string? Heading);

// Готовая механическая правка: замена подстроки AnchorText на After в корневой карте.
// Before и AnchorText — одна и та же строка под двумя именами (первое для показа
// человеку, второе для поиска в файле): расширять якорь контекстом ради уникальности
// запрещено — человек в «было → стало» видел бы одно, а в файл уезжало бы другое (Р10а).
public record MapApplyPatch(string Before, string After, string AnchorText);

// Предложение к уборке: ФАКТ сканера плюс место под суждение модели.
//
// Всё, кроме ModelSays и Severity, модель знать не могла — она видит отчёт, а не файл,
// и потому ничего из этого не диктует (Р8а.1). Sha-шный Id от содержимого, а не
// порядковый номер: факты не кэшируются, apply сканирует карту заново, и присланные
// фронтом ids обязаны находиться в СВЕЖЕМ скане — с номером человек применил бы не то
// предложение, которое отметил, молча и с записью в файл (Р8а.3).
public record MapSuggestion
{
    public required string Id { get; init; }
    // dead-link | dead-import | root-relative | long-section — ровно четыре, и все
    // выводятся из фактов сканера. «Секция пересказывает ADR» — это long-section плюс
    // фраза в ModelSays, а не пятый вид находки
    public required string Kind { get; init; }
    // Единственное, что проходит из ответа модели как есть, и то через белый список трёх
    // значений с дефолтом по виду: цена ошибки здесь — цвет плашки, а не правка файла
    public required string Severity { get; init; }
    // Факт сканера человеческим текстом. Показывается ОСНОВНЫМ: суждение модели — догадка
    // по метаданным, и поменяв их местами, человек снесёт живой раздел, поверив фразе
    public required string Fact { get; init; }
    // Суждение модели, ≤ 120 символов. null — модель промолчала или не ответила вовсе;
    // факт при этом показывается всё равно (принцип «факт важнее формулировки»)
    public string? ModelSays { get; init; }
    public required MapSuggestionAnchor Anchor { get; init; }
    // Сколько строк карты освободит правка: у длинной секции — её размер, у ссылки 0
    public int SavingLines { get; init; }
    // null ⇒ кнопки «Применить» нет вовсе. Заполняется только у dead-link и только когда
    // сканер нашёл ровно одного кандидата И якорь уникален вне кодовых заборов (Р10а) —
    // это контракт записи, его достраивает волна 4 вместе с самим apply
    public MapApplyPatch? Apply { get; init; }
}

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

    // Забор кода в карте не закрыт (опечатка вида ``` без пары): весь остаток файла
    // разобран как содержимое блока, то есть секции и ссылки ниже не проверены вовсе.
    // Отдельное поле, а не тишина — иначе отчёт рапортует «карта здорова» у половины файла
    public bool UnclosedFence { get; init; }

    // Список ДЕЙСТВИЙ поверх списков находок выше: те же факты, сведённые в одну форму с
    // устойчивым id. Отдаёт их сам скан, а не только review: id адресует будущее
    // применение правки, значит он обязан существовать до обращения к модели и приезжать
    // человеку вместе с фактами. Фронт ничего не синтезирует из deadLinks сам — списки
    // находок остаются фактурой, действия живут здесь
    public IReadOnlyList<MapSuggestion> Suggestions { get; init; } = [];

    // Чем сервер объясняет неполноту разбора моделью: «модель не ответила», «ответ не
    // разобрался». Сам сканер его не заполняет никогда (он модель не зовёт) — поле живёт
    // здесь, потому что review отдаёт ТОТ ЖЕ отчёт с вклеенными формулировками, а не
    // отдельную тройку полей: одна форма ответа на все три ручки, и фронту нечему
    // разъехаться с бэком. При успешном разборе — null
    public string? ModelNote { get; init; }

    public MapFileRef? SecondMap { get; init; }
    public int NestedMapCount { get; init; }
    public IReadOnlyList<MapFileRef> NestedMaps { get; init; } = [];
    public MapFileRef? LocalMap { get; init; }

    // Обход дерева упёрся в потолок файлов или вложенности: вложенные карты неполны,
    // а кандидаты починки не отдаются вовсе — «одноимённый файл ровно один» на
    // оборванном обходе не факт, а совпадение
    public bool WalkTruncated { get; init; }

    // Хоть один список урезан потолком — читателю отчёта это видно одним полем,
    // без сверки четырёх пар «счётчик против длины»
    public bool Truncated { get; init; }

    internal const string TokensNote = "оценка: байты ÷ 3, точное число зависит от токенизатора модели";
}

/// <summary>
/// Поиск якорной подстроки в карте ВНЕ кодовых заборов — общий для обеих половин фичи:
/// сканер решает по нему, рождается ли у предложения кнопка, а применение — куда писать.
///
/// Заборы не разбираются и на записи тоже: замена по подстроке иначе залезает в пример
/// внутри ```-блока, а в карте про карты таких примеров много. Прямое следствие правила:
/// цель, встречающаяся один раз в тексте и один раз в примере, — ОДИН кандидат, и замена
/// состоится.
///
/// Разметка построчная, а текст при этом НЕ пересобирается из строк: наружу отдаются
/// смещения в исходной строке текста. Пересборка через '\n' превратила бы CRLF-файл в LF
/// одной правкой — дифф на весь файл вместо одной строки.
/// </summary>
public static class MapAnchorText
{
    /// <summary>Смещения всех вхождений якоря вне кодовых заборов, слева направо.</summary>
    public static IReadOnlyList<int> Offsets(string text, string anchor)
    {
        var result = new List<int>();
        if (string.IsNullOrEmpty(anchor)) return result;

        var fence = new MarkdownFence();
        var pos = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            // Строка без завершающего \r: якорь пришёл от сканера, который читал тот же
            // текст построчно, и переноса внутри себя не содержит
            var lineEnd = end > pos && text[end - 1] == '\r' ? end - 1 : end;
            var line = text[pos..lineEnd];

            if (!fence.Consume(line) && !fence.InFence)
            {
                var i = line.IndexOf(anchor, StringComparison.Ordinal);
                while (i >= 0)
                {
                    result.Add(pos + i);
                    i = line.IndexOf(anchor, i + 1, StringComparison.Ordinal);
                }
            }

            if (nl < 0) break;
            pos = nl + 1;
        }
        return result;
    }

    /// <summary>Замена подстроки по смещению — без пересборки текста, BOM и CRLF целы.</summary>
    public static string ReplaceAt(string text, int offset, int length, string replacement) =>
        string.Concat(text.AsSpan(0, offset), replacement, text.AsSpan(offset + length));
}

/// <summary>
/// Общий словарь предложений: виды находок, градации серьёзности и формула id.
/// Живёт отдельно от сканера, потому что нужен обеим половинам — той, что факты
/// порождает, и той, что сшивает с ними суждение модели.
/// </summary>
public static class MapSuggestions
{
    public const string KindDeadLink = "dead-link";
    public const string KindDeadImport = "dead-import";
    public const string KindRootRelative = "root-relative";
    public const string KindLongSection = "long-section";

    // Ровно три градации: у Badge три подходящих тона (danger/warning/neutral),
    // четвёртая потребовала бы нового цвета в обе темы
    public const string SeverityHigh = "high";
    public const string SeverityMedium = "medium";
    public const string SeverityLow = "low";

    public static readonly IReadOnlySet<string> Severities =
        new HashSet<string>(StringComparer.Ordinal) { SeverityHigh, SeverityMedium, SeverityLow };

    // Мёртвая ссылка — единственная находка, где что-то заведомо сломано: остальные
    // требуют решения человека, а не починки
    public static string DefaultSeverity(string kind) =>
        kind == KindDeadLink ? SeverityHigh : SeverityMedium;

    /// <summary>
    /// Идентификатор находки — первые 8 байт SHA-256 от вида и якорной строки.
    ///
    /// От СОДЕРЖИМОГО, а не порядковый номер и не GUID: факты нигде не кэшируются,
    /// применение правки сканирует карту заново, и присланные фронтом id обязаны
    /// находиться в свежем скане. Порядковый номер съехал бы при любой правке выше по
    /// файлу — человек применил бы не то предложение, которое отметил, молча и с записью
    /// в файл. С хешем несовпадение даёт честный отказ по неизвестному id.
    /// </summary>
    public static string Id(string kind, string anchorText)
    {
        // Схлопывание пробелов и TrimEnd: перенос ссылки на другую строку или выравнивание
        // отступа не должны менять идентификатор того же самого дефекта
        var normalized = NormalizeAnchor(anchorText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\n{normalized}"));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }

    internal static string NormalizeAnchor(string anchorText)
    {
        var sb = new StringBuilder(anchorText.Length);
        var space = false;
        foreach (var c in anchorText)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
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
    // Пороги — настройка, а не константа: разные проекты живут с разной картой (Р6).
    // Ноль — осознанное значение («список в отчёт не класть вовсе», счётчик рядом остаётся
    // честным), а не приглашение к дефолту: подменять его молча значит врать настройке
    private readonly int _sectionLineThreshold =
        int.TryParse(config?["ProjectMap:SectionLineThreshold"], out var t) && t >= 0 ? t : 40;
    private readonly int _totalLineBudget =
        int.TryParse(config?["ProjectMap:TotalLineBudget"], out var b) && b >= 0 ? b : 200;
    private readonly int _maxSectionsInReport =
        int.TryParse(config?["ProjectMap:MaxSectionsInReport"], out var m) && m >= 0 ? m : 15;
    // Потолок КАЖДОГО списка находок по ссылкам и импортам (dead, deadImports,
    // rootRelative, skipped считаются отдельно): сотня дефектов не имеет права разнести отчёт
    private readonly int _maxLinkFindingsInReport =
        int.TryParse(config?["ProjectMap:MaxDeadLinksInReport"], out var d) && d >= 0 ? d : 20;
    private readonly int _maxNestedMaps =
        int.TryParse(config?["ProjectMap:MaxNestedMaps"], out var n) && n >= 0 ? n : 20;

    // Карта проекта — то, что реально собирает продукт (ClaudeSession): корневой файл и
    // его двойник в .claude/. Личный CLAUDE.md профиля CLI в отчёт не входит — он не про проект.
    // internal, а не private: имя карты — одно на вертикаль, второй такой же литерал
    // в ProjectMapApplyService означал бы две точки правды об одном файле
    internal const string MainMapName = "CLAUDE.md";
    // Компактная карта для BareMode: в счёт контекста не входит, но показывает, что
    // компактная версия того же проекта уже написана и цель достижима (Р4)
    private const string LocalMapPath = "docs/CLAUDE-local.md";

    // Папки, которых нет ни в одном обходе: рабочие деревья дают копии карт (наивный find
    // нашёл 8 файлов CLAUDE.md вместо 4), bin/obj и node_modules — чистый мусор
    private static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", ".vs", ".idea" };

    // Предохранитель обхода: у чужого проекта дерево может быть любым, а отчёт нужен быстро.
    // Упёрлись в потолок — вложенные карты неполны, кандидатов не отдаём вовсе, а сам
    // факт виден в отчёте полем WalkTruncated. Считаются и файлы, и КАТАЛОГИ: цикл из
    // симлинков без единого файла иначе не упёрся бы в потолок никогда
    private const int MaxWalkFiles = 100_000;

    // Предел вложенности обхода. Visit рекурсивна, а симлинк «latest -> .» или «static -> ..»
    // в репозитории — обычное дело: без предела обход уходит в цикл и кончается
    // StackOverflowException, который в .NET не ловится ни catch, ни middleware — падает
    // весь процесс бэкенда со всеми чужими чатами и идущими ходами. Реальные деревья
    // столько не вкладывают (у нас максимум — единицы уровней)
    private const int MaxWalkDepth = 32;

    // Длина первой строки секции в отчёте: дальше начинается пересказ тела
    private const int FirstLineMax = 120;

    // ct — полный обход дерева идёт на каждый запрос: ушёл клиент, ушёл и обход
    public MapHygieneReport Scan(string root, CancellationToken ct = default) =>
        Scan(root, mainText: null, ct);

    /// <summary>
    /// Тот же скан, но корневая карта берётся из УЖЕ ПРОЧИТАННОГО текста, а не с диска.
    ///
    /// Нужен применению правок (Р10а): файл там читается ровно один раз, и сверка хеша,
    /// поиск фактов и запись обязаны идти по одному снимку. Повторное чтение с диска
    /// между сверкой и записью означало бы, что сверен один текст, а перезаписан другой —
    /// чужая правка исчезла бы целиком, а сверка baseSha отработала бы «успешно».
    /// </summary>
    /// <param name="mainText">null — читать карту с диска обычным путём.</param>
    public MapHygieneReport Scan(string root, string? mainText, CancellationToken ct = default)
    {
        var mainFull = Path.Combine(root, MainMapName);

        // Карты нет вовсе — штатный ответ, а не ошибка: у большинства проектов её и не будет
        var main = mainText is not null
            ? ParseText(MainMapName, mainText)
            : SafeExists(mainFull) ? ParseMap(root, MainMapName) : null;

        // Указатель «имя файла → где он лежит» нужен ТОЛЬКО кандидатам починки, а они
        // бывают лишь у ссылок разобранных карт: нет карты — не держим в памяти путь
        // каждого файла чужого проекта
        var walk = Walk(root, collectByName: main is not null, ct);

        if (main is null)
            return new MapHygieneReport
            {
                Path = MainMapName,
                Exists = false,
                Budget = new MapBudget(_totalLineBudget, OverBudget: false),
                NestedMapCount = walk.NestedMaps.Count,
                NestedMaps = Cap(walk.NestedMaps, _maxNestedMaps),
                LocalMap = FileRef(root, LocalMapPath),
                WalkTruncated = walk.Truncated,
                Truncated = walk.NestedMaps.Count > _maxNestedMaps || walk.Truncated,
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

        // Раскрытие от текста, когда он уже в руках: то же самое побайтово, но без
        // второго чтения файла с диска (инвариант «читаем ровно один раз», Р10а)
        var expansion = mainText is not null
            ? ClaudeMdExpander.Expand(mainText, mainFull)
            : ClaudeMdExpander.Expand(mainFull);
        var deadImports = DeadImportFindings(root, main, expansion);

        // Предложения собираются из ПОКАЗАННЫХ находок, а не из полных списков: факт,
        // не доехавший до отчёта из-за потолка, нечем ни объяснить человеку, ни применить
        var deadShown = Cap(dead, _maxLinkFindingsInReport);
        var deadImportsShown = Cap(deadImports, _maxLinkFindingsInReport);
        var rootRelativeShown = Cap(rootRelative, _maxLinkFindingsInReport);

        // Ничего не раскрылось — размер равен исходному. Считать по тексту раскрытия
        // и в этом случае нельзя: оно нормализует концовку файла и добавило бы строку,
        // из-за чего отчёт показывал бы expandedLines > lines у карты без импортов
        var expandedLines = expansion.Text is null || expansion.ImportCount == 0
            ? main.Lines
            : CountLines(expansion.Text);

        return new MapHygieneReport
        {
            Path = MainMapName,
            Exists = true,
            // Отпечаток по РАСКРЫТОМУ составу, а не по одному файлу: правка импортированного
            // rules/*.md не меняет в CLAUDE.md ни байта, и при хеше по файлу обе опирающиеся
            // на baseSha проверки («тот ли файл я читал») врали бы ровно в том сценарии, ради
            // которого раскрытие и заведено. Раскрытие считается ВСЕГДА, в том числе при нуле
            // импортов — иначе у карты, в которую импорт добавили, хеш скакнул бы без единой
            // содержательной правки. Точка правды одна: и review, и apply берут это поле
            BaseSha = Sha256(expansion.Text ?? main.Text),

            Lines = main.Lines,
            Bytes = main.Bytes,
            ApproxTokens = (int)(main.Bytes / 3),

            ExpandedLines = expandedLines,
            ImportCount = expansion.ImportCount,
            ExpansionTruncated = expansion.Truncated,
            ImportDepthExceeded = expansion.DepthExceeded,

            // Приговор — по РАСКРЫТОМУ составу, а не по размеру самого файла: платится
            // именно он. Карта на сорок строк, собранная из восьми @rules/*.md, съедает
            // контекст как тысяча — и при сравнении с порогом по main.Lines отчёт врал бы
            // «всё хорошо» ровно в том сценарии, ради которого раскрытие и заведено
            Budget = new MapBudget(_totalLineBudget, expandedLines > _totalLineBudget),

            SectionCount = main.Sections.Count,
            LongSectionCount = longSections.Count,
            Sections = sections,

            DeadLinkCount = dead.Count,
            DeadLinks = deadShown,
            DeadImportCount = deadImports.Count,
            DeadImports = deadImportsShown,
            RootRelativeCount = rootRelative.Count,
            RootRelativeLinks = rootRelativeShown,
            SkippedCount = skipped.Count,
            Skipped = Cap(skipped, _maxLinkFindingsInReport),

            Suggestions = BuildSuggestions(deadShown, deadImportsShown, rootRelativeShown, sections,
                main.Text),

            UnclosedFence = main.UnclosedFence,

            SecondMap = second is null ? null : new MapFileRef(secondPath, second.Lines, second.Bytes),
            NestedMapCount = walk.NestedMaps.Count,
            NestedMaps = Cap(walk.NestedMaps, _maxNestedMaps),
            LocalMap = FileRef(root, LocalMapPath),

            WalkTruncated = walk.Truncated,
            Truncated = walk.Truncated ||
                longSections.Count > _maxSectionsInReport ||
                dead.Count > _maxLinkFindingsInReport ||
                deadImports.Count > _maxLinkFindingsInReport ||
                rootRelative.Count > _maxLinkFindingsInReport ||
                skipped.Count > _maxLinkFindingsInReport ||
                walk.NestedMaps.Count > _maxNestedMaps,
        };
    }

    private static IReadOnlyList<T> Cap<T>(IReadOnlyList<T> all, int max) =>
        all.Count <= max ? all : [.. all.Take(max)];

    // ---------- предложения ----------

    // Находки сводятся в один список действий с устойчивым id. Порядок — по весу вида:
    // сломанное впереди, содержательная работа в хвосте. Суждения модели приедут сюда
    // позже, сшивкой по id; сам сканер про модель не знает вовсе
    private static IReadOnlyList<MapSuggestion> BuildSuggestions(
        IReadOnlyList<MapLinkFinding> dead, IReadOnlyList<MapLinkFinding> deadImports,
        IReadOnlyList<MapLinkFinding> rootRelative, IReadOnlyList<MapSection> sections,
        string mainText)
    {
        var result = new List<MapSuggestion>();
        // Id считается от содержимого, поэтому одна и та же ссылка, написанная дважды
        // (в тексте и во вложенной карте, или просто повторённая), даёт один id — и это
        // один дефект, а не два. Первым выигрывает корневая карта: её находки идут первыми
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(MapSuggestion s)
        {
            if (seen.Add(s.Id)) result.Add(s);
        }

        foreach (var f in dead)
            Add(LinkSuggestion(f, MapSuggestions.KindDeadLink,
                f.Candidates.Count == 1
                    ? $"ссылка на «{f.Target}» — файла нет; одноимённый файл в проекте один: {f.Candidates[0]}"
                    : $"ссылка на «{f.Target}» — файла нет",
                BuildPatch(f, mainText)));

        foreach (var f in deadImports)
            Add(LinkSuggestion(f, MapSuggestions.KindDeadImport,
                $"импорт «@{f.Target}» не раскрылся — файла нет, и правило в контекст не едет вовсе"));

        foreach (var f in rootRelative)
            Add(LinkSuggestion(f, MapSuggestions.KindRootRelative,
                $"ссылка «{f.Target}» написана от корня проекта — по стандарту markdown она битая"));

        foreach (var s in sections)
        {
            var fact = s.DocsRefs > 0
                ? $"{s.Lines} строк, внутри {s.DocsRefs} живых ссылок на docs/*"
                : $"{s.Lines} строк";
            Add(new MapSuggestion
            {
                // Якорь секции — её заголовок: своего anchorText у неё нет, а без общей
                // формулы исполнитель скатился бы к порядковому номеру (Р8а.3)
                Id = MapSuggestions.Id(MapSuggestions.KindLongSection, s.Title),
                Kind = MapSuggestions.KindLongSection,
                Severity = MapSuggestions.DefaultSeverity(MapSuggestions.KindLongSection),
                Fact = fact,
                Anchor = new MapSuggestionAnchor(s.StartLine, s.Title),
                SavingLines = s.Lines,
            });
        }

        return result;
    }

    private static MapSuggestion LinkSuggestion(MapLinkFinding f, string kind, string fact,
        MapApplyPatch? patch = null) =>
        new()
        {
            Id = MapSuggestions.Id(kind, f.AnchorText),
            Kind = kind,
            Severity = MapSuggestions.DefaultSeverity(kind),
            Fact = f.Path == MainMapName ? fact : $"{fact} (в {f.Path})",
            Anchor = new MapSuggestionAnchor(f.Line, f.Section),
            // Патч есть только у мёртвой ссылки, и только когда выполнены ОБА условия
            // формулы Р10а (см. BuildPatch). У импортов, ссылок от корня и длинных секций
            // он null по определению вида находки, а не «пока не реализован»
            Apply = patch,
        };

    /// <summary>
    /// Патч механической починки мёртвой ссылки — или null, и тогда кнопки у предложения
    /// нет вовсе.
    ///
    /// Формула Р10а из двух независимых условий: сканер нашёл РОВНО ОДНОГО одноимённого
    /// кандидата (чем чинить) И якорь встречается в карте РОВНО ОДИН раз вне кодовых
    /// заборов (где чинить). Реализовавший только первое получит запись не в ту строку,
    /// только второе — кнопку, ведущую в никуда.
    /// </summary>
    private static MapApplyPatch? BuildPatch(MapLinkFinding f, string mainText)
    {
        // Предмет правки ровно один — корневая карта (Р4). Находка вложенной карты
        // кнопки не получает: адреса файла в теле apply нет и не будет
        if (f.Path != MainMapName || f.Candidates.Count != 1) return null;

        var anchor = f.AnchorText;
        if (anchor.Length == 0) return null;

        var candidate = f.Candidates[0];
        // Путь с пробелом, скобкой, решёткой или угловой скобкой в markdown-ссылку без
        // экранирования не кладётся: «починка» сделала бы ссылку битой по-новому —
        // «docs/C#-гайд.md» разберётся как адрес «docs/C» с фрагментом. Редкий случай,
        // решение человека
        if (candidate.Any(c => char.IsWhiteSpace(c) || c is '(' or ')' or '#' or '<' or '>' or '"'))
            return null;

        // Место замены — АДРЕС ссылки, и он ищется по структуре, а не поиском пути по
        // тексту: в «[docs/a.md](docs/a.md)» путь стоит ещё и в подписи, а в
        // «[x](a.md#a.md)» — ещё и в якоре, и поиск «последнего вхождения» переписал бы
        // якорь вместо адреса. Адрес по CommonMark (и по LinkRegex) начинается сразу за
        // «](» с точностью до пробелов
        var open = anchor.LastIndexOf("](", StringComparison.Ordinal);
        if (open < 0) return null;
        var i = open + 2;
        while (i < anchor.Length && char.IsWhiteSpace(anchor[i])) i++;
        if (string.CompareOrdinal(anchor, i, f.Target, 0, f.Target.Length) != 0) return null;
        var after = string.Concat(anchor.AsSpan(0, i), candidate, anchor.AsSpan(i + f.Target.Length));

        // Второе условие формулы: неуникальный якорь не расширяется контекстом ради
        // уникальности (человек видел бы в «было → стало» одно, а в файл уезжало бы
        // другое), а обнуляет патч
        if (MapAnchorText.Offsets(mainText, anchor).Count != 1) return null;

        // Before и AnchorText — одна строка под двумя именами: первое для показа, второе
        // для поиска в файле
        return new MapApplyPatch(anchor, after, anchor);
    }

    // Импорт, который не раскрылся: файла нет. Отдельная находка, а не «пропущено» —
    // человек уверен, что правило едет в контекст, и молчание тут дороже всего.
    //
    // Раскрывается ОДНА карта — корневая (именно её размер платится контекстом каждого
    // хода), поэтому номер строки ищется в её сыром тексте. Импорт второго уровня живёт
    // в чужом файле (`@rules/a.md` → `@rules/нет.md` внутри него): у такой находки Line
    // остаётся нулём, а Path указывает на настоящий источник строки
    private static List<MapLinkFinding> DeadImportFindings(string root,
        ParsedMap main, ClaudeMdExpansion expansion)
    {
        var result = new List<MapLinkFinding>();
        foreach (var missing in expansion.MissingImports)
        {
            var sourceRelative = Relative(root, missing.SourceFile);
            var line = string.Equals(main.Path, sourceRelative, StringComparison.OrdinalIgnoreCase)
                ? ImportLine(main.Text, missing.Target)
                : 0;
            result.Add(new MapLinkFinding(sourceRelative, null, line, missing.Target, "deadImport", [],
                $"@{missing.Target}"));
        }
        return result;
    }

    // Забор учитывается и здесь: внутри блока кода строка «@путь» импортом не является,
    // и номер её строки указал бы на пример вместо настоящего импорта ниже
    private static int ImportLine(string text, string target)
    {
        var lineNo = 0;
        var fence = new MarkdownFence();
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.TrimEnd('\r');
            if (fence.Consume(line) || fence.InFence) continue;
            var trimmed = line.Trim();
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

            // Кандидат ищется по РЕЗОЛВНУТОМУ пути, а не по сырому тексту ссылки:
            // «docs/моя%20карта.md» дало бы имя с процент-энкодингом и кандидата не нашло
            dead.Add(Finding(map, link, target, "notFound", walk.Candidates(resolved)));
        }
    }

    private static MapLinkFinding Finding(ParsedMap map, RawLink link, string target, string reason,
        IReadOnlyList<string>? candidates = null) =>
        new(map.Path, link.Section, link.Line, target, reason, candidates ?? [], link.Raw);

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

    // Raw — ссылка целиком, как она написана в карте («[текст](путь)»): якорь находки
    private readonly record struct RawLink(string Target, string Raw, int Line, string? Section, int? SectionStart);

    private sealed record RawSection(string Title, int StartLine, int Lines, string FirstLine);

    private sealed record ParsedMap(string Path, string Text, int Lines, long Bytes,
        IReadOnlyList<RawSection> Sections, IReadOnlyList<RawLink> Links, bool UnclosedFence);

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

        return ParseText(relativePath, text, bytes);
    }

    // Разбор карты из текста. bytes — размер файла на диске; при разборе текста в памяти
    // считается по кодировке (BOM в эту величину не входит, разница в три байта видна
    // только в оценке токенов и там несущественна)
    private static ParsedMap ParseText(string relativePath, string text, long? bytes = null)
    {
        var sections = new List<RawSection>();
        var links = new List<RawLink>();
        var fence = new MarkdownFence();
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
            // внутри примера markdown не секция, а [текст](путь) там — не связь.
            // Закрытие по CommonMark (тот же символ, длина не меньше): карта, документирующая
            // формат карт, содержит вложенные примеры заборов
            if (fence.Consume(line) || fence.InFence) continue;

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
                links.Add(new RawLink(target, m.Value, lineNo, curTitle, curTitle is null ? null : curStart));
            }
        }

        CloseSection(curEnd);

        return new ParsedMap(relativePath, text, CountLines(text),
            bytes ?? Encoding.UTF8.GetByteCount(text), sections, links,
            UnclosedFence: fence.InFence);
    }

    // Строки как их считает wc -l: завершающий перевод строки новой строки не создаёт
    private static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var n = text.Count(c => c == '\n');
        return text[^1] == '\n' ? n : n + 1;
    }

    // ---------- обход дерева ----------

    // Вложенные карты и указатель «имя файла → где он лежит» для кандидатов починки.
    // Truncated — обход оборвался предохранителем, набор файлов неполон
    private sealed record WalkResult(IReadOnlyList<MapFileRef> NestedMaps,
        IReadOnlyDictionary<string, List<string>> ByName, bool Truncated)
    {
        // Ровно один одноимённый файл ⇒ механическая починка возможна; ноль или два ⇒ пусто.
        // Кандидата ищет СКАНЕР, а не модель: обе мёртвые ссылки нашего репозитория
        // чинятся при неработающей LLM.
        // resolvedPath — путь ссылки от корня проекта (уже декодированный и нормализованный)
        public IReadOnlyList<string> Candidates(string resolvedPath)
        {
            // Обход оборван — «файл в проекте ровно один» уже не факт, а совпадение: второй
            // мог просто не попасть в обход, и механическая правка ушла бы на неверный файл
            if (Truncated) return [];

            var name = resolvedPath;
            var i = name.LastIndexOf('/');
            if (i >= 0) name = name[(i + 1)..];
            if (name.Length == 0 || !ByName.TryGetValue(name, out var paths) || paths.Count != 1)
                return [];
            // Сравнение ТОЧНОЕ: путь, отличающийся от ссылки только регистром, на Linux
            // как раз и делает ссылку мёртвой — кандидат подсказывает верное написание
            return string.Equals(paths[0], resolvedPath, StringComparison.Ordinal) ? [] : paths;
        }
    }

    private static WalkResult Walk(string root, bool collectByName, CancellationToken ct)
    {
        var nested = new List<MapFileRef>();
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        var truncated = false;

        void Visit(string dir, string relativeDir, int depth)
        {
            if (seen >= MaxWalkFiles) { truncated = true; return; }
            if (depth > MaxWalkDepth) { truncated = true; return; }
            ct.ThrowIfCancellationRequested();

            string[] entries;
            try { entries = Directory.GetFileSystemEntries(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

            foreach (var entry in entries)
            {
                if (seen >= MaxWalkFiles) { truncated = true; return; }
                var name = Path.GetFileName(entry);
                var rel = relativeDir.Length == 0 ? name : $"{relativeDir}/{name}";

                bool isDir;
                try { isDir = Directory.Exists(entry); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                // Каталоги считаются наравне с файлами: дерево из одних каталогов (или
                // цикл симлинков без файлов) иначе не упёрся бы в потолок вовсе
                seen++;

                if (isDir)
                {
                    if (SkipDirNames.Contains(name)) continue;
                    // Рабочие деревья агентов — копии репозитория целиком: в отчёте они
                    // дали бы вторые экземпляры всех карт и всех кандидатов
                    if (rel.Equals(".claude/worktrees", StringComparison.OrdinalIgnoreCase)) continue;
                    // Символическая ссылка на каталог: Directory.Exists для неё true, и
                    // «latest -> .» увёл бы обход в бесконечный цикл
                    if (IsLink(entry)) continue;
                    Visit(entry, rel, depth + 1);
                    continue;
                }

                if (collectByName)
                {
                    if (!byName.TryGetValue(name, out var paths)) byName[name] = paths = [];
                    paths.Add(rel);
                }

                // Корневая карта и её двойник в .claude/ — не «вложенные»: они и есть предмет отчёта
                if (!name.Equals(MainMapName, StringComparison.OrdinalIgnoreCase)) continue;
                if (relativeDir.Length == 0 || relativeDir.Equals(".claude", StringComparison.OrdinalIgnoreCase))
                    continue;
                try { nested.Add(new MapFileRef(rel, CountLines(File.ReadAllText(entry)), new FileInfo(entry).Length)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        try { Visit(root, "", 0); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return new WalkResult([.. nested.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase)],
            byName, truncated);
    }

    // Символическая ссылка или junction (reparse point). Не прочиталось — считаем обычным
    // каталогом: пропустить настоящее поддерево дороже, чем лишний раз войти в него
    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).LinkTarget is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or SecurityException)
        {
            return false;
        }
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
