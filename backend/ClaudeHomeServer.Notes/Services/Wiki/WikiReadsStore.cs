using System.Collections.Concurrent;
using System.Text.Json;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// keeper/reads.json — счётчик чтений страниц вики владельца (ADR-039, решение 7): по каждой
// странице — раздельные счётчики по путям чтения и время последнего чтения. Это НЕ pages/:
// пишет его код путей чтения (шаги R2/R4/R6), а не Хранитель — инвариант единственного
// писателя ADR-035 (решение 2) при этом цел, как у keeper/settings.json (WikiSettingsStore).
//
// Формат — словарь по ключу страницы (PageKey — путь под pages/, тот же синтаксис, что
// параметр page у GET /api/wiki/page). Порядок записей фиксирован по ключу: diff файла между
// периодами должен быть читаемым, а не зависеть от порядка вызовов.
//
// Состояния файла — тот же класс, что у стора разбора чатов (WikiChatTriageStore): отсутствие
// файла — норма (пустой Valid: учёт ещё не начинался); пустой/битый файл, чужой V, кривая
// запись — «невалиден» с причиной, не исключение наружу и не «пустой стор»: на нечитаемом сторе
// правило архива (R5) объявило бы половину вики спящей. Запись в невалидный файл невозможна:
// слить правку с ним — значит молча стереть чужие счётчики (тот же fail-closed, что у
// WikiSettingsStore.Read и WikiChatTriageStore.Upsert).
//
// Конкурентность: семафор по ПОЛНОМУ пути файла, а не по экземпляру (стор статический, его
// зовут из нескольких точек одного процесса по разным корням). Read-modify-write целиком под
// замком, файл читается внутри замка, без кэша между вызовами: иначе два одновременных вызова
// теряют записи друг друга. Отказ любой IO-ошибки — false: учёт чтения не должен ронять ход,
// в котором что-то прочли. Чтение (Read) — без замка: запись атомарна (temp→Move,
// WikiFileWriter), читатель видит либо прежний файл, либо новый целиком.
public static class WikiReadsStore
{
    public const int ModelVersion = 1;
    private const string ReadsRelPath = "keeper/reads.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Ключи записей — пути страниц с русскими именами папок («Общее/…»): \u-escape сделал
        // бы файл нечитаемым, а его правят руками при разборе учёта.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    /// <summary>Счётчик одной страницы: число чтений по каждому пути и время последнего
    /// чтения по трём группам. LastRead — любое чтение (по нему R5 считает dormant);
    /// LastRecall — только слабыми (recall приходит сам по похожести, пользу не доказывает);
    /// LastStrongRead — только сильными (Query, Read, Human). Разделение держится с первой
    /// записи: вес путей в правиле архива (Открытое ADR-039, п. 4) решается по уже собранной
    /// базовой линии, переписывать стор под новый вес нельзя — потеряется то, что мерили.</summary>
    public sealed record PageReads(
        int Recall,
        int Query,
        int Read,
        int Human,
        DateTimeOffset? LastRead,
        DateTimeOffset? LastRecall,
        DateTimeOffset? LastStrongRead);

    /// <summary>Снимок стора. Valid=false — файл есть, но не читается: Pages пуст, половина
    /// прочитанного притворилась бы половиной учёта. Since — момент первой записи файла
    /// (null, пока файла нет): по нему R5 не считает страницы архивом, пока у владельца нет
    /// базовой линии («учёт начат меньше 30 дней назад», ADR-039, решение 7).</summary>
    public readonly record struct Snapshot(
        bool Valid,
        string? Reason,
        DateTimeOffset? Since,
        IReadOnlyDictionary<string, PageReads> Pages);

    /// <summary>Прочитать стор. Файла нет — пустой валидный снимок (учёт не начинался).
    /// Файл не читается — Valid=false с причиной, данных нет: вызывающий по состоянию решает
    /// не показывать счётчики, исключения наружу нет.</summary>
    public static Snapshot Read(string root)
    {
        var (valid, reason, since, pages) = ReadFile(SafePath.Join(root, ReadsRelPath));
        if (!valid)
            return new Snapshot(false, reason, null, EmptyPageReads());

        var reads = new Dictionary<string, PageReads>(StringComparer.Ordinal);
        foreach (var (key, p) in Ordered(pages))
            reads[key] = new PageReads(p.Recall, p.Query, p.Read, p.Human, p.LastRead, p.LastRecall, p.LastStrongRead);
        return new Snapshot(true, null, since, reads);
    }

    /// <summary>Записать состоявшиеся чтения: каждый элемент входа даёт +1 своей странице по
    /// своему пути. Всё «прочитать файл → изменить → записать» — под семафором файла; файл
    /// читается здесь, а не берётся из снимка, сделанного вне замка раньше: решавший по
    /// устаревшему состоянию не затрёт чужую запись. Отказ: кривый PageKey или чужой вид
    /// чтения (файл не трогается),
    /// невалидный файл (его записи были бы стёрты), любая IO-ошибка. Пустой вход — true без
    /// записи: «ничего не прочли» не должно выглядеть как «учёт начат». Возвращает false —
    /// ничего не записано; исключение наружу не выходит.</summary>
    public static bool RecordMany(string root, IEnumerable<(string PageKey, WikiReadKind Kind)> reads, DateTimeOffset now)
    {
        // Форма входа — до замка: кривый ключ не должен держать файл. Вход материализуется
        // один раз: ленивый IEnumerable читался бы дважды (проверка и запись).
        var items = reads.ToList();
        foreach (var (pageKey, kind) in items)
        {
            if (!IsValidPageKey(pageKey))
                return false;
            if (!Enum.IsDefined(kind))
                return false;
        }
        if (items.Count == 0)
            return true;

        var full = SafePath.Join(root, ReadsRelPath);
        var sem = _fileLocks.GetOrAdd(full, _ => new SemaphoreSlim(1));
        sem.Wait();
        try
        {
            var (valid, _, since, current) = ReadFile(full);
            if (!valid)
                return false;

            foreach (var (pageKey, kind) in items)
                current[pageKey] = Add(current.TryGetValue(pageKey, out var p) ? p : EmptyPage(), kind, now);

            // Since — момент первой записи: ставится один раз, при создании файла, и больше
            // не двигается ни одной последующей записью (дата из входа не переносит начало учёта).
            var data = new FileData(ModelVersion, since ?? now, Ordered(current));
            WikiFileWriter.Write(root, ReadsRelPath, JsonSerializer.Serialize(data, JsonOpts));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    // +1 ровно по своему пути; время — только вперёд: «последнее чтение», отставшая запись не
    // должна отматывать страницу к архиву. Сильный путь — любой кроме recall (ADR-039:
    // «приходит сам по похожести»); любое чтение двигает LastRead, recall — только LastRecall.
    private static PageData Add(PageData cur, WikiReadKind kind, DateTimeOffset now)
    {
        var strong = kind != WikiReadKind.Recall;
        return new PageData(
            cur.Recall + (kind == WikiReadKind.Recall ? 1 : 0),
            cur.Query + (kind == WikiReadKind.Query ? 1 : 0),
            cur.Read + (kind == WikiReadKind.Read ? 1 : 0),
            cur.Human + (kind == WikiReadKind.Human ? 1 : 0),
            Later(cur.LastRead, now),
            strong ? cur.LastRecall : Later(cur.LastRecall, now),
            strong ? Later(cur.LastStrongRead, now) : cur.LastStrongRead);
    }

    private static DateTimeOffset Later(DateTimeOffset? cur, DateTimeOffset now) =>
        cur is null || now > cur.Value ? now : cur.Value;

    // Чтение и проверка файла: «файла нет» — Valid с пустым словарём (до первой записи это одно
    // и то же состояние); пустой/битый JSON, чужой V, отсутствие since, кривая запись — Invalid.
    private static (bool Valid, string? Reason, DateTimeOffset? Since, Dictionary<string, PageData> Pages) ReadFile(string full)
    {
        if (!File.Exists(full))
            return (true, null, null, NoPages());

        var text = ReadOrNull(full);
        if (text is null)
            return (false, "файл учёта чтений не читается", null, NoPages());
        if (string.IsNullOrWhiteSpace(text))
            return (false, "файл учёта чтений пуст", null, NoPages());

        try
        {
            var data = JsonSerializer.Deserialize<FileData>(text, JsonOpts);
            if (data is null || data.V != ModelVersion)
                return (false, "файл учёта чтений другой версии", null, NoPages());
            // since обязателен в записанном файле: его ставит только этот стор, и без него
            // «начало учёта» стало бы догадкой.
            if (data.Since is null || data.Pages is null)
                return (false, "в файле учёта чтений нет начала учёта или словаря страниц", null, NoPages());

            var pages = new Dictionary<string, PageData>(StringComparer.Ordinal);
            foreach (var (key, page) in data.Pages)
            {
                if (!IsValidPageKey(key) || page is null || !IsValidPage(page))
                    return (false, $"запись страницы «{key}» не читается", null, NoPages());
                pages[key] = page;
            }
            return (true, null, data.Since, pages);
        }
        catch (JsonException)
        {
            return (false, "файл учёта чтений не разобран", null, NoPages());
        }
    }

    // Запись страницы целая: счётчики неотрицательные, и у любого непустого счётника есть
    // своё время — запись «прочитано 5 раз, когда — неизвестно» не дала бы ни dormant, ни вес
    // путей. Null вместо времени допустим только там, где счётчик нулевой.
    private static bool IsValidPage(PageData p)
    {
        if (p.Recall < 0 || p.Query < 0 || p.Read < 0 || p.Human < 0)
            return false;
        if (p.Recall + p.Query + p.Read + p.Human > 0 && p.LastRead is null)
            return false;
        if (p.Recall > 0 && p.LastRecall is null)
            return false;
        if (p.Query + p.Read + p.Human > 0 && p.LastStrongRead is null)
            return false;
        return true;
    }

    // PageKey — тот же рубеж, что у GET /api/wiki/page (WikiController.Page): путь под pages/,
    // принадлежащий разделу (WikiSections.Parse), и не служебная проекция. Ключ вне pages/
    // нечем пометить: его никто не покажет и по нему нельзя решить ничего.
    private static bool IsValidPageKey(string? pageKey) =>
        !string.IsNullOrWhiteSpace(pageKey)
        && !WikiPaths.IsProjection(pageKey)
        && WikiSections.Parse(pageKey) is not null;

    private static Dictionary<string, PageData> Ordered(Dictionary<string, PageData> pages)
    {
        var sorted = new Dictionary<string, PageData>(StringComparer.Ordinal);
        foreach (var key in pages.Keys.OrderBy(k => k, StringComparer.Ordinal))
            sorted[key] = pages[key];
        return sorted;
    }

    private static Dictionary<string, PageData> NoPages() => new(StringComparer.Ordinal);

    private static Dictionary<string, PageReads> EmptyPageReads() => new(StringComparer.Ordinal);

    private static PageData EmptyPage() => new(0, 0, 0, 0, null, null, null);

    private static string? ReadOrNull(string full)
    {
        try { return File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    // Форма файла: v + since + словарь страниц. Внутренние типы — только чтение/запись:
    // наружу отдаётся Snapshot (PageReads), чтобы вызывающий не держался за формат файла.
    private sealed record FileData(int V, DateTimeOffset? Since, Dictionary<string, PageData>? Pages);

    private sealed record PageData(
        int Recall,
        int Query,
        int Read,
        int Human,
        DateTimeOffset? LastRead,
        DateTimeOffset? LastRecall,
        DateTimeOffset? LastStrongRead);
}

/// <summary>Путь, которым страницу прочли (ADR-039, решение 7, таблица путей). Сила сигнала
/// разная: recall приходит сам по похожести и пользу не доказывает (слабый), остальные —
/// выбор человека или вызов, который он за собой держит (сильные).</summary>
public enum WikiReadKind
{
    /// <summary>Recall в ходе: страница попала в подсказку хода (NotesRecallContributor).
    /// Слабый: приходит сам по похожести.</summary>
    Recall,
    /// <summary>Запрос к вики по MCP <c>wiki_query</c> (NotesToolset.WikiQueryCall). Сильный.</summary>
    Query,
    /// <summary>Агент прочитал страницу: MCP <c>notes_read</c> по странице вики; тот же вид
    /// дают <c>notes_resolve</c> и <c>notes_thread</c> по странице вики — в ответе полный
    /// текст страницы. Сильный.</summary>
    Read,
    /// <summary>Открытие человеком: во вкладке «Вики» (GET <c>/api/wiki/page</c>) или в
    /// «Заметках» (GET <c>/api/notes/{id}</c> по вики-источнику). Сильный. Правка человека
    /// — не путь этого стора: правило архива считает её по самой странице (исключение из
    /// правила архива, ADR-039, решение 7).</summary>
    Human,
}
