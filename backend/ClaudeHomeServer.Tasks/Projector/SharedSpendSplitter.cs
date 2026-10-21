using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Spend;
using ClaudeHomeServer.Services.Tasks.Journal;

namespace ClaudeHomeServer.Services.Tasks.Projector;

// ─── Б9c: деление расхода ОБЩЕГО (оркестраторского) чата поровну между активными фичами ──────
//
// «Общий чат» — чат, входящий в ДВЕ И БОЛЕЕ фичи владельца (решение Андрея 20.10: его расход
// не приписывается каждой фиче целиком — двойной счёт, critical ревью Глеба в Б9b; вместо
// этого делится поровну между фичами, активными на момент хода). Доля помечена как оценка:
// `method` = journal (по полному журналу) | approx (по интервалам задач).
//
// Правило деления (фиксация, шаг 4): метод наибольшего остатка. Для каждого хода и каждого
// поля токенов: активные фичи упорядочиваются по FeatureId (Ordinal), base = x / N,
// r = x % N; фича с индексом i получает base + (i < r ? 1 : 0). Сумма долей = x РОВНО
// (точное равенство, не «≈»). Деньги делятся в decimal без округления; проверка сохранения
// — с допуском 1e-9. DAILY-строки (IsDaily) делятся между фичами, активными в любой момент
// суток [d, d+1); метод такого чата принудительно approx.

// (FeatureActivityInterval и FeatureActivity — в FeatureActivity.cs, единый модуль активности.)

// Чистая функция (без IO): вход — записи расхода по ходам ОБЩЕГО чата (SpendFeatureReader.Records)
// + интервалы активности ВСЕХ фич, в которые чат входит (знаменатель деления) + id текущей фичи
// + метод оценки. Выход — доля текущей фичи (FeatureSpendShared) + признак ненадёжной цены её
// доли + модели без цены в доле (для costPartial/hiddenCostModels итога, те же правила, что Б9b).
public static class SharedSpendSplitter
{
    // Доля фичи в расходе общего чата.
    //   sharedTurns    — записи расхода по ходам ОБЩИХ чатов (время, токены, цена, источник,
    //     признак ненадёжной цены — уже выставлен читателем);
    //   activities     — интервалы активности ВСЕХ участниц (объединение составов общих чатов);
    //   chatParticipants — карта chatId → M[c] (состав каждого общего чата: множество фич, в
    //     которые чат входит; строит FeatureChatMembership.Build). Знаменатель записи = M[rec.SessionId],
    //     НЕ объединение всех участниц (дефект: c1={F,G}, c2={F,H} → ход в c1 делится на 2, не 3);
    //   featureId      — фича, долю которой считаем;
    //   method         — "journal" | "approx" (метод оценки; задаёт вызывающий: journal, только
    //     если ВСЕ общие чаты фичи journal — см. комментарий у FeatureSpendShared.Method).
    // Возвращает (доля, признак ненадёжной цены доли, модели без цены в доле).
    public static (FeatureSpendShared Share, bool CostPartial, List<string> HiddenModels) Split(
        IReadOnlyList<FeatureSpendRecord> sharedTurns,
        IReadOnlyList<FeatureActivity> activities,
        IReadOnlyDictionary<string, HashSet<string>> chatParticipants,
        string featureId,
        string method)
    {
        // Индекс featureId → интервалы.
        var byId = new Dictionary<string, IReadOnlyList<FeatureActivityInterval>>(StringComparer.Ordinal);
        foreach (var a in activities)
            byId[a.FeatureId] = a.Intervals;

        long input = 0, output = 0, cacheRead = 0, cacheCreation = 0;
        decimal cost = 0;
        int turns = 0;
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        bool costPartial = false;

        foreach (var rec in sharedTurns)
        {
            // Дневная строка: активна, если фича активна в ЛЮБОЙ момент суток [d, d+1).
            // Активные = пересечение (активных среди ВСЕХ activities) и M[rec.SessionId]:
            // знаменатель — состав ЧАТА записи, не объединение всех участниц (дефект Б9c-3).
            var (activeIds, isDaily) = ActiveFeatures(activities, byId, rec, chatParticipants);
            // Дневная строка понижает метку ДО проверки активных (п.5: независимо от того,
            // был ли кто-то активен; SharedSpendSplitter.cs:58-59 раньше нарушало это).
            if (isDaily) method = "approx";
            if (activeIds.Count == 0) continue;

            // Текущая фича в списке активных? Нет — её доля 0.
            var idx = activeIds.IndexOf(featureId);
            if (idx < 0) continue;

            var n = activeIds.Count;

            // Наибольший остаток: base + (idx < r ? 1 : 0). Кламп ≥ 0.
            input += LargestRemainder(Math.Max(0, rec.InputTokens), n, idx);
            output += LargestRemainder(Math.Max(0, rec.OutputTokens), n, idx);
            cacheRead += LargestRemainder(Math.Max(0, rec.CacheReadTokens), n, idx);
            cacheCreation += LargestRemainder(Math.Max(0, rec.CacheCreationTokens), n, idx);

            // Цена: ReliableCostOf (единая точка правды, доработка Б9c шаг 1).
            var reliableCost = SpendCostReliability.ReliableCostOf(rec);
            if (reliableCost is not null)
                cost += (decimal)(reliableCost.Value / n);
            else
            {
                costPartial = true;
                if (rec.Model is not null) hidden.Add(rec.Model);
            }

            turns++;
        }

        return (
            new FeatureSpendShared(input, output, cacheRead, cacheCreation, (double)cost, turns, method),
            costPartial,
            hidden.OrderBy(m => m, StringComparer.Ordinal).ToList());
    }

    // Активные фичи на момент записи: для detail — по Timestamp (точка), для daily — по
    // пересечению [d, d+1) с интервалами. Затем — пересечение с составом чата записи
    // M[rec.SessionId]: знаменатель = состав ЧАТА, не объединение всех участниц.
    // Возвращает (отсортированный список id, isDaily).
    private static (List<string> Ids, bool IsDaily) ActiveFeatures(
        IReadOnlyList<FeatureActivity> activities,
        Dictionary<string, IReadOnlyList<FeatureActivityInterval>> byId,
        FeatureSpendRecord rec,
        IReadOnlyDictionary<string, HashSet<string>> chatParticipants)
    {
        var isDaily = rec.IsDaily;
        var dayStart = rec.Timestamp;
        var dayEnd = dayStart.AddDays(1);
        var ids = new List<string>();
        foreach (var a in activities)
        {
            if (isDaily)
            {
                // Пересечение [d, d+1) с любым интервалом фичи.
                if (IntervalsIntersectDay(a.Intervals, dayStart, dayEnd))
                    ids.Add(a.FeatureId);
            }
            else
            {
                if (IsFeatureActive(a.Intervals, rec.Timestamp))
                    ids.Add(a.FeatureId);
            }
        }
        // Пересечение с составом чата записи (M[rec.SessionId]): фича, не входящая в этот чат,
        // в знаменатель не попадает (дефект: c1={F,G}, c2={F,H} → ход в c1 делится на 2, не 3).
        // rec.SessionId null / не в карте → без фильтра (запись не из общих чатов — не ожидается).
        if (rec.SessionId is not null && chatParticipants.TryGetValue(rec.SessionId, out var members))
            ids.RemoveAll(id => !members.Contains(id));
        ids.Sort(StringComparer.Ordinal);
        return (ids, isDaily);
    }

    // Пересечение любого интервала фичи с [dayStart, dayEnd): true, если есть перекрытие.
    private static bool IntervalsIntersectDay(
        IReadOnlyList<FeatureActivityInterval> intervals, DateTime dayStart, DateTime dayEnd)
    {
        foreach (var iv in intervals)
            if (iv.From < dayEnd && iv.To > dayStart) return true;
        return false;
    }

    // Фича активна на момент t: t внутри любого её интервала [From, To).
    private static bool IsFeatureActive(IReadOnlyList<FeatureActivityInterval> intervals, DateTime t)
    {
        foreach (var iv in intervals)
            if (iv.From <= t && t < iv.To) return true;
        return false;
    }

    // Метод наибольшего остатка: x делится на n частей; часть с индексом idx получает
    // base + (idx < r ? 1 : 0), где base = x/n, r = x%n. Сумма по всем idx = x РОВНО.
    private static long LargestRemainder(long value, int n, int idx)
    {
        if (n <= 1) return value;
        var q = value / n;
        var r = value % n;
        return q + (idx < r ? 1 : 0);
    }

    // ─── Интервалы активности фичи (делегирование в FeatureActivity) ───────────────────────────

    // Approx-режим: делегирует в FeatureActivityBuilder.Approx (единый модуль активности, шаг 3).
    // run null → нет интервалов.
    public static IReadOnlyList<FeatureActivityInterval> ApproxActivity(FeatureRun? run, DateTime now)
        => FeatureActivityBuilder.Approx(run, now);

    // Journal-режим: делегирует в FeatureActivityBuilder.Journal (единый модуль активности, шаг 3).
    // clipFrom = epic.CreatedAt (вызывающий): журнальные интервалы обрезаются по созданию фичи,
    // как и approx-интервалы — знаменатели режимов однородны (минор 3: раньше journal шёл
    // без clipFrom, и активность могла начинаться до создания фичи).
    public static IReadOnlyList<FeatureActivityInterval> JournalActivity(
        FeatureJournalReadResult journal, DateTime now, DateTime? clipFrom = null)
        => FeatureActivityBuilder.Journal(journal, now, clipFrom);
}
