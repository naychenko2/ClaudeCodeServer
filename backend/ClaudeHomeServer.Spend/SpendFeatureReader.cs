using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// Реализация шва расхода по фиче (ISpendFeatureReader, Core) поверх SpendStore.
//
// Срез фичи: запись попадает, если её задача входит в `taskIds` ИЛИ её чат входит
// в `sessionIds` (ИЛИ, не И: ход оркестраторского чата задачи не имеет — его ловит
// чат; задача, исполнявшаяся в чате вне списка, ловится задачей). Оба набора пусты —
// срез пуст. Фильтр `OwnerId == ownerId` — защита в глубину: потребитель сам знает
// владельца, но чужая запись с верным task/session-id в срез не должна попадать.
//
// Деньги (решение Андрея 20.10):
//  • локальные модели (`Source == free`) — стоимость НАДЁЖНО 0: даже если сырая
//    запись несёт ненулевую цену (CLI может её отчитать), она принудительно 0;
//  • модели с ненадёжной ценой от CLI (DeepSeek, GLM, …) — токены идут в сумму,
//    стоимость НЕ идёт; модель попадает в `HiddenCostModels`, `CostPartial`
//    выставляется.
//
// Предикат «цена ненадёжна» — единая точка правды БЕЗ зависимости Spend → Llm:
// конфиг `Spend:UnreliableCostModels` (список подстрок/идентификаторов моделей,
// регистронезависимо). Секция отсутствует или пуста — встроенный дефолт
// (deepseek, glm — модели, названные в решении). Предикат оценивается на ЧТЕНИИ
// по имени модели из записи, а не запросом к Llm-реестру: прайс-таблица реестра
// — про подписку, а ненадёжна именно цена, которую CLI отчитал за стороннюю модель.
//
// Без двойного счёта: инвариант стора — день живёт либо в деталях, либо в дневных
// агрегатах (SpendStore). Читатель следует правилу стора: если день уже в
// агрегатах, детали этого дня пропускаются (это же закрывает окно rollup, когда
// день временно и там, и там).
//
// Дневные агрегаты за окном деталей (вопрос плана №7, уточнено по коду):
// `DailySpendRow` несёт PersonaId/Model/TaskId/SessionId с ПЕРВОЙ версии модели
// расхода (commit bac4030bc), и ключ свёртки включает все четыре — агрегаты
// полные, неполных строк нет; `From` помечает, с какой даты в срезе есть данные.
public sealed class SpendFeatureReader : ISpendFeatureReader
{
    private readonly SpendStore _store;
    // Подстроки имён моделей, в нижнем регистре: предикат — регистронезависимый Contains.
    // Дефолт (deepseek, glm) — в Core (SpendCostReliability.DefaultUnreliableModels), единая
    // точка правды с разрезом фичи (FeatureBreakdown).
    private readonly string[] _unreliableModels;

    // DI-конструктор (SpendStore + IConfiguration). Второй конструктор (store, string[]?) —
    // тестовый (прямая подача списка моделей) и `internal`: DI видит только этот public
    // (иначе два 2-аргументных конструктора — ambiguous, когда шов ISpendFeatureReader реально
    // активируется — TasksController, Б9b). Тестовые сборки видят internal через InternalsVisibleTo.
    public SpendFeatureReader(SpendStore store, IConfiguration config)
        : this(store, ResolveUnreliableModels(config)) { }

    // Прямой конструктор для тестов: стор и список моделей с ненадёжной ценой.
    internal SpendFeatureReader(SpendStore store, string[]? unreliableModels = null)
    {
        _store = store;
        _unreliableModels = (unreliableModels ?? SpendCostReliability.DefaultUnreliableModels)
            .Select(m => m.Trim().ToLowerInvariant())
            .Where(m => m.Length > 0)
            .ToArray();
    }

    private static string[] ResolveUnreliableModels(IConfiguration config)
    {
        var fromConfig = config.GetSection("Spend:UnreliableCostModels").Get<string[]>() ?? [];
        return fromConfig.Length > 0 ? fromConfig : SpendCostReliability.DefaultUnreliableModels;
    }

    public FeatureSpendTotals Totals(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string> excludeSessionIds,
        DateTime? from) =>
        ComputeTotals(Matched(ownerId, taskIds, sessionIds, excludeSessionIds, from));

    public IReadOnlyList<FeatureSpendRecord> Records(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string> excludeSessionIds,
        DateTime? from) =>
        [.. Matched(ownerId, taskIds, sessionIds, excludeSessionIds, from).OrderBy(r => r.Timestamp).ToList()];

    // Срез одним проходом (минор доработки Б9b): один Matched → и итог, и записи из одного
    // набора — без окна рассогласования между двумя отдельными сканами стора.
    public (FeatureSpendTotals Totals, IReadOnlyList<FeatureSpendRecord> Records) Slice(
        string ownerId, IReadOnlyCollection<string> taskIds, IReadOnlyCollection<string> sessionIds,
        IReadOnlyCollection<string> excludeSessionIds, DateTime? from)
    {
        var records = Matched(ownerId, taskIds, sessionIds, excludeSessionIds, from).OrderBy(r => r.Timestamp).ToList();
        return (ComputeTotals(records), records);
    }

    // Итог по набору записей среза: токены (вход/выход/кэш), стоимость (надёжная),
    // признак ненадёжной цены (единая точка правды SpendCostReliability), From (ранняя дата).
    private static FeatureSpendTotals ComputeTotals(IEnumerable<FeatureSpendRecord> records)
    {
        var totals = new FeatureSpendTotals { HiddenCostModels = [] };
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        DateOnly? earliest = null;

        foreach (var r in records)
        {
            // Минор Б9b: отрицательные/нулевые токены не дают отрицательных сумм — вклад
            // каждой записи кламится в ≥ 0 (сырая запись с отрицательным числом — сбой CLI,
            // не повод тянуть весь итог в минус).
            totals.InputTokens += Math.Max(0, r.InputTokens);
            totals.OutputTokens += Math.Max(0, r.OutputTokens);
            totals.CacheReadTokens += Math.Max(0, r.CacheReadTokens);
            totals.CacheCreationTokens += Math.Max(0, r.CacheCreationTokens);

            // Надёжная стоимость записи — единая точка правды (SpendCostReliability.ReliableCostOf):
            // 0 — free (надёжный 0), null — ненадёжно (флаг UnreliableCost / нет цены при
            // Source != free / NaN или ∞). В null: токены в сумму, стоимость — нет, CostPartial,
            // модель в hidden. Иначе запись без цены давала бы в итоге 0 (CostUsd ?? 0), а в
            // разрезе (FeatureBreakdown) null — два разных утверждения о цене в одном ответе
            // (ревью Глеба, major).
            var cost = SpendCostReliability.ReliableCostOf(r);
            if (cost is null)
            {
                if (r.Model is not null) hidden.Add(r.Model);
                totals.CostPartial = true;
            }
            else
            {
                totals.CostUsd += (double)cost;
            }

            var date = DateOnly.FromDateTime(r.Timestamp);
            if (earliest is null || date < earliest) earliest = date;
        }

        totals.From = earliest;
        totals.HiddenCostModels = [.. hidden.OrderBy(m => m, StringComparer.Ordinal)];
        return totals;
    }

    // Записи среза: дневные агрегаты (строка = день, Timestamp = начало дня) +
    // детальные ходы (точное время). День, уже свёрнутый в агрегаты, в деталях
    // пропускается — инвариант стора «день либо в деталях, либо в daily».
    private List<FeatureSpendRecord> Matched(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string> excludeSessionIds,
        DateTime? from)
    {
        var tasks = new HashSet<string>(taskIds ?? [], StringComparer.Ordinal);
        var sessions = new HashSet<string>(sessionIds ?? [], StringComparer.Ordinal);
        var exclude = new HashSet<string>(excludeSessionIds ?? [], StringComparer.Ordinal);
        if (tasks.Count == 0 && sessions.Count == 0) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowFrom = from is null ? DateOnly.MinValue : DateOnly.FromDateTime(from.Value);

        var result = new List<FeatureSpendRecord>();
        var dailyDates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in _store.DailyBetween(windowFrom, today))
        {
            if (!Match(d.OwnerId, d.TaskId, d.SessionId, ownerId, tasks, sessions, exclude)) continue;
            if (DateOnly.TryParse(d.Date, out var date)) dailyDates.Add(date.ToString("yyyy-MM-dd"));
            result.Add(ToRecord(d, date.ToDateTime(TimeOnly.MinValue)));
        }
        foreach (var r in _store.DetailsBetween(windowFrom, today))
        {
            if (from is not null && r.Timestamp < from) continue;
            if (!Match(r.OwnerId, r.TaskId, r.SessionId, ownerId, tasks, sessions, exclude)) continue;
            // День уже в агрегатах — детали дня не считаем (нет двойного счёта).
            if (dailyDates.Contains(r.Date.ToString("yyyy-MM-dd"))) continue;
            result.Add(ToRecord(r, r.Timestamp));
        }
        return result;
    }

    // Запись в срезе: её задача — одна из задач фичи, ИЛИ её чат — один из чатов фичи;
    // при этом её чат НЕ в `exclude` (исключение ГЛАВНЕЕ обеих осей: запись общего чата,
    // несущая TaskId задачи фичи, не должна входить в срез по taskIds — она уходит в долю).
    // Пустой набор = «фильтра по этой оси нет», но если оба набора пусты — срез пуст
    // (у фичи без задач и чатов расхода нет).
    private static bool Match(string ownerId, string? taskId, string? sessionId, string owner,
        HashSet<string> tasks, HashSet<string> sessions, HashSet<string> exclude) =>
        ownerId == owner
        && (sessionId is null || !exclude.Contains(sessionId))
        && ((taskId is not null && tasks.Contains(taskId))
          || (sessionId is not null && sessions.Contains(sessionId)));

    // Запись среза: признак «цена ненадёжна» выставляется единой точкой правды
    // (SpendCostReliability, список из конфига/дефолта) — его же читает разрез фичи.
    private FeatureSpendRecord ToRecord(SpendRecord r, DateTime timestamp) => new()
    {
        Timestamp = timestamp,
        SessionId = r.SessionId,
        TaskId = r.TaskId,
        PersonaId = r.PersonaId,
        Model = r.Model,
        InputTokens = r.InputTokens,
        OutputTokens = r.OutputTokens,
        CacheReadTokens = r.CacheReadTokens,
        CacheCreationTokens = r.CacheCreationTokens,
        CostUsd = r.CostUsd,
        Source = r.Source,
        UnreliableCost = SpendCostReliability.IsUnreliable(r.Model, r.Source, _unreliableModels),
    };

    // Дневная строка: IsDaily = true — признак выставляет ШОВ (единственный, кто знает, что
    // строка дневной агрегат, а не ход). Сплиттер (SharedSpendSplitter) по признаку делит строку
    // между фичами, активными в ЛЮБОЙ момент суток [d, d+1), и принудительно метод approx
    // (агрегат не несёт точного времени). Без признака строка делится как ход в 00:00,
    // активных в полночь нет → доля 0, расход свёрнутых суток теряется (ревью Глеба, major).
    private FeatureSpendRecord ToRecord(DailySpendRow d, DateTime timestamp) => new()
    {
        Timestamp = timestamp,
        SessionId = d.SessionId,
        TaskId = d.TaskId,
        PersonaId = d.PersonaId,
        Model = d.Model,
        InputTokens = d.InputTokens,
        OutputTokens = d.OutputTokens,
        CacheReadTokens = d.CacheReadTokens,
        CacheCreationTokens = d.CacheCreationTokens,
        CostUsd = d.CostUsd,
        Source = d.Source,
        UnreliableCost = SpendCostReliability.IsUnreliable(d.Model, d.Source, _unreliableModels),
        IsDaily = true,
    };
}
