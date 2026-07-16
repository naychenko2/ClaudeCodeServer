namespace ClaudeHomeServer.Services.Memory;

// Параметры скоринга долгой памяти: веса взвешенной суммы, полураспад свежести и гейт
// минимальной релевантности. Общий тип для памяти персон и памяти команд — читается из
// конфига (Persona:Score:* / TeamMemory:Score:*) в ctor соответствующего сервиса; Default —
// дефолты той же шкалы. Вынесен в общее ядро Services/Memory (был в PersonaMemoryScorer.cs).
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
