using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// Точка записи расхода для всех источников (ходы, one-shot, fal, бесплатные модели).
// Интерфейс, а не класс — чтобы точки сбора (SessionManager, раннеры, HTTP-клиенты)
// мокались в тестах без файлового стора.
public interface ISpendCollector
{
    void Record(SpendRecord record);
}

// Хранилище аналитики расхода токенов. Форма согласована с инвариантом Артёма «новой БД
// не нужно» (team memory, техразрез 2026-07-24): файловое хранилище в data/, как остальные
// сторы проекта.
//   data/spend/turns-YYYY-MM-DD.jsonl — детальные записи (строка = SpendRecord), append-only;
//   data/spend/daily.json             — дневные агрегаты по всем разрезам (свёрнутые дни).
// Гибридная глубина: детально — последние Spend:DetailDays дней (дефолт 30), старше —
// RollupOlderThan сворачивает день в DailySpendRow и удаляет jsonl. Инвариант: день живёт
// ЛИБО в деталях, ЛИБО в daily — читатели выбирают источник по наличию дня в daily,
// двойного счёта нет. Все даты — по UTC.
public sealed class SpendStore : ISpendCollector
{
    private readonly string _dir;
    private readonly ILogger<SpendStore>? _log;
    private readonly object _ioLock = new();
    // date(yyyy-MM-dd) → детальные записи дня; лок — на списке дня
    private readonly ConcurrentDictionary<string, List<SpendRecord>> _details = new();
    // Id всех детальных записей (живёт синхронно с _details): дедуп повторного Record —
    // прерванный backfill при рестарте пишет те же детерминированные Id, дубли не проходят
    private readonly ConcurrentDictionary<string, byte> _ids = new();
    // Снапшот дневных агрегатов; при записи заменяется целиком (читатели без блокировок)
    private volatile Dictionary<string, List<DailySpendRow>> _daily = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public int DetailDays { get; }

    public SpendStore(IConfiguration config, ILogger<SpendStore>? log = null)
        : this(ResolveDir(config), ParseDetailDays(config), log) { }

    // Прямой конструктор для тестов: произвольная папка и окно
    public SpendStore(string dir, int detailDays, ILogger<SpendStore>? log = null)
    {
        _dir = dir;
        _log = log;
        DetailDays = detailDays;
        Load();
    }

    private static string ResolveDir(IConfiguration config)
    {
        var dataPath = config["DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "data", "projects.json");
        return Path.Combine(Path.GetDirectoryName(dataPath)!, "spend");
    }

    private static int ParseDetailDays(IConfiguration config) =>
        int.TryParse(config["Spend:DetailDays"], out var d) && d > 0 ? d : 30;

    // Первый день детального окна (включительно): сегодня и (DetailDays-1) дней назад
    public DateOnly WindowStart => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-(DetailDays - 1));

    public void Record(SpendRecord record)
    {
        // Пустые записи не копим: ни токенов, ни генераций — аналитике нечего показать
        if (record.TotalTokens == 0 && record.Generations == 0) return;

        // Дедуп по Id: повторный импорт той же записи (оборванный backfill) — тихий no-op
        if (!_ids.TryAdd(record.Id, 0)) return;

        var date = record.Date.ToString("yyyy-MM-dd");
        var list = _details.GetOrAdd(date, _ => []);
        lock (list) list.Add(record);

        try
        {
            lock (_ioLock)
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(TurnsPath(date),
                    JsonSerializer.Serialize(record, JsonOpts) + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            // Запись уже в памяти — теряется только персистентность этой строки
            _log?.LogError(ex, "spend: не удалось дописать {Date}", date);
        }
    }

    // Детальные записи периода (только несвёрнутые дни). Снимок — безопасно для LINQ поверх.
    public List<SpendRecord> DetailsBetween(DateOnly from, DateOnly to)
    {
        var result = new List<SpendRecord>();
        foreach (var (date, list) in _details)
        {
            if (!DateOnly.TryParse(date, out var d) || d < from || d > to) continue;
            lock (list) result.AddRange(list);
        }
        return result;
    }

    // Дневные агрегаты периода (только свёрнутые дни)
    public List<DailySpendRow> DailyBetween(DateOnly from, DateOnly to)
    {
        var result = new List<DailySpendRow>();
        foreach (var (date, rows) in _daily)
        {
            if (!DateOnly.TryParse(date, out var d) || d < from || d > to) continue;
            result.AddRange(rows);
        }
        return result;
    }

    // Свёрнут ли день в агрегаты (для честной пометки «за окном» в API)
    public bool IsAggregated(DateOnly date) => _daily.ContainsKey(date.ToString("yyyy-MM-dd"));

    public SpendRecord? FindTurn(string id)
    {
        foreach (var (_, list) in _details)
            lock (list)
            {
                var hit = list.FirstOrDefault(r => r.Id == id);
                if (hit is not null) return hit;
            }
        return null;
    }

    // Свернуть все детальные дни СТРОГО старше cutoff (обычно WindowStart) в дневные агрегаты
    // и удалить их jsonl. Идемпотентно: повторный rollup дня заменяет его строки в daily целиком.
    // Порядок «сначала daily на диск, потом удаление jsonl» — при падении между шагами день
    // окажется и там и там, но читатели предпочитают daily, а следующий rollup дочистит файл.
    public void RollupOlderThan(DateOnly cutoff)
    {
        var victims = _details.Keys
            .Where(date => DateOnly.TryParse(date, out var d) && d < cutoff)
            .ToList();
        if (victims.Count == 0) return;

        lock (_ioLock)
        {
            var next = new Dictionary<string, List<DailySpendRow>>(_daily);
            foreach (var date in victims)
            {
                if (!_details.TryGetValue(date, out var list)) continue;
                List<SpendRecord> snapshot;
                lock (list) snapshot = [.. list];
                next[date] = Aggregate(date, snapshot);
            }
            _daily = next;
            PersistDaily(next);

            foreach (var date in victims)
            {
                if (_details.TryRemove(date, out var removed))
                    lock (removed)
                        foreach (var r in removed) _ids.TryRemove(r.Id, out _);
                try { File.Delete(TurnsPath(date)); }
                catch (Exception ex) { _log?.LogWarning(ex, "spend: не удалить {Date} после rollup", date); }
            }
        }
        _log?.LogInformation("spend: свёрнуто дней в агрегаты: {Count}", victims.Count);
    }

    // Свёртка записей дня по полному составному ключу разрезов
    internal static List<DailySpendRow> Aggregate(string date, IEnumerable<SpendRecord> records)
    {
        var map = new Dictionary<(string, string?, string?, string?, string?, string, string?, string), DailySpendRow>();
        foreach (var r in records)
        {
            var key = (r.OwnerId, r.ProjectId, r.SessionId, r.TaskId, r.PersonaId, r.Provider, r.Model, r.Source);
            if (!map.TryGetValue(key, out var row))
                map[key] = row = new DailySpendRow
                {
                    Date = date,
                    OwnerId = r.OwnerId,
                    ProjectId = r.ProjectId,
                    SessionId = r.SessionId,
                    TaskId = r.TaskId,
                    PersonaId = r.PersonaId,
                    Provider = r.Provider,
                    Model = r.Model,
                    Source = r.Source,
                };
            row.InputTokens += r.InputTokens;
            row.OutputTokens += r.OutputTokens;
            row.CacheReadTokens += r.CacheReadTokens;
            row.CacheCreationTokens += r.CacheCreationTokens;
            row.CostUsd += r.CostUsd ?? 0;
            row.Generations += r.Generations;
            row.Turns += 1;
        }
        return [.. map.Values];
    }

    // --- backfill: маркер разового импорта истории ---

    private string BackfillMarkerPath => Path.Combine(_dir, "backfill.done");

    public bool BackfillDone => File.Exists(BackfillMarkerPath);

    public void MarkBackfillDone()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(BackfillMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) { _log?.LogError(ex, "spend: не удалось записать маркер backfill"); }
    }

    // --- диск ---

    private string TurnsPath(string date) => Path.Combine(_dir, $"turns-{date}.jsonl");
    private string DailyPath => Path.Combine(_dir, "daily.json");

    private void Load()
    {
        try
        {
            if (File.Exists(DailyPath))
                _daily = JsonSerializer.Deserialize<Dictionary<string, List<DailySpendRow>>>(
                    File.ReadAllText(DailyPath), JsonOpts) ?? new();
        }
        catch (Exception ex) { _log?.LogWarning(ex, "spend: не удалось прочитать daily.json"); }

        if (!Directory.Exists(_dir)) return;
        foreach (var file in Directory.GetFiles(_dir, "turns-*.jsonl"))
        {
            var date = Path.GetFileNameWithoutExtension(file)["turns-".Length..];
            // Инвариант «день либо в деталях, либо в daily»: недоудалённый после rollup файл
            // (падение между шагами) не загружаем — его данные уже в агрегатах
            if (_daily.ContainsKey(date))
            {
                try { File.Delete(file); } catch { /* дочистится следующим rollup */ }
                continue;
            }
            var list = new List<SpendRecord>();
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        // Дедуп по Id и при загрузке: дубли, записанные до появления дедупа, схлопываются
                        if (JsonSerializer.Deserialize<SpendRecord>(line, JsonOpts) is { } r && _ids.TryAdd(r.Id, 0))
                            list.Add(r);
                    }
                    catch (JsonException) { /* битая строка (оборванный append) — пропускаем */ }
                }
            }
            catch (Exception ex) { _log?.LogWarning(ex, "spend: не удалось прочитать {File}", file); }
            if (list.Count > 0) _details[date] = list;
        }
    }

    private void PersistDaily(Dictionary<string, List<DailySpendRow>> snapshot)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = DailyPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, DailyPath, overwrite: true);
        }
        catch (Exception ex) { _log?.LogError(ex, "spend: не удалось записать daily.json"); }
    }
}
