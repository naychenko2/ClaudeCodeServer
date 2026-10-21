namespace ClaudeHomeServer.Services.Spend;

// Шов чтения расхода по фиче (обзор фич, волна 3, шаг Б9a). Потребитель (страница
// фичи, шаг Б9b) вертикаль Spend не видит — сборка Spend это динамический модуль
// (ADR-014, сценарий Б), все её швы живут в Core. Потребитель зависит от этого
// интерфейса и берёт его ОПЦИОНАЛЬНЫМ: при выключенной подсистеме Spend (гейт
// Subsystems:spend:Enabled / DynamicModules.spend.Enabled) шов не зарегистрирован,
// и расход фичи показывается «≈» (решение Андрея 20.10: фичи до журнала — приблизительные).
//
// Источник данных — существующий SpendStore (решение Андрея 20.10: отдельное
// хранилище не нужно): детальные записи в окне Spend:DetailDays + дневные агрегаты
// для более старых дней. Инвариант стора: день живёт либо в деталях, либо в
// агрегатах — двойного счёта нет.
//
// Срез фичи: запись попадает, если ЕЁ задача входит в `taskIds` ИЛИ ЕЁ чат входит
// в `sessionIds` (ИЛИ, не И: ход оркестраторского чата задачи не имеет — его
// ловит чат; задача, исполнявшаяся в чате вне списка, ловится задачей). Оба набора
// пусты — срез пуст. Фильтр владельца (`OwnerId == ownerId`) — защита в глубину.
//
// `excludeSessionIds` (обязательный, без значения по умолчанию: каждый вызов обязан решить
// явно) — ось исключения, ГЛАВНЕЕ обеих осей (решение Александра, доработка Б9c): запись
// с `SessionId ∈ excludeSessionIds` не входит в срез, даже если её задача в `taskIds`
// (в проде это реальность: запись общего чата несёт TaskId задачи фичи, а чат — общий).
// Правило Match: `owner == ownerId && SessionId ∉ exclude && (TaskId ∈ taskIds || SessionId ∈
// sessionIds)`. Распространяется и на дневные строки (`DailySpendRow.SessionId` есть).
public interface ISpendFeatureReader
{
    // Итог расхода фичи: токены (вход+выход крупно, кэш отдельно — решение Андрея 20.10)
    // и стоимость в USD. `from` — граница «с какого момента» (null — весь журнал).
    // Мёртвая поверхность шва (ревью Глеба, minor): прод-код (TasksController) ходит через
    // Slice (один проход), Totals/Records держим для Б9c (Records — деление расхода общего
    // оркестраторского чата) и для тестов слоёв.
    FeatureSpendTotals Totals(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string> excludeSessionIds,
        DateTime? from);

    // Записи по ходам того же среза (время, чат, задача, персона, модель, токены,
    // стоимость, источник) — для шага Б9c: расход ОБЩЕГО оркестраторского чата
    // делится поровну между активными фичами, и деление делается по этим записям.
    IReadOnlyList<FeatureSpendRecord> Records(string ownerId, IReadOnlyCollection<string> taskIds,
        IReadOnlyCollection<string> sessionIds, IReadOnlyCollection<string> excludeSessionIds,
        DateTime? from);

    // Срез одним проходом: итог + записи из ОДНОГО набора записей (доработка Б9b, минор):
    // два отдельных вызова Totals/Records — два скана стора, и запись, попавшая между ними,
    // попадала в разрез, но не в итог (окно рассогласования). Возвращает (итог, записи).
    (FeatureSpendTotals Totals, IReadOnlyList<FeatureSpendRecord> Records) Slice(
        string ownerId, IReadOnlyCollection<string> taskIds, IReadOnlyCollection<string> sessionIds,
        IReadOnlyCollection<string> excludeSessionIds, DateTime? from);
}

// Итог расхода фичи. `From` — самая ранняя дата, с которой в срезе есть данные
// (null — записей нет): фича старше этой даты показывается «≈».
// Свойства set-аксессоры: реализация накапливает итог по записям (аккумулятор),
// не собирает объект одним initializer'ом.
public sealed class FeatureSpendTotals
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    // Сумма стоимости записей с НАДЁЖНОЙ ценой: локальные модели (Source == free)
    // дают 0, модели с ненадёжной ценой (HiddenCostModels) в сумму не входят.
    // double (IEEE 754), НЕ decimal: минор Б9b — бэкенд не округляет молча на границе,
    // округление до отображаемой точности делает фронт (featureStats.ts); разрез
    // (FeatureBreakdown) при конверсии в decimal не округляет, а переносит как есть.
    public double CostUsd { get; set; }
    // По меньшей мере одна запись имела ненадёжную цену: её стоимость не в
    // CostUsd, а модель перечислена в HiddenCostModels.
    public bool CostPartial { get; set; }
    // Модели, чья стоимость не вошла в итог (цена от CLI ненадёжна).
    public List<string> HiddenCostModels { get; set; } = [];
    // Самая ранняя дата, с которой в срезе есть данные; null — записей нет.
    public DateOnly? From { get; set; }
}

// Одна запись расхода фичи: ход (детальная запись) или строка дневного агрегата.
// Для дневной строки `Timestamp` — начало дня (агрегат времени не несёт),
// `CostUsd` — сумма стоимости записей дня (double, не nullable: агрегат всегда
// несёт число, пусть и 0).
public sealed class FeatureSpendRecord
{
    public DateTime Timestamp { get; init; }
    public string? SessionId { get; init; }
    public string? TaskId { get; init; }
    public string? PersonaId { get; init; }
    public string? Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public double? CostUsd { get; init; }
    public string Source { get; init; } = "";
    // Цена от CLI ненадёжна (модель в списке `Spend:UnreliableCostModels`): токены в сумму,
    // стоимость — нет. Единая точка правды (SpendCostReliability): читатель (SpendFeatureReader)
    // выставляет флаг при сборке записи, и итог расхода с разрезом фичи (FeatureBreakdown)
    // используют ОДИН и тот же признак — не «CostUsd is null» в разрезе против имени модели
    // в итоге (ревью Глеба, major: две точки правды давали разные цифры по одной записи).
    public bool UnreliableCost { get; init; }
    // Дневная строка (агрегат суток, Timestamp = 00:00): делится между фичами, активными
    // в любой момент этих суток (пересечение [d, d+1) с интервалами), метод принудительно approx.
    public bool IsDaily { get; init; }
}
