using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Чистый скоринг записи командной памяти (③-3.4) — эталон PersonaMemoryScorer.
// Взвешенная сумма: score = wRel·relevance + wRec·recency + wSal·salience + wType·typeFactor.
// Параметры (MemoryScoringOptions) общие с персональной памятью; читаются из TeamMemory:Score:*
// в ctor TeamMemoryService, дефолты — MemoryScoringOptions.Default (та же шкала).
// Отличие от персоны: у TeamMemoryEntry нет LastAccessedAt — свежесть считаем от CreatedAt.
public static class TeamMemoryScorer
{
    // Вклад типа знания: решение важнее договорённости, та важнее факта, факт важнее термина
    public static double TypeFactor(TeamMemoryType t) => t switch
    {
        TeamMemoryType.Decision => 1.0,
        TeamMemoryType.Convention => 0.9,
        TeamMemoryType.Fact => 0.8,
        TeamMemoryType.Glossary => 0.7,
        _ => 0.8,
    };

    public static double Score(TeamMemoryEntry e, double relevance, DateTime nowUtc, MemoryScoringOptions o)
    {
        // Гейт: нерелевантная запись не должна всплывать одной свежестью/значимостью
        if (relevance < o.MinRelevance) return 0;

        // Свежесть — от создания записи (реинфорсмент командной памяти двигает только salience)
        var ageDays = Math.Max((nowUtc - e.CreatedAt).TotalDays, 0);
        var recency = Math.Pow(2, -ageDays / o.RecencyHalfLifeDays);

        var salience = Math.Clamp(e.Salience, 0.0, 1.0);

        return o.RelevanceWeight * relevance
             + o.RecencyWeight * recency
             + o.SalienceWeight * salience
             + o.TypeWeight * TypeFactor(e.Type);
    }

    // Retention-скоринг = скоринг без релевантности (она доступна только в контексте запроса).
    // Используется для вытеснения: чем ниже — тем скорее запись «забывается».
    public static double Retention(TeamMemoryEntry e, DateTime nowUtc, MemoryScoringOptions o) =>
        Score(e, 0, nowUtc, o with { RelevanceWeight = 0, MinRelevance = 0 });

    // Выбор записей на вытеснение при переполнении сверх общего потолка maxEntries (≤0 — выключен):
    // режем хвост с наименьшим retention-скорингом (при равенстве — старейшие). У командной памяти
    // нет под-лимита по типу (в отличие от эпизодов персоны) — потолок один на весь стор проекта.
    public static List<string> SelectEvictionIds(IReadOnlyList<TeamMemoryEntry> entries,
        int maxEntries, MemoryScoringOptions options, DateTime nowUtc)
    {
        if (maxEntries <= 0 || entries.Count <= maxEntries) return [];
        return entries
            .OrderBy(e => Retention(e, nowUtc, options)).ThenBy(e => e.CreatedAt)
            .Take(entries.Count - maxEntries)
            .Select(e => e.Id)
            .ToList();
    }
}
