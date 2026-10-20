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
//   переходы пишутся; чтение возвращает truncated=true.
// - Дедуп строк turn по (sessionId, turnSeq) при чтении; у turnSeq=0 — по startedAt.
// - Потокобезопасность: запись под замком на featureId; сбой ловится и логируется,
//   наружу не летит (хранилище не должно ронять мутацию задачи).
// - Бэкап: каталог в data/ попадает в архив по умолчанию (BackupPaths.ShouldInclude → true);
//   BackupSchema.Version не поднимается, в исключения каталог не добавляется.

/// <summary>
/// Строка журнала: одно событие фичи. JSON camelCase, null-поля не сериализуются.
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
    /// Вид события: created | started | stopped | stop_cleared | waiting |
    /// waiting_cleared | status | verdict | dropped | turn.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>Id задачи, к которой относится событие.</summary>
    [JsonPropertyName("taskId")]
    public string? TaskId { get; init; }

    /// <summary>Причина (текст) — для waiting/stopped/dropped.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>Старое значение (для status/verdict: от чего перешли).</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>Новое значение (для status/verdict: к чему перешли).</summary>
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
                if (entry.Kind == "turn" && CountLines(path) >= MaxLines) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonOpts) + "\n");
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
            // [MUTATION] if (!IsValidId(ownerId) || !IsValidId(featureId))
            //     return new FeatureJournalReadResult();
            var path = GetPath(ownerId, featureId);
            if (!File.Exists(path))
                return new FeatureJournalReadResult();

            var lines = File.ReadAllLines(path);
            var truncated = lines.Length > MaxLines;

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
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "FeatureJournalStore: не удалось удалить {Owner}/{Feature}", ownerId, featureId);
        }
    }

    /// <summary>
    /// Подмести файлы журнала без живого эпика. Вызывается при старте (Б8b).
    /// <paramref name="featureExists"/> — проверка, жив ли эпик с данным id.
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
                    try { File.Delete(file); }
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

    private static int CountLines(string path)
    {
        if (!File.Exists(path)) return 0;
        // Считаем строки без полного чтения: достаточно для проверки потолка.
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

            // Ключ дедупа: (sessionId, turnSeq) или (sessionId, startedAt) при turnSeq=0/null.
            var key = e.TurnSeq is > 0
                ? $"turn:{e.SessionId}:{e.TurnSeq}"
                : $"turn:{e.SessionId}:{e.StartedAt?.Ticks ?? 0}";

            if (seen.Add(key))
                result.Add(e);
        }

        return result;
    }
}
