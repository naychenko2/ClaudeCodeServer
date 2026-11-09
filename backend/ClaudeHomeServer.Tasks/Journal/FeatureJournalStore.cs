using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClaudeHomeServer.Services;

namespace ClaudeHomeServer.Services.Tasks.Journal;

// Журнал событий фичи (append-only, волна 3 «Обзор фич», Б8a).
// Файл: data/feature-journal/{ownerId}/{featureId}.jsonl — одна JSON-строка на событие.
// Источник точной статистики: ожидание, простой, запуски/остановки, ходы.
//
// Инварианты:
// - Путь — только через SafePath.Join (Core-примитив, не дублируем).
// - ownerId/featureId сверяются с белым списком ^[A-Za-z0-9_-]{1,128}$:
//   кривой id («..», слэш) не доходит до диска — отказ без исключения наружу.
// - Потолок 20 000 строк: при переполнении записи kind=turn больше не пишутся,
//   переходы (created/started/stopped/stop_cleared/stop_expired/waiting/waiting_cleared/
//   device_wait/device_wait_cleared/status/verdict/dropped) пишутся всегда — они редки (по числу
//   мутаций, не по числу ходов); чтение возвращает truncated=true (lines >= MaxLines).
// - Дедуп строк turn по (sessionId, turnSeq) при чтении; у turnSeq<=0 — по startedAt,
//   а если startedAt==null — по endedAt, затем по outcome (уникальность хода).
// - Потокобезопасность: запись под замком на featureId; сбой ловится и логируется,
//   наружу не летит (хранилище не должно ронять мутацию задачи).
// - Бэкап: каталог в data/ попадает в архив по умолчанию (BackupPaths.ShouldInclude → true);
//   BackupSchema.Version не поднимается, в исключения каталог не добавляется.

/// <summary>
/// Строка журнала: одно событие фичи. JSON camelCase, null-поля не сериализуются.
///
/// Контракт формата v:1 (контракт для Б5 timeline / Б6):
/// - <see cref="Kind"/> — словарь видов (см. поле): переходы задач + device_wait + turn.
/// - <see cref="From"/>/<see cref="To"/> — для status/verdict: ИМЕНА enum
///   (TaskItemStatus: Todo/InProgress/Done; TaskVerdicts: accepted/accepted_with_remarks/
///   returned/withdrawn), а не числовые коды.
/// - <see cref="TurnSeq"/> — номер хода; 0/null — ход без номера (дедуп по startedAt/endedAt).
/// - <see cref="StartedAt"/>/<see cref="EndedAt"/>/<see cref="Outcome"/> — интервалы:
///   работа = turn (startedAt..endedAt), ожидание = waiting..waiting_cleared,
///   простой-ожидание-устройства = device_wait..device_wait_cleared,
///   ошибка = turn.outcome=error (+errorClass) / stopped.reason.
/// - Потолок: 20 000 строк на файл; при переполнении пишутся ТОЛЬКО переходы,
///   turn пропускаются (truncated=true при чтении, см. Read).
/// </summary>
public sealed record FeatureJournalEntry
{
    /// <summary>Версия схемы строки (текущая — 1).</summary>
    [JsonPropertyName("v")]
    public int V { get; init; } = 1;

    /// <summary>Момент события (UTC).</summary>
    [JsonPropertyName("at")]
    public DateTime At { get; init; }

    /// <summary>
    /// Вид события: created | started | stopped | stop_cleared | stop_expired | waiting |
    /// waiting_cleared | device_wait | device_wait_cleared | status | verdict | dropped | turn.
    /// stop_expired — смена причины стопа на терминальную без движения метки (W4-HOLD-J);
    /// таймлайн его не читает, это запись причины для журнала.
    /// device_wait/device_wait_cleared — ожидание устройства локального проекта
    /// (DeviceWaitSince; добавлено в v:1, пока формат не застыл — рекомендация Александра).
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>Id задачи, к которой относится событие.</summary>
    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    /// <summary>Причина (текст) — для waiting/stopped/dropped.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Кто сделал действие (для hold_cleared: "queue" — автоснятие очереди фич; у ручного снятия
    /// поля нет). Необязательное: старые записи без него читаются как есть, версия формата v:1.
    /// </summary>
    [JsonPropertyName("by")]
    public string? By { get; init; }

    /// <summary>
    /// Старое значение (для status/verdict: от чего перешли). Значения — ИМЕНА enum:
    /// status → TaskItemStatus (Todo/InProgress/Done); verdict → TaskVerdicts
    /// (accepted/accepted_with_remarks/returned/withdrawn).
    /// </summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>
    /// Новое значение (для status/verdict: к чему перешли). Те же имена enum, что <see cref="From"/>.
    /// </summary>
    [JsonPropertyName("to")]
    public string? To { get; init; }

    /// <summary>Вердикт (для verdict: accepted / accepted_with_remarks / returned / withdrawn).</summary>
    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    /// <summary>Id сессии (для turn: чат, в котором идёт ход).</summary>
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    /// <summary>
    /// Порядковый номер хода (для turn). 0 — ход без номера (дедуп по startedAt).
    /// </summary>
    [JsonPropertyName("turnSeq")]
    public int? TurnSeq { get; init; }

    /// <summary>Момент начала (для started/turn).</summary>
    [JsonPropertyName("startedAt")]
    public DateTime? StartedAt { get; init; }

    /// <summary>Момент окончания (для stopped/turn).</summary>
    [JsonPropertyName("endedAt")]
    public DateTime? EndedAt { get; init; }

    /// <summary>Итог (для stopped/turn: finished / error / cancelled).</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    /// <summary>Класс ошибки (для turn с outcome=error).</summary>
    [JsonPropertyName("errorClass")]
    public string? ErrorClass { get; init; }

    /// <summary>Модель (для turn: какая модель выполняла ход).</summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

/// <summary>
/// Результат чтения журнала: список событий (дедуплицированные), признак «начинается с created»,
/// признак переполнения.
/// </summary>
public sealed record FeatureJournalReadResult
{
    public List<FeatureJournalEntry> Entries { get; init; } = [];
    public bool StartsWithCreated { get; init; }
    public bool Truncated { get; init; }
}

public sealed class FeatureJournalStore
{
    // Белый список id: GUID/строка id без спецсимволов (тот же паттерн, что FeatureIdPattern
    // в TasksController). Кривой id («..», слэш, >128) → отказ без исключения наружу.
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9_-]{1,128}$", RegexOptions.Compiled);

    // Потолок строк на файл: при переполнении turn-записи больше не пишутся.
    public const int MaxLines = 20_000;

    private readonly string _dir;
    private readonly ILogger<FeatureJournalStore>? _log;

    // Лок на featureId: сериализует append/read/delete одной фичи.
    // Ключ — "{ownerId}/{featureId}"; ConcurrentDictionary гарантирует атомарность GetOrAdd.
    private readonly ConcurrentDictionary<string, object> _locks = new();

    // Счётчик строк в памяти (под тем же замком на featureId): раньше потолок
    // пересчитывал файл построчно (CountLines) на КАЖДЫЙ turn-append — O(n) на горячем
    // пути (~2–4 МБ чтения на ход при 20 000 строк). Ключ — "{ownerId}/{featureId}".
    // Инициализация: при первом обращении считаем файл; далее — инкремент на каждый append.
    private readonly ConcurrentDictionary<string, int> _lineCounts = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FeatureJournalStore(IConfiguration config, ILogger<FeatureJournalStore>? log = null)
        : this(ResolveDir(config), log) { }

    // Прямой конструктор для тестов: произвольная папка.
    public FeatureJournalStore(string dir, ILogger<FeatureJournalStore>? log = null)
    {
        _dir = dir;
        _log = log;
    }

    private static string ResolveDir(IConfiguration config)
    {
        var dataPath = config["DataPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, "feature-journal");
    }

    /// <summary>
    /// Добавить запись в журнал фичи. Сбой не летит наружу (лог + return).
    /// При переполнении (≥ MaxLines) записи kind=turn пропускаются, переходы пишутся.
    /// </summary>
    public void Append(string ownerId, string featureId, FeatureJournalEntry entry)
    {
        try
        {
            if (!IsValidId(ownerId) || !IsValidId(featureId)) return;

            var path = GetPath(ownerId, featureId);
            var key = $"{ownerId}/{featureId}";
            var lockObj = _locks.GetOrAdd(key, _ => new object());

            lock (lockObj)
            {
                // Потолок: если файл уже ≥ MaxLines и запись — turn, пропускаем.
                // Переходы (created/started/stopped/…) пишутся всегда.
                // Счётчик — в памяти (инициализация при первом обращении), не перечитывание.
                if (entry.Kind == "turn" && GetLineCount(key, path) >= MaxLines) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOpts) + "\n");
                _lineCounts.AddOrUpdate(key, 1, (_, old) => old + 1);
            }
        }
        catch (Exception ex)
        {
            // Хранилище не должно ронять мутацию задачи: лог и дальше.
            _log?.LogWarning(ex, "FeatureJournalStore: не удалось записать {Owner}/{Feature}", ownerId, featureId);
        }
    }

    /// <summary>
    /// Прочитать журнал фичи. Возвращает дедуплицированные записи, признак «начинается с created»
    /// и признак переполнения. Если файла нет — пустой результат.
    /// </summary>
    public FeatureJournalReadResult Read(string ownerId, string featureId)
    {
        try
        {
            // Невалидный id («..», слэш, >128, пустой) → пустой результат БЕЗ обращения к
            // диску: иначе SafePath.Join(_dir, "{owner}/{featureId}.jsonl") уходит в соседний
            // каталог/файл и читает журнал ЧУЖОГО владельца (обход изоляции по владельцу).
            // Та же проверка, что в Append/Delete/SweepOrphans — fail-closed.
            if (!IsValidId(ownerId) || !IsValidId(featureId))
                return new FeatureJournalReadResult();
            var path = GetPath(ownerId, featureId);
            if (!File.Exists(path))
                return new FeatureJournalReadResult();

            var lines = File.ReadAllLines(path);
            // Потолок ограничивает ТОЛЬКО kind=turn: переходы (created/started/stopped/
            // stop_cleared/waiting/waiting_cleared/status/verdict/dropped) пишутся всегда —
            // они редки (по числу мутаций, не по числу ходов). Файл, дошедший до MaxLines,
            // уже теряет turn-записи (Append пропускает turn при >= MaxLines), поэтому
            // truncated = «пропущены ходы из-за потолка» = lines >= MaxLines, а не > MaxLines
            // (при ровно потолке > не срабатывало, хотя ходы уже терялись — мутация).
            var truncated = lines.Length >= MaxLines;

            // Парсим строки; битая строка (не JSON) пропускается.
            var entries = new List<FeatureJournalEntry>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<FeatureJournalEntry>(line, JsonOpts);
                    if (entry is not null) entries.Add(entry);
                }
                catch { /* битая строка — пропускаем */ }
            }

            // Дедуп turn: по (sessionId, turnSeq); у turnSeq=0 — по (sessionId, startedAt).
            // Не-turn записи не дедуплицируются.
            var deduped = DedupTurns(entries);

            var startsWithCreated = deduped.Count > 0 && deduped[0].Kind == "created";

            return new FeatureJournalReadResult
            {
                Entries = deduped,
                StartsWithCreated = startsWithCreated,
                Truncated = truncated,
            };
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "FeatureJournalStore: не удалось прочитать {Owner}/{Feature}", ownerId, featureId);
            return new FeatureJournalReadResult();
        }
    }

    /// <summary>
    /// Удалить файл журнала фичи. Если файла нет — no-op. Сбой не летит наружу.
    /// </summary>
    public void Delete(string ownerId, string featureId)
    {
        try
        {
            if (!IsValidId(ownerId) || !IsValidId(featureId)) return;

            var path = GetPath(ownerId, featureId);
            if (File.Exists(path))
            {
                File.Delete(path);
                // Счётчик строк сбрасываем вместе с файлом: иначе после удаления и
                // повторного создания фичи с тем же id потолок (turn пропускается)
                // считался бы от устаревшего значения.
                _lineCounts.TryRemove($"{ownerId}/{featureId}", out _);
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "FeatureJournalStore: не удалось удалить {Owner}/{Feature}", ownerId, featureId);
        }
    }

    /// <summary>
    /// Подмести файлы журнала без живого эпика. <paramref name="featureExists"/> — проверка,
    /// жив ли эпик с данным id.
    ///
    /// Инструмент, НЕ вызываемый из продакшена при старте (Б8-доработка-2): автоматическое
    /// подметание при старте было опасно — при испорченном/частично загруженном tasks.json
    /// оно сносило журналы владельцев, чьи задачи не поднялись (тотальная потеря истории).
    /// Вызывать можно ТОЛЬКО при подтверждённой ПОЛНОЙ загрузке задач (флаг полноты в
    /// JsonFileStore.Load пока неведён).
    /// </summary>
    public void SweepOrphans(string ownerId, Func<string, bool> featureExists)
    {
        try
        {
            if (!IsValidId(ownerId)) return;

            var ownerDir = SafePath.Join(_dir, ownerId);
            if (!Directory.Exists(ownerDir)) return;

            foreach (var file in Directory.GetFiles(ownerDir, "*.jsonl"))
            {
                var featureId = Path.GetFileNameWithoutExtension(file);
                if (!featureExists(featureId))
                {
                    try
                    {
                        File.Delete(file);
                        _lineCounts.TryRemove($"{ownerId}/{featureId}", out _);
                    }
                    catch { /* файл занят — оставим, подберём на следующем круге */ }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "FeatureJournalStore: SweepOrphans {Owner}", ownerId);
        }
    }

    // --- Вспомогательные ---

    private string GetPath(string ownerId, string featureId) =>
        SafePath.Join(_dir, $"{ownerId}/{featureId}.jsonl");

    private static bool IsValidId(string id) =>
        !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    // Счётчик строк: из памяти, если уже инициализирован; иначе — первичный подсчёт файла.
    // Вызывается под замком на featureId, поэтому race на инициализации нет.
    private int GetLineCount(string key, string path)
    {
        if (_lineCounts.TryGetValue(key, out var cached)) return cached;
        var count = CountLines(path);
        _lineCounts[key] = count;
        return count;
    }

    // Первичный подсчёт строк файла (без полного чтения): инициализация счётчика.
    private static int CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        var count = 0;
        using var reader = new StreamReader(path);
        while (reader.ReadLine() is not null) count++;
        return count;
    }

    /// <summary>
    /// Дедуп записей kind=turn: по (sessionId, turnSeq); у turnSeq=0 — по (sessionId, startedAt).
    /// Не-turn записи проходят без изменений. Порядок сохраняется.
    /// </summary>
    private static List<FeatureJournalEntry> DedupTurns(List<FeatureJournalEntry> entries)
    {
        var seen = new HashSet<string>();
        var result = new List<FeatureJournalEntry>(entries.Count);

        foreach (var e in entries)
        {
            if (e.Kind != "turn")
            {
                result.Add(e);
                continue;
            }

            // Ключ дедупа: (sessionId, turnSeq) при turnSeq>0; иначе (sessionId, startedAt);
            // если startedAt==null — endedAt, а если и он null — outcome. Старый ключ
            // `turn:{session}:0` (turnSeq<=0 && startedAt==null) схлопывал ВСЕ такие ходы
            // сессии в один (потеря строк) — мутация: два хода с turnSeq<=0, startedAt==null
            // и РАЗНЫМ endedAt больше не дубль.
            string key;
            if (e.TurnSeq is > 0)
                key = $"turn:{e.SessionId}:{e.TurnSeq}";
            else if (e.StartedAt is not null)
                key = $"turn:{e.SessionId}:s{e.StartedAt.Value.Ticks}";
            else if (e.EndedAt is not null)
                key = $"turn:{e.SessionId}:e{e.EndedAt.Value.Ticks}";
            else
                key = $"turn:{e.SessionId}:o{e.Outcome}";

            if (seen.Add(key))
                result.Add(e);
        }

        return result;
    }
}
