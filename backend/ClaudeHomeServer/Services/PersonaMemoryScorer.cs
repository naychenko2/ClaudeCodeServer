using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Memory;

namespace ClaudeHomeServer.Services;

// Скоринг записи памяти персоны — тонкая обёртка над общим ядром MemoryScorerCore.
// Специфика персоны: веса типов (semantic > procedural > episodic), якорь свежести от последнего
// обращения (reinforcement) и под-лимит эпизодов при вытеснении.
// ВАЖНО: шкала суммы (~0..1) несовместима со старой шкалой произведения —
// пороги (Persona:RecallMinScore) калиброваны под ~0.30, а не 0.02.
public static class PersonaMemoryScorer
{
    // Вклад типа памяти: факты важнее приёмов, приёмы важнее эпизодов
    public static double TypeFactor(PersonaMemoryType t) => t switch
    {
        PersonaMemoryType.Semantic => 1.0,
        PersonaMemoryType.Procedural => 0.8,
        PersonaMemoryType.Episodic => 0.6,
        _ => 0.6,
    };

    // Свежесть отсчитываем от последнего обращения (reinforcement);
    // guard: битая LastAccessedAt раньше создания → берём CreatedAt
    private static DateTime RecencyAnchor(PersonaMemoryEntry e) =>
        e.LastAccessedAt < e.CreatedAt ? e.CreatedAt : e.LastAccessedAt;

    public static double Score(PersonaMemoryEntry e, double relevance, DateTime nowUtc, MemoryScoringOptions o) =>
        MemoryScorerCore<PersonaMemoryEntry, PersonaMemoryType>.Score(e, relevance, nowUtc, o, TypeFactor, RecencyAnchor);

    public static double Retention(PersonaMemoryEntry e, DateTime nowUtc, MemoryScoringOptions o) =>
        MemoryScorerCore<PersonaMemoryEntry, PersonaMemoryType>.Retention(e, nowUtc, o, TypeFactor, RecencyAnchor);

    // Вытеснение: под-лимит эпизодов (лишние эпизоды первыми) + общий потолок. Рабочий фокус —
    // не запись памяти и здесь не участвует.
    public static List<string> SelectEvictionIds(IReadOnlyList<PersonaMemoryEntry> entries,
        int maxEntries, int maxEpisodic, MemoryScoringOptions options, DateTime nowUtc) =>
        MemoryScorerCore<PersonaMemoryEntry, PersonaMemoryType>.SelectEvictionIds(
            entries, maxEntries, options, nowUtc, TypeFactor, RecencyAnchor,
            maxEpisodic, t => t == PersonaMemoryType.Episodic);
}
