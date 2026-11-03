using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Журнал вики для человека (S7c-0, ADR-035 §12г меры 4–6): читает keeper/log.jsonl владельца и
// склеивает строки с пакетами — статус, политика «само / через Входящие», класс origin × op,
// страницы — и даёт имя источника. Только чтение: ни одного записи на диск эта служба не делает.
//
// Строки журнала — единственные носители события (apply/revert/retract/relink/adopt их только
// дописывают — WikiManifest.AppendLog), их порядок в файле и есть хронология (тот же закон, что
// у _log.md): «новые сверху» = обратный порядок файла, он же обратная хронология, и для равных
// ts он не зависит ни от чего ещё.
//
// Граница чтения — хвост: последние `take` строк (DefaultTake, потолка MaxTake). Весь файл в
// память не читается никогда: 50 000 строк журнала — не аргумент прочитать их все. Фильтры
// сужают прочитанный хвост, а не ищут по всей истории: окно take — то, что читается за один
// вызов (мера выдачи, её отдаёт наружу полем take).
//
// Имя источника — недоверенные данные: для чата — название чата этого владельца из каталога
// сессий (чужой и несуществующий — без имени, не отказ: по ответу нельзя отличить «нет» от
// «не твой»), для заметки — её заголовок. Без разметки, одной строкой, до MaxNameLength
// символов. Прочие origin — без имени: карточка источника — страница вики, её заголовок не имя
// источника, выдумывать его нечем.
public sealed class WikiJournalReader
{
    public const int DefaultTake = 200;
    public const int MaxTake = 1000;
    internal const int MaxNameLength = 120;

    // Тот же путь журнала, что у WikiManifest (его константа закрытая): строки читаются там же,
    // где они пишутся.
    private const string LogRelPath = "keeper/log.jsonl";
    // Шаг обратного чтения: хвост ищётся блоками, а не по байту.
    private const int BlockSize = 64 * 1024;

    private readonly NotesService _notes;
    private readonly ISessionDirectory _sessions;
    private readonly string _dataDir;

    public WikiJournalReader(NotesService notes, ISessionDirectory sessions, IConfiguration config)
    {
        _notes = notes;
        _sessions = sessions;
        // Каталог данных — как у всех сервисов вертикали (тот же корень, что у Хранителя).
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        _dataDir = Path.GetDirectoryName(Path.GetFullPath(dataPath))!;
    }

    /// <summary>Фильтры окна: раздел (ключ, проверенный контроллером), источник (origin из
    /// закрытого перечня), класс пары «источник × операция». Пустое — без фильтра.</summary>
    public readonly record struct Query(
        string? Section, string? Origin, string? Class, int? Take, bool OverrodeHuman = false);

    /// <summary>Запись журнала: событие строки log.jsonl и то, что о нём известно по пакету.
    /// Status/Policy/Classes пустые там, где пакета нет (relink, adopt — системные операции
    /// пакета не имеют): выдумывать их нечем, пустое поле честнее выдумки.</summary>
    public sealed record Entry(
        DateTimeOffset Ts,
        string Op,
        string Section,
        string Actor,
        string? Source,
        string? Origin,
        string? SourceName,
        IReadOnlyList<string> Pages,
        IReadOnlyList<string> Packages,
        string? Status,
        string? Policy,
        IReadOnlyList<string> Classes,
        bool OverrodeHuman = false,
        string? OverrodeKind = null);

    /// <summary>Вид действия ИИ над утверждением человека в строке `overrodeHuman` (ADR-038,
    /// предохранители 1 и 6): ИИ заменил формулировку (`supersede@human`).</summary>
    public const string KindReplaced = "replaced";
    /// <summary>ИИ не заменил, а оспорил утверждение человека (`conflict@human`).</summary>
    public const string KindDisputed = "disputed";

    /// <summary>Результат чтения. Take — фактическое окно после обрезки (клиентский take выше
    /// потолка урезается, и ответ должен показывать, какое окно читалось). LinesRead — сколько
    /// строк журнала реально прочитано: мера того, что хвост ограничен. InvalidLines — нечитаемые
    /// строки хвоста (обрыв посреди записи, битый JSON): их видно числом, они не притворяются
    /// записями — тот же приём, что у нечитаемых файлов пакета в «Входящих».</summary>
    public sealed record Result(
        IReadOnlyList<Entry> Entries,
        int Take,
        int LinesRead,
        int InvalidLines);

    public Result Read(string ownerId, Query query)
    {
        var take = ClampTake(query.Take);
        var root = WikiPaths.Root(_dataDir, ownerId);
        var path = SafePath.Join(root, LogRelPath);

        var parsed = new List<Entry>();
        var linesRead = 0;
        var invalidLines = 0;
        if (File.Exists(path))
        {
            // FileShare.ReadWrite: журнал дописывается Хранителем, и чтение не должно его
            // блокировать — хвост читается тем, что на диске сейчас.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Position = TailStart(fs, take);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                linesRead++;
                var entry = ParseLine(line);
                if (entry is null)
                {
                    // Пустая строка — не битая запись, а край файла (последний «\n» даёт
                    // пустую строку после себя): её числом не меряют.
                    if (line.Length > 0)
                        invalidLines++;
                    continue;
                }
                parsed.Add(entry);
            }
        }

        // Пакеты — один раз на вызов: они нужны только чтобы назвать состояние и класс того,
        // о чём уже сказала строка журнала.
        var store = parsed.Count > 0 ? WikiPackageStore.List(root) : new WikiPackageListResult([], []);
        var byId = new Dictionary<string, WikiPackage>(StringComparer.Ordinal);
        foreach (var p in store.Packages)
            byId.TryAdd(p.Id, p);

        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        var entries = new List<Entry>(parsed.Count);
        foreach (var e in parsed)
            entries.Add(JoinWithPackages(e, byId, ownerId, names));

        // Новые сверху — обратный порядок файла: строки дописываются append, его порядок и есть
        // хронология. Фильтры — по собранному хвосту: они сужают окно, а не ищут за его краем.
        var shown = new List<Entry>();
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (query.Section is not null && e.Section != query.Section) continue;
            if (query.Origin is not null && !OriginsOf(e.Source).Contains(query.Origin, StringComparer.Ordinal)) continue;
            if (query.Class is not null && !e.Classes.Contains(query.Class, StringComparer.Ordinal)) continue;
            // «ИИ поменял моё» (ADR-038, предохранитель 4): строки, где ИИ сам заменил или оспорил утверждение человека.
            if (query.OverrodeHuman && !e.OverrodeHuman) continue;
            shown.Add(e);
        }
        return new Result(shown, take, linesRead, invalidLines);
    }

    /// <summary>Страницы, откатанные человеком позже since (строго): записи журнала op∈{revert,
    /// retract} с actor="user", путь → самая поздняя дата. Actor обязателен: сторож старта
    /// и прочая автоматика пишут не «user» и анти-петлю (N7d) не включают. Журнал читается без
    /// записи и без блокировки: откат, дописанный посреди чтения, может не попасть в выборку —
    /// пакет, разобранный в эту долю секунды, применится само (допустимо: следующий разбор уже
    /// увидит строку). Битая строка и нечитаемый файл — пропуск без исключения.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ReadRecentHumanReverts(string root, DateTimeOffset since)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            var path = SafePath.Join(root, LogRelPath);
            if (!File.Exists(path))
                return result;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var e = ParseLine(line);
                if (e is null || e.Ts <= since || e.Actor != "user" || e.Op is not ("revert" or "retract"))
                    continue;
                foreach (var page in e.Pages)
                    if (!result.TryGetValue(page, out var seen) || e.Ts > seen)
                        result[page] = e.Ts;
            }
        }
        catch (IOException)
        {
            // Нечитаемый журнал не роняет разбор: правило не сработает на этот проход.
        }
        return result;
    }

    /// <summary>Операции журнала, считающиеся правкой человека (ADR-038, решение 4): `edit` пакета
    /// human и `restore` («Вернуть мою»). `adopt` — только «как мою правку» (B5): его строка несёт
    /// источник «human:{id пакета}», у «Принять как есть» источника нет и правкой он не считается
    /// (см. IsHumanEdit).
    /// Откат (`revert`, `retract`) — другой механизм (N7d), его читает ReadRecentHumanReverts.</summary>
    internal static readonly IReadOnlySet<string> HumanEditOps =
        new HashSet<string>(StringComparer.Ordinal) { "edit", "restore" };

    // Строка журнала — правка человека: actor=user и либо op из HumanEditOps, либо `adopt` «как
    // мою» (источник «human:…» — карточка пакета). Голый `adopt` («Принять как есть») без
    // источника — не правка: человек принял чужие байты, а не написал утверждения.
    private static bool IsHumanEdit(Entry e) =>
        e.Actor == "user"
        && (HumanEditOps.Contains(e.Op)
            || (e.Op == "adopt" && e.Source is { } src && src.StartsWith("human:", StringComparison.Ordinal)));

    /// <summary>Правка человека: путь страницы → время ПОСЛЕДНЕЙ его правки (ADR-038, решение 4,
    /// условие «источник новее правки»). Весь журнал, без окна: правка защищает формулировку,
    /// пока её не заместит источник новее. Строки с actor=user и op из HumanEditOps; автоматика
    /// (keeper и прочие) правкой человека не считается. Читает так же, как ReadRecentHumanReverts:
    /// без блокировки, битая строка и нечитаемый файл — пропуск без исключения (пусто — условие
    /// «источник новее правки» не срабатывает от журнала, а условие «новее цели» держится на
    /// неизменяемых карточках: вторая независимая опора).</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> ReadLastHumanEdits(string root)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            var path = SafePath.Join(root, LogRelPath);
            if (!File.Exists(path))
                return result;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var e = ParseLine(line);
                if (e is null || !IsHumanEdit(e))
                    continue;
                foreach (var page in e.Pages)
                    if (!result.TryGetValue(page, out var seen) || e.Ts > seen)
                        result[page] = e.Ts;
            }
        }
        catch (IOException)
        {
            // Нечитаемый журнал: пусто (см. шапку метода).
        }
        return result;
    }

    /// <summary>take вне окна — обрезается, а не отказ: окно журнала — мера выдачи, а не право
    /// вызова спорить с клиентом. Отсутствующий — DefaultTake.</summary>
    public static int ClampTake(int? take) => take switch
    {
        null => DefaultTake,
        < 1 => 1,
        > MaxTake => MaxTake,
        _ => take.Value,
    };

    // ─── Хвост файла ────────────────────────────────────────────────────────

    // Начало хвоста — offset первого байта последних `take` полных строк. Идём назад блоками и
    // считаем переводы строк: нашли (take+1)-й с конца — хвост начинается сразу за ним; дошли до
    // начала файла — полных строк в нём меньше take, хвост это весь файл. 0x0A в UTF-8
    // встречается только как перевод строки, поэтому счёт по байтам точен; память — один блок.
    private static long TailStart(FileStream fs, int take)
    {
        var buffer = new byte[BlockSize];
        var pos = fs.Length;
        var newlines = 0;
        while (pos > 0)
        {
            var len = (int)Math.Min(BlockSize, pos);
            pos -= len;
            fs.Position = pos;
            fs.ReadExactly(buffer.AsSpan(0, len));
            for (var i = len - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                newlines++;
                if (newlines > take)
                    return pos + i + 1;
            }
        }
        return 0;
    }

    // ─── Строка журнала ─────────────────────────────────────────────────────

    // Форма строки — та, что пишет WikiManifest: {ts, op, section, pages, source, actor, policy,
    // packages}. Нечитается и не становится записью: обрыв посреди записи, невалидный JSON,
    // объект без ts/op/section/actor или с не-датой в ts. Ни одно не роняет чтение: журнал —
    // витрина для человека, а не стор, и один испорченный ряд не должен стоить пустого экрана.
    private static Entry? ParseLine(string line)
    {
        if (line.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object)
                return null;
            if (!TryStr(obj, "ts", out var tsText) ||
                !DateTimeOffset.TryParse(tsText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                return null;
            if (!TryStr(obj, "op", out var op) || !TryStr(obj, "section", out var section) || !TryStr(obj, "actor", out var actor))
                return null;

            TryStr(obj, "source", out var source);
            TryStr(obj, "policy", out var policy);
            return new Entry(
                ts, op, section, actor, NullIfEmpty(source), OriginOf(source), null,
                StrArray(obj, "pages"), StrArray(obj, "packages"),
                null, NullIfEmpty(policy), [],
                obj.TryGetProperty("overrodeHuman", out var oh) && oh.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ─── Склейка с пакетами ─────────────────────────────────────────────────

    // Пакеты строки: названные ею (retract, а с SV1-В и apply — поле packages) или, для старых
    // строк apply/revert без id, — склейкой по источнику и разделу (см. выше).
    private static List<WikiPackage> PackagesOf(Entry e, Dictionary<string, WikiPackage> byId)
    {
        var joined = new List<WikiPackage>();
        if (e.Packages.Count > 0)
        {
            foreach (var id in e.Packages)
                if (byId.TryGetValue(id, out var p))
                    joined.Add(p);
        }
        else if (e.Source is not null)
        {
            var best = default(WikiPackage);
            foreach (var p in byId.Values)
            {
                if (p.Section != e.Section || p.CreatedAt > e.Ts || !SourceMatches(p, e.Source))
                    continue;
                if (best is null || p.CreatedAt > best.CreatedAt ||
                    (p.CreatedAt == best.CreatedAt && string.CompareOrdinal(p.Id, best.Id) > 0))
                    best = p;
            }
            if (best is not null)
                joined.Add(best);
        }
        return joined;
    }

    /// <summary>Что ИИ сам сделал с утверждением человека на странице: op — supersede | conflict,
    /// statement — текст ИИ без ссылок на сноски, previous — что заменялось или оспаривалось.</summary>
    public sealed record PageOverride(string Op, string? Statement, string? Previous, DateTimeOffset At, string PackageId);

    /// <summary>Метка «ИИ заменил моё» страницы: строки `apply` с overrodeHuman за окно [since, ∞)
    /// и автоприменённые операции их пакета с классом `…@human` на этой странице. Состояния у метки
    /// нет — она целиком выводится из журнала и пакетов; новые сверху.</summary>
    public IReadOnlyList<PageOverride> PageOverrides(string ownerId, string section, string page, DateTimeOffset since)
    {
        var root = WikiPaths.Root(_dataDir, ownerId);
        var result = new List<PageOverride>();
        // Журнал читается целиком построчно, без окна выдачи MaxTake: окно меток — по времени
        // (since), а не по числу строк, и хвост в 1000 строк не должен гасить метку раньше срока.
        var hits = new List<Entry>();
        try
        {
            var path = SafePath.Join(root, LogRelPath);
            if (File.Exists(path))
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var e = ParseLine(line);
                    if (e is null || !e.OverrodeHuman || e.Op != "apply" || e.Ts < since || e.Section != section
                        || !e.Pages.Contains(page, StringComparer.Ordinal))
                        continue;
                    hits.Add(e);
                }
            }
        }
        catch (IOException)
        {
            // Нечитаемый журнал — без меток, страница от этого не падает.
        }
        if (hits.Count == 0)
            return result;
        // Новые сверху — как у Read.
        hits.Reverse();
        var byId = new Dictionary<string, WikiPackage>(StringComparer.Ordinal);
        foreach (var p in WikiPackageStore.List(root).Packages)
            byId.TryAdd(p.Id, p);
        foreach (var e in hits)
        {
            if (e.Op != "apply" || e.Ts < since || !e.Pages.Contains(page, StringComparer.Ordinal))
                continue;
            foreach (var pkg in PackagesOf(e, byId))
                foreach (var op in pkg.Operations)
                {
                    if (op.Page != page || !WikiPackage.IsAutoOverrideOfHuman(op))
                        continue;
                    result.Add(new PageOverride(op.Op, StripFootnoteRefs(op.Text), StripFootnoteRefs(op.Target), e.Ts, pkg.Id));
                }
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex FootnoteRefRx =
        new(@"\s*\[\^[^\]\r\n]+\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? StripFootnoteRefs(string? text) => text is null ? null : FootnoteRefRx.Replace(text, "").Trim();

    // Строка журнала называет пакет по-разному: retract перечисляет его id прямо (мера 5),
    // apply/revert не называет id, но несёт ровно тот источник, который пишет о пакете
    // SourceOf, и его раздел. Этого достаточно, чтобы взять ОДИН пакет: тот, что создан не
    // позже строки и самый свежий из таких — применить дважды один пакет нельзя (таблица
    // переходов), а второй пакет того же источника имеет свою строку позже. Пакет, созданный
    // позже строки, — не этот: он ещё не применялся.
    private Entry JoinWithPackages(Entry e, Dictionary<string, WikiPackage> byId, string ownerId,
        Dictionary<string, string?> names)
    {
        var joined = PackagesOf(e, byId);

        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var p in joined)
            foreach (var op in p.Operations)
                if (!string.IsNullOrEmpty(op.Class))
                    classes.Add(op.Class);

        // Политика строки — из строки (её ставил код применения, мера 6). Её нет (revert,
        // системные операции) — берётся по пакету: «через Входящие», если хоть одна его
        // операция пришла оттуда. Пакета нет — политики нет: их не было у relink и adopt.
        var policy = e.Policy ?? (joined.Count == 0 ? null
            : joined.Any(p => p.Operations.Any(o => o.Decision == WikiPackage.DecisionInbox))
                ? WikiPackage.DecisionInbox
                : WikiPackage.DecisionAuto);

        // Вид действия — по автоприменённым операциям пакета с классом `…@human`. Пакет не найден
        // или строка без признака — вида нет (старые строки, системные операции). Замена и
        // оспаривание в одной строке — «replaced»: замена весомее.
        string? kind = null;
        if (e.OverrodeHuman)
        {
            var overrides = joined.SelectMany(p => p.Operations).Where(WikiPackage.IsAutoOverrideOfHuman).ToList();
            kind = overrides.Any(o => o.Op == WikiPackage.OpSupersede) ? KindReplaced
                : overrides.Any(o => o.Op == WikiPackage.OpConflict) ? KindDisputed
                : null;
        }

        return e with
        {
            OverrodeKind = kind,
            // Статус — только когда пакет один: строка retract могла откатать несколько, и
            // одного статуса у них нет — их id лежат в поле packages, их видно по ним.
            Status = joined.Count == 1 ? joined[0].Status : null,
            Policy = policy,
            Classes = [.. classes],
            SourceName = SourceNameOf(e, joined, ownerId, names),
        };
    }

    // Тот же источник, что записан в пакете: SourceOf — joined Ref его источников (форма,
    // которую строка журнала и несёт).
    private static bool SourceMatches(WikiPackage p, string source) =>
        string.Equals(
            string.Join(", ", p.Sources.Select(s => s.Ref)),
            source,
            StringComparison.Ordinal);

    // ─── Имя источника ──────────────────────────────────────────────────────

    // Имя берётся из карточки: у склеенного пакета она точная (Origin и Ref полями), без пакета
    // — из строки журнала тем же разбором формы, что у отката по источнику. Кэшируется на
    // вызов: одна хроника могла дать несколько строк, спрашивать её дважды нечем.
    private string? SourceNameOf(Entry e, List<WikiPackage> joined, string ownerId, Dictionary<string, string?> cache)
    {
        if (e.Source is null)
            return null;
        if (cache.TryGetValue(e.Source, out var cached))
            return cached;
        var name = ResolveName(e, joined, ownerId);
        cache[e.Source] = name;
        return name;
    }

    private string? ResolveName(Entry e, List<WikiPackage> joined, string ownerId)
    {
        string origin, id;
        if (joined.Count > 0 && joined[0].Sources is { Count: > 0 } sources)
        {
            origin = sources[0].Origin;
            id = IdOf(origin, sources[0].Ref);
        }
        else
        {
            origin = OriginOf(e.Source) ?? "";
            id = origin.Length == 0 ? "" : IdOf(origin, FirstSourcePart(e.Source!));
        }
        if (id.Length == 0)
            return null;

        switch (origin)
        {
            case WikiGate.OriginSession:
                var session = _sessions.GetById(id);
                // Чужой чат и несуществующий — один исход: имени нет. По нему нельзя
                // назвать, существует ли этот чат или он чужой (мера 5).
                if (session is null || _sessions.ResolveOwnerId(session) != ownerId)
                    return null;
                return NameOf(session.Name);
            case WikiGate.OriginNote:
                // Чужой заметки здесь нет по построению: GetDetail читает vault этого
                // владельца. Удалённой заметки нет — имени тоже нет.
                var note = _notes.GetDetail(ownerId, id);
                return note is null ? null : NameOf(note.Title);
            default:
                return null;
        }
    }

    // Имя — данные, не разметка: одна строка без управляющих символов и повторяющихся пробелов,
    // до MaxNameLength символов. Название чата писал человек, и оно не обязано быть одной
    // строкой — в выдаче оно ей становится.
    private static string? NameOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var sb = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace && sb.Length > 0)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
            if (sb.Length >= MaxNameLength)
                break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    // ─── Форма источника ────────────────────────────────────────────────────

    // Origin строки по её источнику. Строка может нести несколько карточек через «, » (так их
    // пишет SourceOf пакета): фильтр по источнику совпадает с любым из них, наружу отдаётся
    // первый — отдельного поля под множество строка журнала не несёт.
    private static List<string> OriginsOf(string? source)
    {
        var found = new List<string>();
        if (source is null)
            return found;
        foreach (var part in source.Split(", ", StringSplitOptions.None))
        {
            var origin = WikiRetractService.OriginOf(part);
            if (origin is not null && !found.Contains(origin, StringComparer.Ordinal))
                found.Add(origin);
        }
        return found;
    }

    private static string? OriginOf(string? source) =>
        source is null ? null : WikiRetractService.OriginOf(FirstSourcePart(source));

    private static string FirstSourcePart(string source)
    {
        var cut = source.IndexOf(", ", StringComparison.Ordinal);
        return cut < 0 ? source : source[..cut];
    }

    // Карточ после префикса origin, без диапазона: «session:{id}#m1-9» → «{id}». Ref пакета
    // префикс может и не нести (его пишет вызывающий) — тогда он берётся как есть.
    private static string IdOf(string origin, string refPart)
    {
        var rest = refPart.StartsWith(origin + ":", StringComparison.Ordinal) ? refPart[(origin.Length + 1)..] : refPart;
        var hash = rest.IndexOf('#');
        return hash < 0 ? rest : rest[..hash];
    }

    // ─── Мелкое ─────────────────────────────────────────────────────────────

    private static bool TryStr(JsonElement obj, string name, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            return false;
        value = el.GetString() ?? "";
        return value.Length > 0;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static IReadOnlyList<string> StrArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return [.. el.EnumerateArray()
            .Where(p => p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 })
            .Select(p => p.GetString()!)];
    }
}
