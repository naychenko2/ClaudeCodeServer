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
    // Встроенный дефолт предиката «цена ненадёжна»: модели, названные в решении
    // Андрея (20.10). Конфиг `Spend:UnreliableCostModels` заменяет его.
    internal static readonly string[] DefaultUnreliableModels = ["deepseek", "glm"];

    private readonly SpendStore _store;
    // Подстроки имён моделей, в нижнем регистре: предикат — регистронезависимый Contains.
    private readonly string[] _unreliableModels;

    public SpendFeatureReader(SpendStore store, IConfiguration config)
        : this(store, ResolveUnreliableModels(config)) { }

    // Прямой конструктор для тестов: стор и список моделей с ненадёжной ценой.
    public SpendFeatureReader(SpendStore store, string[]? unreliableModels = null)
    {
        _store = store;
        _unreliableModels = (unreliableModels ?? DefaultUnreliableModels)
            .Select(m => m.Trim().ToLowerInvariant())
            .Where(m => m.Length > 0)
            .ToArray();
    }

    private static string[] ResolveUnreliableModels(IConfiguration config)
    {
        var fromConfig = config.GetSection("Spend:UnreliableCostModels").Get<string[]>() ?? [];
        return fromConfig.Length > 0 ? fromConfig : DefaultUnreliableModels;
    }

    public FeatureSpendTotals Totals(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, DateTime? from)
    {
        var totals = new FeatureSpendTotals { HiddenCostModels = [] };
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        DateOnly? earliest = null;

        foreach (var r in Matched(ownerId, taskIds, sessionIds, from))
        {
            totals.InputTokens += r.InputTokens;
            totals.OutputTokens += r.OutputTokens;
            totals.CacheReadTokens += r.CacheReadTokens;
            totals.CacheCreationTokens += r.CacheCreationTokens;

            if (IsUnreliable(r.Model))
            {
                // Токены уже в сумме выше, стоимость — нет: модель в список, CostPartial.
                if (r.Model is not null) hidden.Add(r.Model);
                totals.CostPartial = true;
            }
            else
            {
                // free — надёжно 0 (даже если сырая запись несёт цену), остальное — как есть.
                totals.CostUsd += r.Source == SpendSources.Free ? 0 : (r.CostUsd ?? 0);
            }

            var date = DateOnly.FromDateTime(r.Timestamp);
            if (earliest is null || date < earliest) earliest = date;
        }

        totals.From = earliest;
        totals.HiddenCostModels = [.. hidden.OrderBy(m => m, StringComparer.Ordinal)];
        return totals;
    }

    public IReadOnlyList<FeatureSpendRecord> Records(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, DateTime? from) =>
        [.. Matched(ownerId, taskIds, sessionIds, from).OrderBy(r => r.Timestamp).ToList()];

    // Записи среза: дневные агрегаты (строка = день, Timestamp = начало дня) +
    // детальные ходы (точное время). День, уже свёрнутый в агрегаты, в деталях
    // пропускается — инвариант стора «день либо в деталях, либо в daily».
    private List<FeatureSpendRecord> Matched(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, DateTime? from)
    {
        var tasks = new HashSet<string>(taskIds ?? [], StringComparer.Ordinal);
        var sessions = new HashSet<string>(sessionIds ?? [], StringComparer.Ordinal);
        if (tasks.Count == 0 && sessions.Count == 0) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowFrom = from is null ? DateOnly.MinValue : DateOnly.FromDateTime(from.Value);

        var result = new List<FeatureSpendRecord>();
        var dailyDates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in _store.DailyBetween(windowFrom, today))
        {
            if (!Match(d.OwnerId, d.TaskId, d.SessionId, ownerId, tasks, sessions)) continue;
            if (DateOnly.TryParse(d.Date, out var date)) dailyDates.Add(date.ToString("yyyy-MM-dd"));
            result.Add(ToRecord(d, date.ToDateTime(TimeOnly.MinValue)));
        }
        foreach (var r in _store.DetailsBetween(windowFrom, today))
        {
            if (from is not null && r.Timestamp < from) continue;
            if (!Match(r.OwnerId, r.TaskId, r.SessionId, ownerId, tasks, sessions)) continue;
            // День уже в агрегатах — детали дня не считаем (нет двойного счёта).
            if (dailyDates.Contains(r.Date.ToString("yyyy-MM-dd"))) continue;
            result.Add(ToRecord(r, r.Timestamp));
        }
        return result;
    }

    // Запись в срезе: её задача — одна из задач фичи, ИЛИ её чат — один из чатов фичи.
    // Пустой набор = «фильтра по этой оси нет», но если оба набора пусты — срез пуст
    // (у фичи без задач и чатов расхода нет).
    private static bool Match(string ownerId, string? taskId, string? sessionId, string owner,
        HashSet<string> tasks, HashSet<string> sessions) =>
        ownerId == owner
        && ((taskId is not null && tasks.Contains(taskId))
          || (sessionId is not null && sessions.Contains(sessionId)));

    // «Цена ненадёжна»: имя модели содержит одну из подстрок (регистронезависимо).
    private bool IsUnreliable(string? model) =>
        model is not null && _unreliableModels.Any(p => model.Contains(p, StringComparison.OrdinalIgnoreCase));

    private static FeatureSpendRecord ToRecord(SpendRecord r, DateTime timestamp) => new()
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
    };

    private static FeatureSpendRecord ToRecord(DailySpendRow d, DateTime timestamp) => new()
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
    };
}
