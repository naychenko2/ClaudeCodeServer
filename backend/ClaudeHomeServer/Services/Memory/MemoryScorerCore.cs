namespace ClaudeHomeServer.Services.Memory;

// Общее ядро скоринга долгой памяти (взвешенная сумма — эталон PersonaMemoryScorer). Параметризовано
// типом записи TEntry и её категорией TType. Специфику задаёт вызывающий скорер делегатами:
//  - typeFactor: вклад категории (у персоны/команды свои веса);
//  - recencyAnchor: от какой даты считать свежесть (персона — max(LastAccessedAt,CreatedAt) с учётом
//    reinforcement; команда — CreatedAt).
// Опциональный под-лимит по категории (maxEpisodic + isEpisodic) — у персоны эпизоды вытесняются в
// первую очередь; у командной памяти под-лимита нет.
//
// ВАЖНО: шкала суммы (~0..1) несовместима со старой шкалой произведения — пороги (RecallMinScore)
// калиброваны под ~0.30, а не 0.02.
public static class MemoryScorerCore<TEntry, TType>
    where TEntry : IMemoryEntry<TType>
{
    // score = wRel·relevance + wRec·recency + wSal·salience + wType·typeFactor.
    public static double Score(TEntry e, double relevance, DateTime nowUtc, MemoryScoringOptions o,
        Func<TType, double> typeFactor, Func<TEntry, DateTime> recencyAnchor)
    {
        // Гейт: нерелевантная запись не должна всплывать одной свежестью/значимостью
        if (relevance < o.MinRelevance) return 0;

        var anchor = recencyAnchor(e);
        var ageDays = Math.Max((nowUtc - anchor).TotalDays, 0);
        var recency = Math.Pow(2, -ageDays / o.RecencyHalfLifeDays);

        var salience = Math.Clamp(e.Salience, 0.0, 1.0);

        return o.RelevanceWeight * relevance
             + o.RecencyWeight * recency
             + o.SalienceWeight * salience
             + o.TypeWeight * typeFactor(e.Type);
    }

    // Retention-скоринг = скоринг без релевантности (она доступна только в контексте запроса).
    // Используется для вытеснения: чем ниже — тем скорее запись «забывается».
    public static double Retention(TEntry e, DateTime nowUtc, MemoryScoringOptions o,
        Func<TType, double> typeFactor, Func<TEntry, DateTime> recencyAnchor) =>
        Score(e, 0, nowUtc, o with { RelevanceWeight = 0, MinRelevance = 0 }, typeFactor, recencyAnchor);

    // Выбор записей на вытеснение при переполнении. Два ограничения:
    //  (1) опц. под-лимит по категории maxEpisodic (isEpisodic; ≤0/без предиката — выключен): записи
    //      этой категории растут линейно и не должны вымывать остальные, поэтому лишние вытесняются
    //      в первую очередь;
    //  (2) общий потолок maxEntries по оставшимся записям.
    // В обоих случаях режем хвост с наименьшим retention-скорингом (при равенстве — старейшие).
    public static List<string> SelectEvictionIds(IReadOnlyList<TEntry> entries,
        int maxEntries, MemoryScoringOptions options, DateTime nowUtc,
        Func<TType, double> typeFactor, Func<TEntry, DateTime> recencyAnchor,
        int maxEpisodic = 0, Func<TType, bool>? isEpisodic = null)
    {
        var evict = new HashSet<string>();

        if (maxEpisodic > 0 && isEpisodic is not null)
        {
            var episodic = entries.Where(e => isEpisodic(e.Type)).ToList();
            if (episodic.Count > maxEpisodic)
                foreach (var e in episodic
                    .OrderBy(e => Retention(e, nowUtc, options, typeFactor, recencyAnchor)).ThenBy(e => e.CreatedAt)
                    .Take(episodic.Count - maxEpisodic))
                    evict.Add(e.Id);
        }

        var remaining = entries.Where(e => !evict.Contains(e.Id)).ToList();
        if (maxEntries > 0 && remaining.Count > maxEntries)
            foreach (var e in remaining
                .OrderBy(e => Retention(e, nowUtc, options, typeFactor, recencyAnchor)).ThenBy(e => e.CreatedAt)
                .Take(remaining.Count - maxEntries))
                evict.Add(e.Id);

        return evict.ToList();
    }
}
