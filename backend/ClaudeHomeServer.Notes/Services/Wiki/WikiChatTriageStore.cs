using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Стор разбора чатов Хранителя (ADR-035, решения 12а/12б): keeper/chat-triage.json —
// одна запись на чат: {sessionId, cursor, cursorHash, zone, reason, nothingNewStreak, triagedAt}.
// Курсор — «разобрано до записи N», cursorHash — хэш записи N: несовпадение хэша (история
// переписана) сбрасывает курсор в 0 (ADR-035:249). nothingNewStreak — счётчик исходов
// «нечего добавить» подряд, по нему чат понижается в балле (ADR-035:269). Поля балла
// (score, signals) добавит S6f: запись расширяемая — неизвестные поля при чтении не теряются
// при перезаписи, иначе S6e затёр бы то, что пишет другой шаг.
//
// Состояния файла — тот же класс, что у манифеста и пакета (ревью S2b-1, M1; S4a, MAJOR 2):
// пустой/битый файл — «невалиден» с причиной, не исключение наружу и не «пустой стор»:
// ночной разбор на нечитаемом сторе не должен начинать с нуля, молча потеряв все курсоры.
// null-поля внутри валидного JSON (sessionId: null, запись null в массиве) — тот же класс
// повреждения, не NRE. Отсутствие файла — норма: пустой Valid, стор до первого разбора.
// Предупреждение — через ILogger (без падения).
//
// Цикл «прочитал → изменил → записал» сериализован блокировкой на корень вики (ревью S4a,
// MAJOR 1): стор — один файл на все чаты владельца, без блокировки два параллельных разбора
// теряют записи друг друга (каждый пишет своё прочтение). Get — под тем же блоком: вызывающий
// видит завершённое состояние стора, а не состояние до идущей записи. Дата записи ставится
// сервером (ревью S4a, минор 6): значение из входа игнорируется.
public static class WikiChatTriageStore
{
    private const string TriageRelPath = "keeper/chat-triage.json";

    // Коды отказов — контракт для вызывающего (S6e-2), устойчивые строки, как wiki_* у
    // WikiPackageStore.
    public const string CodeInvalid = "wiki_triage_invalid";
    public const string CodeShape = "wiki_triage_shape";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Причина — текст для человека («почему чат пропущен»), он читается в хронике и
        // в разборе: \u-escapes делают её нечитаемой.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Имена известных полей записи — контракт S6e-1. Всё, что не в этом списке, — чужое поле
    // (балл S6f, будущие шаги): оно сохраняется при правке записи как есть.
    private static readonly string[] KnownFieldNames =
        ["sessionId", "cursor", "cursorHash", "zone", "reason", "nothingNewStreak", "triagedAt"];

    // Блокировка на корень вики владельца (ключ — как в контракте задачи), стиль WikiKeeper._locks
    // и WikiPackageStore._createLocks: весь цикл чтения-правки-записи внутри.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _rootLocks = new();

    private static T WithRootLock<T>(string root, Func<T> body)
    {
        var sem = _rootLocks.GetOrAdd(root, _ => new SemaphoreSlim(1));
        sem.Wait();
        try { return body(); }
        finally { sem.Release(); }
    }

    /// <summary>Прочитать стор: записи всех разобранных чатов. Отличает состояния файла
    /// (см. WikiTriageFileState): файла нет — норма (пусто, Valid); валиден — записи;
    /// пустой/битый/с null-полями — Invalid с пустым списком: исключение наружу не летит,
    /// вызывающий по состоянию решает не начинать разбор и предупреждает через log.</summary>
    public static WikiTriageReadResult Get(string root, ILogger? log = null)
    {
        return WithRootLock(root, () =>
        {
            var (state, _, entries) = ReadAll(root);
            if (state == WikiTriageFileState.Invalid)
                log?.LogWarning("Стор разбора чатов нечитаем: {Path}", TriageRelPath);
            return new WikiTriageReadResult(state, entries);
        });
    }

    /// <summary>Поставить или обновить запись чата: весь цикл «прочитать стор → заменить
    /// запись → записать файл» под блоком корня. Отказ: форма записи (CodeShape — нет ключа,
    /// отрицательный курсор или счётчик), невалидный файл (CodeInvalid: битый файл не
    /// перезаписывается молча — его записи были бы стёрты). Запись на несуществующем чате —
    /// новая, на существующем — правка известных полей: чужие поля записи сохраняются.
    /// triagedAt ставит сервер (значение из входа игнорируется). Запись — через
    /// WikiFileWriter: атомарная, уникальный temp в нём уже есть.</summary>
    public static WikiTriageOpResult Upsert(string root, WikiChatTriageEntry entry, ILogger? log = null)
    {
        // Форма входа — вне блока: запись без ключа не должна держать корень.
        if (string.IsNullOrWhiteSpace(entry.SessionId))
            return Fail(CodeShape, "у записи нет sessionId — ключа чата");
        if (entry.Cursor < 0)
            return Fail(CodeShape, $"курсор не может быть отрицательным: {entry.Cursor}");
        if (entry.NothingNewStreak < 0)
            return Fail(CodeShape, $"nothingNewStreak не может быть отрицательным: {entry.NothingNewStreak}");

        return WithRootLock<WikiTriageOpResult>(root, () =>
        {
            var (state, nodes, _) = ReadAll(root);
            if (state == WikiTriageFileState.Invalid)
            {
                // Тот же fail-closed, что у RecordApplied: слить правку с нечитаемым файлом
                // и перезаписать его — значит молча стереть чужие курсоры (ADR-035:325).
                log?.LogWarning("Стор разбора чатов нечитаем, запись невозможна: {Path}", TriageRelPath);
                return Fail(CodeInvalid, "файл разбора чатов не читается — запись в него невозможна");
            }

            var stamped = entry with { TriagedAt = DateTimeOffset.UtcNow };
            var fresh = (JsonObject)JsonSerializer.SerializeToNode(stamped, JsonOpts)!;
            var idx = nodes.FindIndex(n => KeyOf(n) == stamped.SessionId);
            if (idx >= 0)
            {
                // Замена записи целиком канонической формой + перенос чужих полей прежней:
                // известные поля всегда в порядке записи (diff стора читаем), неизвестные
                // (score/signals S6f) не теряются.
                var old = (JsonObject)nodes[idx]!;
                foreach (var (key, value) in old)
                    if (!KnownFieldNames.Contains(key))
                        fresh[key] = value?.DeepClone();
                nodes[idx] = fresh;
            }
            else
            {
                nodes.Add(fresh);
            }
            // Порядок записей фиксированный по ключу: стор — данные на диске и в бэкапе,
            // его diff между разборами должен быть читаемым, а не зависеть от порядка вставки.
            nodes.Sort((a, b) => string.CompareOrdinal(KeyOf(a), KeyOf(b)));
            WikiFileWriter.Write(root, TriageRelPath, JsonSerializer.Serialize(nodes, JsonOpts));
            return Ok(stamped);
        });
    }

    // Чтение и проверка файла: состояния три, но «файла нет» здесь — то же поведение для
    // вызывающего, что пустой список (до первого разбора), поэтому Valid без отдельного
    // Missing: у стора нет операции, зависящей от различия «пустого» и «ещё не созданного».
    private static (WikiTriageFileState State, List<JsonNode?> Nodes, IReadOnlyList<WikiChatTriageEntry> Entries) ReadAll(string root)
    {
        var full = SafePath.JoinBelowRoot(root, TriageRelPath);
        if (!File.Exists(full))
            return (WikiTriageFileState.Valid, [], []);
        var text = ReadOrNull(full);
        if (text is null || string.IsNullOrWhiteSpace(text))
            return (WikiTriageFileState.Invalid, [], []);
        try
        {
            var nodes = JsonSerializer.Deserialize<List<JsonNode?>>(text);
            if (nodes is null)
                return (WikiTriageFileState.Invalid, [], []);
            var entries = ParseEntries(nodes);
            if (entries is null)
                return (WikiTriageFileState.Invalid, [], []);
            return (WikiTriageFileState.Valid, nodes, entries);
        }
        catch (JsonException)
        {
            // Битый JSON или не массив — состояние «невалиден», не исключение: его ловит
            // фоновый читатель (ревью S2b-1, M1).
            return (WikiTriageFileState.Invalid, [], []);
        }
    }

    // Проверка формы записей по узлам JSON (сильнотипная модель одна на всех, как ShapeErrors
    // у пакета): null-запись, null/отсутствующий ключ, отрицательный курсор или счётчик,
    // отсутствующая дата, чужой тип значения, дубликат ключа — всё Invalid. Значение
    // отсутствует и значение null для необязательного поля — одно и то же (поле просто нет).
    // Возвращает null = невалиден; чужой тип значения ловится как InvalidOperationException.
    private static List<WikiChatTriageEntry>? ParseEntries(List<JsonNode?> nodes)
    {
        var entries = new List<WikiChatTriageEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var node in nodes)
            {
                if (node is not JsonObject obj)
                    return null;
                var sessionId = ReadString(obj, "sessionId");
                if (string.IsNullOrEmpty(sessionId) || !seen.Add(sessionId))
                    return null;
                var cursor = ReadInt(obj, "cursor");
                if (cursor is null || cursor < 0)
                    return null;
                var streak = ReadInt(obj, "nothingNewStreak") ?? 0;
                if (streak < 0)
                    return null;
                var triagedAt = ReadDate(obj, "triagedAt");
                if (triagedAt is null)
                    return null;
                entries.Add(new WikiChatTriageEntry(
                    sessionId, cursor.Value,
                    ReadString(obj, "cursorHash"),
                    ReadString(obj, "zone"),
                    ReadString(obj, "reason"),
                    streak, triagedAt.Value));
            }
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        return entries;
    }

    // null/отсутствие значения — null результата (indexer JsonObject отдаёт null и для
    // отсутствующего поля, и для JSON null — для этого стора это одно и то же); чужой тип —
    // InvalidOperationException (ловится вызывающим как невалидный файл).
    private static string? ReadString(JsonObject obj, string key)
    {
        var v = obj[key];
        if (v is null || v.GetValueKind() == JsonValueKind.Null)
            return null;
        return v.GetValue<string>();
    }

    private static int? ReadInt(JsonObject obj, string key)
    {
        var v = obj[key];
        if (v is null || v.GetValueKind() == JsonValueKind.Null)
            return null;
        return v.GetValue<int>();
    }

    private static DateTimeOffset? ReadDate(JsonObject obj, string key)
    {
        var v = obj[key];
        if (v is null || v.GetValueKind() == JsonValueKind.Null)
            return null;
        return v.GetValue<DateTimeOffset>();
    }

    // Ключ записи для поиска и порядка. После ReadAll все узлы — проверенные объекты со
    // строковым sessionId; форма функции — ради безопасного CompareOrdinal, а не нового NRE.
    private static string KeyOf(JsonNode? node) =>
        node is JsonObject obj && obj["sessionId"] is { } s && s.GetValueKind() == JsonValueKind.String
            ? s.GetValue<string>()
            : string.Empty;

    private static string? ReadOrNull(string full)
    {
        try { return File.ReadAllText(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static WikiTriageOpResult Ok(WikiChatTriageEntry entry) => new(true, "", null, entry);
    private static WikiTriageOpResult Fail(string code, string reason) => new(false, code, reason, null);
}

/// <summary>Запись стора: курсор разбора чата и его исход. Поля балла (score, signals)
/// добавит S6f — класс записи расширяемая, их несёт файл, а не эта модель.</summary>
public sealed record WikiChatTriageEntry(
    string SessionId,
    int Cursor,
    string? CursorHash,
    string? Zone,
    string? Reason,
    int NothingNewStreak,
    DateTimeOffset TriagedAt);

/// <summary>Состояние файла стора на диске: «невалиден» нельзя схлопнуть с «пусто» —
/// нечитаемый файл — это потерянные курсоры всех чатов, а не «ничего не разобрано»
/// (ревью S2b-1, M1). Отсутствие файла здесь — Valid: для вызывающего оно значит ровно
/// то же, что пустой список (до первого разбора).</summary>
public enum WikiTriageFileState
{
    Valid,
    /// <summary>Файл есть, но не читается как стор: пустой, битый JSON, не массив или
    /// запись с null-полем/дубликатом ключа. Запись в него невозможна.</summary>
    Invalid,
}

/// <summary>Результат чтения стора: состояние + записи. Записи значимы только при Valid.</summary>
public readonly record struct WikiTriageReadResult(WikiTriageFileState State, IReadOnlyList<WikiChatTriageEntry> Entries);

/// <summary>Результат записи: Ok + запись после постановки (с датой сервера), либо код отказа
/// и текст для вызывающего.</summary>
public readonly record struct WikiTriageOpResult(bool Ok, string Code, string? Reason, WikiChatTriageEntry? Entry);
