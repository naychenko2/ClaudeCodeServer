using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// ISpendCollector переехал в Core (`Core/Services/Spend/ISpendCollector.cs`,
// этап 5, шаг 1) — для разрыва цикла Llm ⇄ Spend.

// Хранилище аналитики расхода токенов. Форма согласована с инвариантом Артёма «новой БД
// не нужно» (team memory, техразрез 2026-07-24): файловое хранилище в data/, как остальные
// сторы проекта.
//   data/spend/turns-YYYY-MM-DD.jsonl — детальные записи (строка = SpendRecord), append-only;
//   data/spend/daily.json             — дневные агрегаты по всем разрезам (свёрнутые дни).
// Гибридная глубина: детально — последние Spend:DetailDays дней (дефолт 30), старше —
// RollupOlderThan сворачивает день в DailySpendRow и удаляет jsonl. Инвариант: день живёт
// ЛИБО в деталях, ЛИБО в daily — читатели выбирают источник по наличию дня в daily,
// двойного счёта нет. Все даты — по UTC.
public sealed class SpendStore : ISpendCollector, ISpendDetailReader
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

    // Самая ранняя дата с данными (детали и агрегаты); null — стор пуст.
    // Для клампа периода API: раньше первой записи ходить незачем.
    public DateOnly? EarliestDate
    {
        get
        {
            DateOnly? min = null;
            foreach (var key in _details.Keys)
                if (DateOnly.TryParse(key, out var d) && (min is null || d < min)) min = d;
            foreach (var key in _daily.Keys)
                if (DateOnly.TryParse(key, out var d) && (min is null || d < min)) min = d;
            return min;
        }
    }

    public void Record(SpendRecord record)
    {
        // Пустые записи не копим: ни токенов, ни генераций, ни суммы — аналитике нечего
        // показать. Сумма без генераций — догоняющая запись редактора картинок, её терять нельзя
        if (record.TotalTokens == 0 && record.Generations == 0
            && (record.CostUsd ?? 0) == 0 && (record.CostRub ?? 0) == 0 && (record.CostCredits ?? 0) == 0) return;

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

            if (!PersistDaily(next))
            {
                // daily.json не записан — сырьё остаётся нетронутым,
                // следующий проход rollup попробует записать снова
                _log?.LogWarning("spend: daily.json не записан, сырьё {Dates} остаётся до следующего прохода",
                    string.Join(", ", victims));
                return;
            }

            _daily = next;
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
            row.CostRub += r.CostRub ?? 0;
            row.CostCredits += r.CostCredits ?? 0;
            row.Generations += r.Generations;
            row.Turns += 1;
        }
        return [.. map.Values];
    }

    // --- переклассификация «локальный = бесплатный» ---

    // Ходы локальных провайдеров (IsLocal в конфиге), записанные до учёта этого признака
    // в SpendSources.IsFree, лежат в сторе как chat-turn/one-shot. Проход переставляет их
    // в "free" — и в деталях, и в дневных агрегатах (инвариант «день либо в деталях, либо
    // в daily» не затрагивается). Идемпотентен: уже бесплатные не трогает, ничего не
    // перевернулось — никакого обращения к диску.
    public int ReclassifyLocalAsFree(Func<string, bool> isLocalProvider) =>
        Reclassify((source, provider, model) => Reclassifiable(source, provider, model, isLocalProvider),
            SpendSources.Free);

    // Обратный проход: платный прямой адаптер («minimax-direct», «kimi-direct»), записанный
    // free по старому правилу «любой *-direct бесплатен», переводится в one-shot (прямой адаптер
    // зовут только фоновые one-shot). «freellm*-direct» и локальные провайдеры остаются free.
    // Условия взаимоисключающие с ReclassifyLocalAsFree (там IsFree=true, здесь false) —
    // пинг-понга в часовом цикле нет. Идемпотентен. Локальность спрашивается у реестра по
    // БАЗОВОМУ ключу провайдера (без «-direct»): записи «local-x-direct» остаются free.
    public int ReclassifyPaidDirectFromFree(Func<string, bool> isLocalProvider) =>
        Reclassify((source, provider, model) =>
                source == SpendSources.Free
                && provider.EndsWith(DirectSuffix, StringComparison.OrdinalIgnoreCase)
                && !SpendSources.IsFree(provider, model, isLocalProvider(provider[..^DirectSuffix.Length])),
            SpendSources.OneShot);

    private const string DirectSuffix = "-direct";

    private int Reclassify(Func<string, string, string?, bool> match, string target)
    {
        var flipped = 0;
        lock (_ioLock)
        {
            foreach (var (date, list) in _details)
            {
                List<SpendRecord> snapshot;
                bool changed = false;
                lock (list)
                {
                    foreach (var r in list)
                    {
                        if (!match(r.Source, r.Provider, r.Model)) continue;
                        r.Source = target;
                        changed = true;
                        flipped++;
                    }
                    snapshot = [.. list];
                }
                if (!changed) continue;
                try
                {
                    File.WriteAllText(TurnsPath(date),
                        string.Join(Environment.NewLine,
                            snapshot.Select(r => JsonSerializer.Serialize(r, JsonOpts)))
                        + Environment.NewLine);
                }
                catch (Exception ex)
                {
                    // Память уже исправлена — диск догонит при следующем проходе
                    _log?.LogError(ex, "spend: переклассификация — не переписать {Date}", date);
                }
            }

            flipped += ReclassifyDaily(match, target);
        }
        return flipped;
    }

    private static bool Reclassifiable(string source, string provider, string? model, Func<string, bool> isLocal) =>
        (source == SpendSources.ChatTurn || source == SpendSources.OneShot)
        && SpendSources.IsFree(provider, model, isLocal(provider));

    // Дневные агрегаты: после смены источника две строки могут сойтись по полному
    // составному ключу — их сливает MergeByKey (двойного счёта не должно быть).
    private int ReclassifyDaily(Func<string, string, string?, bool> match, string target)
    {
        Dictionary<string, List<DailySpendRow>>? next = null;
        var flipped = 0;
        foreach (var (date, rows) in _daily)
        {
            var changed = false;
            foreach (var r in rows)
            {
                if (!match(r.Source, r.Provider, r.Model)) continue;
                r.Source = target;
                changed = true;
                flipped++;
            }
            if (!changed) continue;
            next ??= new Dictionary<string, List<DailySpendRow>>(_daily);
            next[date] = MergeByKey(rows);
        }
        if (next is null) return 0;
        if (PersistDaily(next)) _daily = next;
        return flipped;
    }

    private static List<DailySpendRow> MergeByKey(List<DailySpendRow> rows)
    {
        var map = new Dictionary<(string, string?, string?, string?, string?, string, string?, string), DailySpendRow>();
        foreach (var r in rows)
        {
            var key = (r.OwnerId, r.ProjectId, r.SessionId, r.TaskId, r.PersonaId, r.Provider, r.Model, r.Source);
            if (map.TryGetValue(key, out var acc))
            {
                acc.InputTokens += r.InputTokens;
                acc.OutputTokens += r.OutputTokens;
                acc.CacheReadTokens += r.CacheReadTokens;
                acc.CacheCreationTokens += r.CacheCreationTokens;
                acc.CostUsd += r.CostUsd;
                acc.CostRub += r.CostRub;
                acc.CostCredits += r.CostCredits;
                acc.Generations += r.Generations;
                acc.Turns += r.Turns;
            }
            else map[key] = r;
        }
        return [.. map.Values];
    }

    // --- разовый rekey прямых пар: Claude-ключ + не-Claude модель (разрез c47fe6da, Ш3) ---

    // Записи, где Provider — ключ Claude, а Model — не родная модель Claude («claude» +
    // MiniMax-M3, «claude-2» + qwen3.8-27b): подписка Claude такие модели не обслуживает,
    // ход шёл к провайдеру, объявившему модель. Правда следует из самой записи, поэтому
    // проход меняет Provider в деталях и дневных агрегатах. Это исправление ЗАВЫШЕНИЯ:
    // «дорогой» Claude прятал реальный «дешёвый» или бесплатный ход. Обратные пары
    // (minimax + claude-*) и новые Claude-ключи из резолва не трогается: источника правды в
    // них нет, перевод был бы догадкой в сторону завышения. Идемпотентен: после rekey Provider
    // уже не Claude-ключ, повторный проход — no-op без обращения к диску. Сбой записи любого
    // turns-*.jsonl или daily.json пробрасывается наружу: маркер не ставится, следующий старт
    // повторит проход с диска. Суффикс «-direct» не срезается:
    // прямые адаптеры пишутся one-shot-путём и в фильтр не попадают.
    //
    // Порядок проверок важен: «родной Claude» спрашивается ДО резолва — сторонний провайдер,
    // объявивший модель с id вида claude-* или перехвативший префикс «claude-» (ghostcli),
    // не должен увести запись настоящей подписки к себе (тот же порядок требует
    // LlmProviderRegistry.IsNativeClaudeModel).
    public int RekeyNonClaudeModelsOnClaudeKeys(
        Func<string?, bool> isNativeClaudeModel, Func<string?, string?> providerByModel)
    {
        var rekeyed = 0;
        lock (_ioLock)
        {
            foreach (var (date, list) in _details)
            {
                List<SpendRecord> snapshot;
                bool changed = false;
                lock (list)
                {
                    foreach (var r in list)
                    {
                        if (RekeyNewProvider(r.Source, r.Provider, r.Model,
                                isNativeClaudeModel, providerByModel) is not string key
                            || key == r.Provider) continue;
                        r.Provider = key;
                        changed = true;
                        rekeyed++;
                    }
                    snapshot = [.. list];
                }
                if (!changed) continue;
                try
                {
                    // Как PersistDaily: tmp + Move(overwrite) — убийство процесса посреди
                    // записи не оставляет обрезанный день
                    var path = TurnsPath(date);
                    var tmp = path + ".tmp";
                    File.WriteAllText(tmp,
                        string.Join(Environment.NewLine,
                            snapshot.Select(r => JsonSerializer.Serialize(r, JsonOpts)))
                        + Environment.NewLine);
                    File.Move(tmp, path, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Проход одноразовый и без отката: сбой записи = провал прохода.
                    // Пробрасываем — сервис не поставит маркер, следующий старт повторит
                    // rekey с диска (в памяти уже исправлено, но оно перечитается с диска)
                    _log?.LogError(ex, "spend: rekey прямых пар — не переписать {Date}", date);
                    throw;
                }
            }

            rekeyed += RekeyDaily(isNativeClaudeModel, providerByModel);
        }
        return rekeyed;
    }

    // Новый провайдер записи или null (не трогать). chat-turn + Claude-ключ (через
    // единственный словарь SpendCostTiers) + не-родная модель + резолв модели даёт ключ,
    // который сам НЕ Claude-ключ: rekey к другому Claude-ключу — не выход из области Claude
    // (смысл прохода), а перенумеровывание записи между профилями; такую не трогаем.
    private static string? RekeyNewProvider(string source, string provider, string? model,
        Func<string?, bool> isNativeClaudeModel, Func<string?, string?> providerByModel)
    {
        if (source != SpendSources.ChatTurn || !SpendCostTiers.IsClaudeKey(provider) || isNativeClaudeModel(model))
            return null;
        var key = providerByModel(model);
        return key is not null && !SpendCostTiers.IsClaudeKey(key) ? key : null;
    }

    private int RekeyDaily(
        Func<string?, bool> isNativeClaudeModel, Func<string?, string?> providerByModel)
    {
        // После смены провайдера строка может сойтись с уже существующей (тот же ключ
        // с правильным провайдером) — их сливает MergeByKey, двойного счёта нет.
        Dictionary<string, List<DailySpendRow>>? next = null;
        var rekeyed = 0;
        foreach (var (date, rows) in _daily)
        {
            var changed = false;
            foreach (var r in rows)
            {
                if (RekeyNewProvider(r.Source, r.Provider, r.Model,
                        isNativeClaudeModel, providerByModel) is not string key
                    || key == r.Provider) continue;
                r.Provider = key;
                changed = true;
                rekeyed++;
            }
            if (!changed) continue;
            next ??= new Dictionary<string, List<DailySpendRow>>(_daily);
            next[date] = MergeByKey(rows);
        }
        if (next is null) return 0;
        if (!PersistDaily(next))
            // Первопричина уже залогирована в PersistDaily; здесь только проваливаем проход:
            // маркер не поставится, следующий старт повторит rekey с диска
            throw new IOException("spend: rekey прямых пар — не переписать daily.json");
        _daily = next;
        return rekeyed;
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

    // --- delta-backfill: маркер разовой миграции накопленных итогов в дельты хода (задача
    // ad52316b, 2026-10-12). Как и backfill.done — идемпотентный маркер: файл существует →
    // миграция уже прогонялась, повторный запуск no-op.
    private string DeltaBackfillMarkerPath => Path.Combine(_dir, "delta-backfill.done");

    public bool DeltaBackfillDone => File.Exists(DeltaBackfillMarkerPath);

    public void MarkDeltaBackfillDone()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(DeltaBackfillMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) { _log?.LogError(ex, "spend: не удалось записать маркер delta-backfill"); }
    }

    // --- provider-rekey-v1: маркер разового rekey прямых пар (разрез c47fe6da, Ш3).
    // Как backfill.done — идемпотентный маркер: файл существует → проход уже делался. ---

    private string ProviderRekeyMarkerPath => Path.Combine(_dir, "provider-rekey-v1.done");

    public bool ProviderRekeyDone => File.Exists(ProviderRekeyMarkerPath);

    public void MarkProviderRekeyDone()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(ProviderRekeyMarkerPath, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex) { _log?.LogError(ex, "spend: не удалось записать маркер rekey"); }
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

    // internal — для тестов: сбой записи (диск, права) → false → сырьё не удаляется.
    internal bool PersistDaily(Dictionary<string, List<DailySpendRow>> snapshot)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var tmp = DailyPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
            File.Move(tmp, DailyPath, overwrite: true);
            return true;
        }
        catch (Exception ex) { _log?.LogError(ex, "spend: не удалось записать daily.json"); return false; }
    }
}
