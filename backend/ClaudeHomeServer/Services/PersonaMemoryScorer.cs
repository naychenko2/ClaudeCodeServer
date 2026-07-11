using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services;

// Параметры скоринга памяти персоны: веса взвешенной суммы, полураспад свежести
// и гейт минимальной релевантности. Читаются из конфига Persona:Score:* в ctor
// PersonaMemoryService; Default — дефолты той же шкалы.
public sealed record MemoryScoringOptions(
    double RelevanceWeight,
    double RecencyWeight,
    double SalienceWeight,
    double TypeWeight,
    double RecencyHalfLifeDays,
    double MinRelevance)
{
    public static readonly MemoryScoringOptions Default = new(
        RelevanceWeight: 0.55,
        RecencyWeight: 0.20,
        SalienceWeight: 0.15,
        TypeWeight: 0.10,
        RecencyHalfLifeDays: 30.0,
        MinRelevance: 0.05);
}

// Чистый скоринг записи памяти (взвешенная сумма вместо прежнего произведения):
// score = wRel·relevance + wRec·recency + wSal·salience + wType·typeFactor.
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

    public static double Score(PersonaMemoryEntry e, double relevance, DateTime nowUtc, MemoryScoringOptions o)
    {
        // Гейт: нерелевантная запись не должна всплывать одной свежестью/значимостью
        if (relevance < o.MinRelevance) return 0;

        // Свежесть отсчитываем от последнего обращения (reinforcement);
        // guard: битая LastAccessedAt раньше создания → берём CreatedAt
        var anchor = e.LastAccessedAt < e.CreatedAt ? e.CreatedAt : e.LastAccessedAt;
        var ageDays = Math.Max((nowUtc - anchor).TotalDays, 0);
        var recency = Math.Pow(2, -ageDays / o.RecencyHalfLifeDays);

        var salience = Math.Clamp(e.Salience, 0.0, 1.0);

        return o.RelevanceWeight * relevance
             + o.RecencyWeight * recency
             + o.SalienceWeight * salience
             + o.TypeWeight * TypeFactor(e.Type);
    }
}
