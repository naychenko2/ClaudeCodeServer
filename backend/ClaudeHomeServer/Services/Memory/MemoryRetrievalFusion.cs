namespace ClaudeHomeServer.Services.Memory;

// Метод слияния сигналов retrieval.
public enum MemoryFusionMethod
{
    // Взвешенная сумма нормализованных сигналов (основной путь)
    WeightedSum,
    // Reciprocal Rank Fusion — слияние по рангам, устойчиво к разным шкалам сигналов
    Rrf,
}

// Параметры гибридного retrieval: веса сигналов, метод слияния и константа RRF.
// Читаются из конфига (Memory:Fusion:*) в ctor сервисов памяти; Default — дефолты
// (semantic доминирует, keyword добирает точные термины/идентификаторы).
public sealed record MemoryFusionOptions(
    double SemanticWeight = 0.7,
    double KeywordWeight = 0.3,
    MemoryFusionMethod Method = MemoryFusionMethod.WeightedSum,
    double RrfK = 60.0)
{
    public static readonly MemoryFusionOptions Default = new();
}

// Гибридный retrieval (fusion): слияние семантического (Dify) и ключевого (полнотекст) сигналов
// в единый relevance per entryId в шкале 0..1. Повышает recall на точных терминах/идентификаторах,
// которые векторный поиск пропускает. Чистая функция без I/O — легко тестируется.
//
// ТОЧКА РАСШИРЕНИЯ (#5 entity-centric): третий сигнал entity-pass добавляется как ещё один
// взвешенный набор в массив signals внутри Fuse — обе стратегии (weighted-sum/RRF) уже работают
// на произвольном числе сигналов, менять их не нужно.
public static class MemoryRetrievalFusion
{
    // Взвешенный сигнал: набор score-ов (entryId → 0..1) и его вес в слиянии.
    private readonly record struct WeightedSignal(IReadOnlyDictionary<string, double> Scores, double Weight);

    // Слить semantic + keyword в единый relevance per entryId. Записи, попавшие только в один сигнал,
    // учитываются с нулём по второму. По умолчанию — взвешенная сумма нормализованных сигналов.
    public static Dictionary<string, double> Fuse(
        IReadOnlyDictionary<string, double> semantic,
        IReadOnlyDictionary<string, double> keyword,
        MemoryFusionOptions options)
    {
        var signals = new[]
        {
            new WeightedSignal(semantic, options.SemanticWeight),
            new WeightedSignal(keyword, options.KeywordWeight),
            // ТОЧКА РАСШИРЕНИЯ (#5): new WeightedSignal(entity, options.EntityWeight),
        };
        return options.Method == MemoryFusionMethod.Rrf
            ? FuseRrf(signals, options.RrfK)
            : FuseWeightedSum(signals);
    }

    // Взвешенная сумма нормализованных сигналов: relevance = Σ wᵢ·normᵢ.
    // Каждый сигнал нормализуется в 0..1 по своему максимуму (пустой / целиком нулевой сигнал даёт
    // вклад 0). Итог клампится в 0..1 — общая шкала relevance сохраняется (пороги MinRelevance/
    // RecallMinScore калибруются под неё и здесь не меняются).
    private static Dictionary<string, double> FuseWeightedSum(IReadOnlyList<WeightedSignal> signals)
    {
        var maxes = signals
            .Select(s => s.Scores.Count > 0 ? s.Scores.Values.Max() : 0.0)
            .ToArray();

        var result = new Dictionary<string, double>();
        foreach (var id in signals.SelectMany(s => s.Scores.Keys).Distinct())
        {
            double rel = 0;
            for (var i = 0; i < signals.Count; i++)
            {
                var max = maxes[i];
                if (max <= 0) continue;   // сигнал пуст/нулевой — не участвует
                rel += signals[i].Weight * (signals[i].Scores.GetValueOrDefault(id, 0.0) / max);
            }
            result[id] = Math.Clamp(rel, 0.0, 1.0);
        }
        return result;
    }

    // Reciprocal Rank Fusion (альтернатива): relevance ∝ Σ wᵢ/(k + rankᵢ), ранг 1 — самый релевантный
    // в сигнале; запись вне сигнала вклада не получает. Итог нормализуется в 0..1 по максимуму —
    // общая шкала relevance сохраняется, как и в weighted-sum.
    private static Dictionary<string, double> FuseRrf(IReadOnlyList<WeightedSignal> signals, double k)
    {
        var raw = new Dictionary<string, double>();
        foreach (var signal in signals)
        {
            var rank = 0;
            foreach (var kv in signal.Scores.OrderByDescending(x => x.Value))
            {
                rank++;
                raw[kv.Key] = raw.GetValueOrDefault(kv.Key, 0.0) + signal.Weight / (k + rank);
            }
        }

        var max = raw.Count > 0 ? raw.Values.Max() : 0.0;
        if (max <= 0) return raw;
        return raw.ToDictionary(kv => kv.Key, kv => kv.Value / max);
    }
}
