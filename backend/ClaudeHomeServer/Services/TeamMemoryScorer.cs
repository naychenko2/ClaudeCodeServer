using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services;

// Скоринг записи командной памяти (③-3.4) — тонкая обёртка над общим ядром MemoryScorerCore.
// Специфика команды: веса типов (decision > convention > fact > glossary) и якорь свежести от
// создания (у TeamMemoryEntry нет LastAccessedAt — reinforcement двигает только salience). Под-лимита
// по типу нет (в отличие от эпизодов персоны) — потолок один на весь стор проекта.
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

    public static double Score(TeamMemoryEntry e, double relevance, DateTime nowUtc, MemoryScoringOptions o) =>
        MemoryScorerCore<TeamMemoryEntry, TeamMemoryType>.Score(e, relevance, nowUtc, o, TypeFactor, e => e.CreatedAt);

    public static double Retention(TeamMemoryEntry e, DateTime nowUtc, MemoryScoringOptions o) =>
        MemoryScorerCore<TeamMemoryEntry, TeamMemoryType>.Retention(e, nowUtc, o, TypeFactor, e => e.CreatedAt);

    public static List<string> SelectEvictionIds(IReadOnlyList<TeamMemoryEntry> entries,
        int maxEntries, MemoryScoringOptions options, DateTime nowUtc) =>
        MemoryScorerCore<TeamMemoryEntry, TeamMemoryType>.SelectEvictionIds(
            entries, maxEntries, options, nowUtc, TypeFactor, e => e.CreatedAt);
}
