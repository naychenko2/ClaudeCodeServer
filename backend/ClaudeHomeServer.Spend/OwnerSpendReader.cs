using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// Реализация швы чтения расхода владельца (IOwnerSpendReader, Core) поверх SpendStore.
//
// «Сегодня» и «месяц» — локальные даты таймзоны вызывающего (параметр timeZone), не UTC:
// каждая детальная запись пересчитывается в таймзону по своему instant (Timestamp UTC →
// TimeZoneInfo.ConvertTimeFromUtc) и относится к локальной дате этого пересчёта. Запись
// 23:30 Europe/Moscow (20:30 UTC) принадлежит московской дате, даже если UTC-дата другая.
//
// Диапазон чтения по UTC-дням расширяется на ±1 день с каждой стороны: смещение любой
// реальной таймзоны ≤ 14 часов, поэтому запись локального окна не может лежать дальше
// соседнего UTC-дня, а лишние дни отсекает фильтр локальной даты.
//
// Нет двойного счёта: дневные агрегаты читаются первыми, их даты собираются в множество,
// и детали этих дат пропускаются — тот же порядок, что у SpendFeatureReader. Он же
// закрывает окно rollup, когда день временно лежит и в деталях, и в агрегатах
// (SpendStore.RollupOlderThan: `_daily = next` до удаления деталей).
//
// Конкурентность: детали читаются через SpendStore.DetailsBetween — под тем же lock(list)
// дня, под которым Record() пишет (SpendStore.Record), и без «рваной» суммы; дневные
// агрегаты — volatile-снапшот стора, atomic по построению.
//
// Дневные агрегаты времени не несут: их UTC-день относится локальному окну по своей
// UTC-дате (граница месяца по свёрнутым дням — нижняя оценка; «сегодня» этим не затронуто:
// текущий день никогда не свёрнут — RollupOlderThan берёт дни строго старше окна деталей).
//
// Деньги: складывается только CostUsd (TodayUsd/MonthUsd — доллары). CostRub и CostCredits —
// отдельные валюты, нигде не складываются (SpendRecord). Source не фильтруется: ходы,
// one-shot и TTS пишут в один стор (TtsController) — все траты владельца его.
// Сумма накапливается в decimal: (double)0.01 → decimal 0.01 точен, сумма ста таких —
// ровно 1.00 (тест гонки); двойная аккумуляция дала бы 1.0000000000000002.
public sealed class OwnerSpendReader : IOwnerSpendReader
{
    private readonly SpendStore _store;
    // «Сейчас» с заменяемым часом: в проде — UTC-сейчас; в тестах — фиксированный instant
    // (сегодня/месяц проверяются детерминированно, тот же приём, что QuietHttpLogger/ClaudeCliUpdateWatcher).
    private readonly Func<DateTimeOffset> _utcNow;

    // DI-конструктор. internal-конструктор со второй сигнатурой DI не видит (чужая сборка) —
    // один public, ambiguous нет (тот же порядок, что у SpendFeatureReader).
    public OwnerSpendReader(SpendStore store) : this(store, null) { }

    // Тестовый конструктор: фиксированный час. internal — виден тестовой сборке через InternalsVisibleTo.
    internal OwnerSpendReader(SpendStore store, Func<DateTimeOffset>? utcNow)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public decimal TodayUsd(string ownerId, TimeZoneInfo timeZone)
    {
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_utcNow(), timeZone).DateTime);
        return SumInLocalDays(ownerId, timeZone, localToday, localToday);
    }

    public decimal MonthUsd(string ownerId, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(_utcNow(), timeZone).DateTime;
        var from = new DateOnly(localNow.Year, localNow.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        return SumInLocalDays(ownerId, timeZone, from, to);
    }

    // Сумма CostUsd записей владельца, чья локальная дата в [localFrom, localTo] (включительно).
    private decimal SumInLocalDays(string ownerId, TimeZoneInfo tz, DateOnly localFrom, DateOnly localTo)
    {
        // Читаем по UTC-датам с запасом ±1 день: смещение любой реальной таймзоны ≤ 14 ч.
        var utcFrom = localFrom.AddDays(-1);
        var utcTo = localTo.AddDays(1);

        var sum = 0m;
        var aggregatedDates = new HashSet<DateOnly>();

        // Дневные агрегаты — первыми (их даты отсекают детали тех же дней, нет двойного
        // счёта; закрывает и окно rollup, когда день временно в обоих). Относятся окну по
        // своей UTC-дате: агрегат времени не несёт, точная локальная атрибуция невозможна.
        foreach (var d in _store.DailyBetween(utcFrom, utcTo))
        {
            if (d.OwnerId != ownerId) continue;
            if (!DateOnly.TryParse(d.Date, out var utcDate)) continue;
            aggregatedDates.Add(utcDate);
            if (utcDate < localFrom || utcDate > localTo) continue;
            sum += ToDecimal(d.CostUsd);
        }

        // Детали — под lock дня стора (DetailsBetween): снимок целой, concurrent Record
        // либо целиком виден, либо целиком нет — «рваной» суммы нет.
        foreach (var r in _store.DetailsBetween(utcFrom, utcTo))
        {
            if (r.OwnerId != ownerId) continue;
            if (aggregatedDates.Contains(r.Date)) continue; // день уже в агрегатах — детали не считаем
            // Kind записи с диска — Unspecified; семантика стора «все даты UTC»
            // (SpendStore), поэтому instant трактуем как UTC явно.
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(r.Timestamp, DateTimeKind.Utc), tz));
            if (localDate < localFrom || localDate > localTo) continue;
            sum += ToDecimal(r.CostUsd);
        }

        return sum;
    }

    // Цена записи в сумму: null (без цены) и NaN/∞ (сбой CLI) — 0, не провал швы:
    // «бюджет не проверить» — решение потребителя (KM-B5c), а не исключение из-за
    // одной битой строки журнала. Ошибка чтения стора (IO) наружу не превращается.
    private static decimal ToDecimal(double? cost) =>
        cost is { } c && double.IsFinite(c) ? (decimal)c : 0m;
}
