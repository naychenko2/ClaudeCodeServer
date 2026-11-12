using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudeHomeServer.Services;
using Microsoft.Extensions.Logging;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Стор разбора чатов Хранителя (ADR-035, решения 12а/12б): keeper/chat-triage.json —
// одна запись на чат: {sessionId, cursor, cursorHash, zone, reason, nothingNewStreak, triagedAt,
// score, signals, stage1Zone, stage1Reason}. Курсор — «разобрано до записи N», cursorHash — хэш
// записи N: несовпадение хэша (история переписана) сбрасывает курсор в 0 (ADR-035:249).
// nothingNewStreak — счётчик исходов «нечего добавить» подряд, по нему чат понижается в балле
// (штраф считает WikiChatScorer, ADR-035:269). Поля балла score/signals — с S6f: известные поля
// записи, читаются сильнотипно; запись остаётся расширяемой — неизвестные поля (будущие шаги)
// при правке сохраняются как есть.
//
// У стора ДВА независимых писателя, и владение полями разведено (ревью S6f, M1/M2): разбор
// (WikiChatIngestService) владеет cursor/cursorHash/zone/reason/nothingNewStreak, пересчёт
// (WikiChatsController.Recalculate) — score/signals и СВОЕЙ зоной ступени 1 в отдельных полях
// stage1Zone/stage1Reason. Зона одна на двоих быть не может: пересчёт затирал бы исход разбора
// nothing_new, а ночной разбор — посчитанный балл. Какую зону показать, решает читающий
// (WikiChatsController.List): пока разбор поставил nothing_new — она, иначе — зона ступени 1.
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

    // Имена известных полей записи — контракт S6e-1 + S6f. Всё, что не в этом списке, — чужое
    // поле (будущие шаги): оно сохраняется при правке записи как есть.
    private static readonly string[] KnownFieldNames =
        ["sessionId", "cursor", "cursorHash", "zone", "reason", "nothingNewStreak", "rejectStreak", "triagedAt",
         "score", "signals", "stage1Zone", "stage1Reason",
         "stage2Zone", "stage2Reason", "stage2HistoryCount"];

    // Поля, которыми владеет пересчёт (скорер), а не разбор: при Upsert разбора переносятся
    // из прежней записи как есть — ночной разбор не имеет права стирать посчитанный балл
    // и зону ступени 1 (ревью S6f, M2).
    private static readonly string[] ScorerFieldNames =
        ["score", "signals", "stage1Zone", "stage1Reason"];

    // Поля ступени 2 (N1a): вердикт модели по серому чату. Третий писатель стора — владеет
    // только ими; Upsert разбора переносит их из прежней записи так же, как поля скорера, а
    // UpdateScoring правит узел точечно и их не касается.
    private static readonly string[] Stage2FieldNames =
        ["stage2Zone", "stage2Reason", "stage2HistoryCount"];

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

    /// <summary>Поставить или обновить запись чата (исход разбора): весь цикл «прочитать стор →
    /// заменить запись → записать файл» под блоком корня. Отказ: форма записи (CodeShape — нет
    /// ключа, отрицательный курсор или счётчик), невалидный файл (CodeInvalid: битый файл не
    /// перезаписывается молча — его записи были бы стёрты). Запись на несуществующем чате —
    /// новая, на существующем — правка полей разбора: чужие поля (будущие шаги) и поля скорера
    /// (score/signals/stage1Zone/stage1Reason) переносятся из прежней записи как есть (M2).
    /// triagedAt ставит сервер (значение из входа игнорируется). Запись — через
    /// WikiFileWriter: атомарная, уникальный temp в нём уже есть.</summary>
    public static WikiTriageOpResult Upsert(string root, WikiChatTriageEntry entry, ILogger? log = null)
    {
        // Форма входа — вне блока: запись без ключа не должна держать корень.
        if (string.IsNullOrWhiteSpace(entry.SessionId))
            return Fail(CodeShape, "у записи нет sessionId — ключа чата");
        return Update(root, entry.SessionId, _ => entry, log);
    }

    /// <summary>Атомарное «прочитать → изменить → записать» записи разбора (C2R-Sh2): build
    /// получает запись, прочитанную ВНУТРИ блока корня (null — записи ещё нет), и возвращает
    /// новую; серия отказов и курсор считаются из неё, а не из прочтения, сделанного до блока
    /// (иначе параллельный разбор/пересчёт той же сессии теряет поле соседа). Перенос полей
    /// скорера, ступени 2 и чужих — как у Upsert. Форма результата проверяется здесь же.</summary>
    public static WikiTriageOpResult Update(
        string root, string sessionId, Func<WikiChatTriageEntry?, WikiChatTriageEntry> build, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Fail(CodeShape, "у записи нет sessionId — ключа чата");

        return WithRootLock<WikiTriageOpResult>(root, () =>
        {
            var (state, nodes, entries) = ReadAll(root);
            if (state == WikiTriageFileState.Invalid)
            {
                // Тот же fail-closed, что у RecordApplied: слить правку с нечитаемым файлом
                // и перезаписать его — значит молча стереть чужие курсоры (ADR-035:325).
                log?.LogWarning("Стор разбора чатов нечитаем, запись невозможна: {Path}", TriageRelPath);
                return Fail(CodeInvalid, "файл разбора чатов не читается — запись в него невозможна");
            }

            var current = entries.FirstOrDefault(e => e.SessionId == sessionId);
            var entry = build(current);
            if (entry.SessionId != sessionId)
                return Fail(CodeShape, "sessionId записи не совпадает с ключом обновления");
            if (entry.Cursor < 0)
                return Fail(CodeShape, $"курсор не может быть отрицательным: {entry.Cursor}");
            if (entry.NothingNewStreak < 0)
                return Fail(CodeShape, $"nothingNewStreak не может быть отрицательным: {entry.NothingNewStreak}");
            if (entry.RejectStreak < 0)
                return Fail(CodeShape, $"rejectStreak не может быть отрицательным: {entry.RejectStreak}");

            var stamped = entry with { TriagedAt = DateTimeOffset.UtcNow };
            var fresh = (JsonObject)JsonSerializer.SerializeToNode(stamped, JsonOpts)!;
            var idx = nodes.FindIndex(n => KeyOf(n) == stamped.SessionId);
            if (idx >= 0)
            {
                // Замена записи целиком канонической формой + перенос полей, которых этот
                // писатель не касается: неизвестные (поля будущих шагов) и поля скорера —
                // известные, но принадлежащие другому писателю (M2). Известные поля разбора
                // (включая rejectStreak) всегда в порядке записи (diff стора читаем).
                var old = (JsonObject)nodes[idx]!;
                foreach (var (key, value) in old)
                    if (!KnownFieldNames.Contains(key) || ScorerFieldNames.Contains(key)
                        || Stage2FieldNames.Contains(key))
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

    /// <summary>Обновить только поля скорера (S6f, разведение владения по ревью S6f M1):
    /// score/signals и зона с причиной ступени 1 (stage1Zone/stage1Reason) + дата сервера.
    /// Поля разбора — курсор, cursorHash, zone, reason, nothingNewStreak — НЕ трогаются:
    /// пересчёт (кнопка «пересчитать») не владеет ни ходом разбора, ни его исходом: зона
    /// nothing_new принадлежит разбору, и пересчёт не вправе её перекрыть своей zone.
    /// Записи нет — новая с курсором 0 (чат ещё ни разу не разбирался). Тот же цикл под
    /// блоком корня и тот же fail-closed, что у Upsert.</summary>
    public static WikiTriageOpResult UpdateScoring(
        string root, string sessionId, int score, IReadOnlyList<WikiTriageSignal>? signals,
        string? stage1Zone, string? stage1Reason, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Fail(CodeShape, "у записи нет sessionId — ключа чата");
        if (score < 0)
            return Fail(CodeShape, $"балл не может быть отрицательным: {score}");

        return WithRootLock<WikiTriageOpResult>(root, () =>
        {
            var (state, nodes, _) = ReadAll(root);
            if (state == WikiTriageFileState.Invalid)
            {
                log?.LogWarning("Стор разбора чатов нечитаем, пересчёт невозможен: {Path}", TriageRelPath);
                return Fail(CodeInvalid, "файл разбора чатов не читается — запись в него невозможна");
            }

            var idx = nodes.FindIndex(n => KeyOf(n) == sessionId);
            if (idx >= 0)
            {
                // Правка только своих полей существующего узла: поля разбора (курсор, зона
                // исхода, streak) и чужие поля остаются как есть — их эта операция не касается.
                var obj = (JsonObject)nodes[idx]!;
                obj["score"] = score;
                obj["signals"] = signals is null
                    ? null
                    : (JsonNode)JsonSerializer.SerializeToNode(signals, JsonOpts)!;
                obj["stage1Zone"] = stage1Zone;
                obj["stage1Reason"] = stage1Reason;
                obj["triagedAt"] = DateTimeOffset.UtcNow;
            }
            else
            {
                var fresh = new WikiChatTriageEntry(sessionId, 0, null, null, null, 0, default,
                    score, signals, stage1Zone, stage1Reason);
                nodes.Add((JsonNode)JsonSerializer.SerializeToNode(fresh, JsonOpts)!);
            }
            nodes.Sort((a, b) => string.CompareOrdinal(KeyOf(a), KeyOf(b)));
            var written = JsonSerializer.Serialize(nodes, JsonOpts);
            WikiFileWriter.Write(root, TriageRelPath, written);
            // Пересбор сильнотипной записи по узлам в памяти (тот же ParseEntries): вызывающий
            // получает ровно то, что записано на диск. Null здесь невозможен (узлы собраны из
            // прочитанного файла правкой известных полей) — но и не додумывается: отказ с формой.
            var entries = ParseEntries(nodes);
            if (entries is null)
                return Fail(CodeShape, "запись пересчёта не читается после сборки");
            return Ok(entries.Single(e => e.SessionId == sessionId));
        });
    }

    /// <summary>Записать вердикт ступени 2 (N1a): stage2Zone/stage2Reason/stage2HistoryCount
    /// + дата сервера triagedAt — её ставит вердикт ступени 2 так же, как пересчёт
    /// (UpdateScoring): время последней записи в стор двигает любой писатель вердикта.
    /// Поля разбора, скорера и чужие не трогаются. stage2HistoryCount — число реплик человека
    /// на момент вердикта: по нему ночь переспросит модель, когда реплик стало на 3 больше.
    /// Записи нет — новая с курсором 0. Тот же цикл под блоком корня и тот же fail-closed, что
    /// у Upsert: нечитаемый стор — CodeInvalid, вердикт не пишется.</summary>
    public static WikiTriageOpResult UpdateStage2(
        string root, string sessionId, string stage2Zone, string? stage2Reason, int historyCount,
        ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Fail(CodeShape, "у записи нет sessionId — ключа чата");
        if (string.IsNullOrWhiteSpace(stage2Zone))
            return Fail(CodeShape, "у вердикта ступени 2 нет зоны");
        if (historyCount < 0)
            return Fail(CodeShape, $"stage2HistoryCount не может быть отрицательным: {historyCount}");

        return WithRootLock<WikiTriageOpResult>(root, () =>
        {
            var (state, nodes, _) = ReadAll(root);
            if (state == WikiTriageFileState.Invalid)
            {
                log?.LogWarning("Стор разбора чатов нечитаем, вердикт ступени 2 не записан: {Path}", TriageRelPath);
                return Fail(CodeInvalid, "файл разбора чатов не читается — запись в него невозможна");
            }

            var idx = nodes.FindIndex(n => KeyOf(n) == sessionId);
            if (idx >= 0)
            {
                var obj = (JsonObject)nodes[idx]!;
                obj["stage2Zone"] = stage2Zone;
                obj["stage2Reason"] = stage2Reason;
                obj["stage2HistoryCount"] = historyCount;
                obj["triagedAt"] = DateTimeOffset.UtcNow;
            }
            else
            {
                var fresh = new WikiChatTriageEntry(sessionId, 0, null, null, null, 0, default,
                    Stage2Zone: stage2Zone, Stage2Reason: stage2Reason, Stage2HistoryCount: historyCount);
                nodes.Add((JsonNode)JsonSerializer.SerializeToNode(fresh, JsonOpts)!);
            }
            nodes.Sort((a, b) => string.CompareOrdinal(KeyOf(a), KeyOf(b)));
            WikiFileWriter.Write(root, TriageRelPath, JsonSerializer.Serialize(nodes, JsonOpts));
            var entries = ParseEntries(nodes);
            if (entries is null)
                return Fail(CodeShape, "запись ступени 2 не читается после сборки");
            return Ok(entries.Single(e => e.SessionId == sessionId));
        });
    }

    /// <summary>Удалить запись чата (чистка стора пересчётом, хвост R-WIKI-PRIV п.3): записи
    /// приватных чатов, попавшие в стор до правила приватности, не читаются ни одним
    /// потребителем (List, BuildQueue) и не должны оставаться в файле. Тот же цикл под блоком
    /// корня и тот же fail-closed, что у правок: нечитаемый файл — CodeInvalid, файл на диске
    /// не трогается. Записи нет — удалять нечего: Ok без записи, файл не переписывается.</summary>
    public static WikiTriageOpResult Remove(string root, string sessionId, ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return Fail(CodeShape, "у записи нет sessionId — ключа чата");

        return WithRootLock<WikiTriageOpResult>(root, () =>
        {
            var (state, nodes, entries) = ReadAll(root);
            if (state == WikiTriageFileState.Invalid)
            {
                log?.LogWarning("Стор разбора чатов нечитаем, удаление невозможно: {Path}", TriageRelPath);
                return Fail(CodeInvalid, "файл разбора чатов не читается — удаление из него невозможно");
            }

            var idx = nodes.FindIndex(n => KeyOf(n) == sessionId);
            if (idx < 0)
                return Ok(null);

            // Записи соответствуют узлам (тот же порядок ParseEntries): удаляется узел целиком
            // со всеми его полями — и разбора, и балла: данные чужого чата не остаются в файле.
            var removed = entries[idx];
            nodes.RemoveAt(idx);
            WikiFileWriter.Write(root, TriageRelPath, JsonSerializer.Serialize(nodes, JsonOpts));
            return Ok(removed);
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
                // Балл S6f: необязателен (запись S6e без него читается), но если есть —
                // неотрицательное число (скорер нижнюю границу балла держит на 0). signals —
                // массив {name, weight}; чужая форма — тот же класс повреждения файла.
                var score = ReadInt(obj, "score");
                if (score is < 0)
                    return null;
                // Вердикт ступени 2: необязателен; счётчик истории — неотрицательный.
                var stage2Count = ReadInt(obj, "stage2HistoryCount");
                if (stage2Count is < 0)
                    return null;
                // Серия отказов после ответа модели (C2R-Sh2): необязательна, старый файл без
                // поля читается как 0 (аддитивное поле, BackupSchema.Version не растёт).
                var rejectStreak = ReadInt(obj, "rejectStreak") ?? 0;
                if (rejectStreak < 0)
                    return null;
                var signalsNode = obj["signals"];
                List<WikiTriageSignal>? signals = null;
                if (signalsNode is not null && signalsNode.GetValueKind() != JsonValueKind.Null)
                {
                    signals = ReadSignals(signalsNode);
                    if (signals is null)
                        return null;
                }
                // Зона ступени 1 (доработка S6f): необязательна, как балл — запись до разведения
                // владения читается с null. Форма — та же, что у zone: чужой тип значения.
                entries.Add(new WikiChatTriageEntry(
                    sessionId, cursor.Value,
                    ReadString(obj, "cursorHash"),
                    ReadString(obj, "zone"),
                    ReadString(obj, "reason"),
                    streak, triagedAt.Value, score, signals,
                    ReadString(obj, "stage1Zone"),
                    ReadString(obj, "stage1Reason"),
                    ReadString(obj, "stage2Zone"),
                    ReadString(obj, "stage2Reason"),
                    stage2Count, rejectStreak));
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

    // signals (S6f): вызывающий передаёт узел, который НЕ null и не JSON null. Строгая форма:
    // массив объектов {name: непустая строка, weight: число}; чужая форма — null, и чтение
    // трактует её как невалидный файл. Пустой массив — законен (балл без сигналов).
    // Отрицательный weight законен: штраф за ряд «нечего добавить» — сигнал с отрицательным
    // весом (WikiChatScorer).
    private static List<WikiTriageSignal>? ReadSignals(JsonNode node)
    {
        if (node is not JsonArray arr)
            return null;
        var list = new List<WikiTriageSignal>();
        foreach (var item in arr)
        {
            if (item is not JsonObject o)
                return null;
            var name = ReadString(o, "name");
            if (string.IsNullOrEmpty(name))
                return null;
            var weight = ReadInt(o, "weight");
            if (weight is null)
                return null;
            list.Add(new WikiTriageSignal(name, weight.Value));
        }
        return list;
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

    // Entry nullable: Remove возвращает Ok без записи, когда удалять нечего (файл не тронут).
    private static WikiTriageOpResult Ok(WikiChatTriageEntry? entry) => new(true, "", null, entry);
    private static WikiTriageOpResult Fail(string code, string reason) => new(false, code, reason, null);
}

/// <summary>Запись стора. Поля разбора: курсор чата и его исход (zone/reason,
/// nothingNewStreak). С S6f — и результат ступени 1: балл (score, null — балла ещё нет:
/// запись S6e или чат до первого пересчёта), сигналы (null — тех же двух случаях) и зона с
/// причиной ступени 1 (stage1Zone/stage1Reason) — ОТДЕЛЬНО от зоны разбора: у стора два
/// писателя, и общая на них zone означала бы, что пересчёт затирает исход nothing_new, а
/// ночной разбор — посчитанный балл (ревью S6f, M1/M2). Форма полей строгая: значение чужого
/// типа — невалидный файл. С N1a — вердикт ступени 2 (stage2Zone/stage2Reason и число реплик
/// человека на момент вердикта stage2HistoryCount): третий писатель, свои поля.</summary>
public sealed record WikiChatTriageEntry(
    string SessionId,
    int Cursor,
    string? CursorHash,
    string? Zone,
    string? Reason,
    int NothingNewStreak,
    DateTimeOffset TriagedAt,
    int? Score = null,
    IReadOnlyList<WikiTriageSignal>? Signals = null,
    string? Stage1Zone = null,
    string? Stage1Reason = null,
    string? Stage2Zone = null,
    string? Stage2Reason = null,
    int? Stage2HistoryCount = null,
    int RejectStreak = 0);

/// <summary>Один сигнал ступени 1 в записи стора: имя — стабильный идентификатор из
/// WikiChatScorer (метку для человека рисует вызывающий), вес — вклад в балл (штраф —
/// отрицательный).</summary>
public sealed record WikiTriageSignal(string Name, int Weight);

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
