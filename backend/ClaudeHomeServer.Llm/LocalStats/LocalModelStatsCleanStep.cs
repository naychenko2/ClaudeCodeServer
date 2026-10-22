namespace ClaudeHomeServer.Services.Llm;

// База шага декода («чистый шаг») по кольцу точек: стабильная величина, НЕ зависящая от
// окна запроса (ADR-029, v2.5). Раньше базу выбирал фронт из интервалов САМОГО окна
// (p10 по 1–5 интервалам, признак чистоты prompt==0 — инвертирован): на окне 1 мин база
// была 58 мс, на окне 5 мин — 455 мс, и полоса «Куда уходит время агентов» прыгала при
// сдвиге окна. Теперь базу считает бэкенд по всему кольцу (1 час) и отдаёт её клиенту
// как ОДНО число.
//
// Алгоритм (чистые функции — без сети и без состояния):
//   • шаг пары соседних точек кольца = ΔitlSeconds/ΔitlCount (сек) → мс; пара валидна при
//     ΔitlCount >= 3 (меньше трёх шагов декодирования — дельта слишком грубая, шаг скачет);
//   • база = 10-й перцентиль шагов по всем точкам кольца (перцентиль, а не среднее:
//     среднее тянут вверх длинные шаги — это «ждёт соседа», а не база);
//   • валидных точек меньше 10 — перцентиль шумный: fallback на накопленное по кольцу
//     (ΣitlSeconds/ΣitlCnt), а если и его нет — на накопленные счётчики движка;
//   • данных нет — null.
//
// Гистерезис: опубликованная база обновляется, только если новое значение отличается от
// текущего на 10 % и больше — иначе между опросами полоса дёргалась бы на копейки.
internal static class LocalModelStatsCleanStep
{
    // Порог валидной точки: меньше трёх шагов декодирования — дельта слишком грубая.
    internal const int MinItlCount = 3;

    // Столько валидных точек нужно для перцентиля; меньше — берём накопленные счётчики.
    internal const int MinPointsForPercentile = 10;

    // Порог гистерезиса: новое значение отличается меньше чем на 10 % — базу не трогаем.
    internal const double HysteresisRatio = 0.10;

    // База шага (мс) по точкам кольца.
    // steps — приращения itl по точкам кольца (ΔitlSeconds, ΔitlCount): null — точка без
    //   данных (рестарт движка или метрика не отдана) → точка не входит в выборку;
    // lifetimeSeconds/lifetimeCount — накопленные счётчики движка (последний fallback,
    //   когда по кольцу нечего суммировать).
    // null — данных нет (нечего показывать).
    internal static double? ComputeBase(
        IReadOnlyList<(double? Seconds, long? Count)> steps,
        double? lifetimeSeconds = null,
        long? lifetimeCount = null)
    {
        var sample = new List<double>();
        double ringSeconds = 0;
        long ringCount = 0;

        foreach (var (seconds, count) in steps)
        {
            if (seconds is not { } s || count is not { } c || c <= 0) continue;
            ringSeconds += s;
            ringCount += c;
            if (c >= MinItlCount)
                sample.Add(s / c * 1000.0);
        }

        // Перцентиль — по достаточной выборке.
        if (sample.Count >= MinPointsForPercentile)
            return Percentile10(sample);

        // Fallback: накопленное по кольцу (ΣitlSeconds/ΣitlCount за час) — тот же охват,
        // что и перцентиль; если по кольцу нечего — накопленные счётчики движка за его жизнь.
        if (ringCount > 0)
            return ringSeconds / ringCount * 1000.0;
        if (lifetimeSeconds is { } ls && lifetimeCount is { } lc && lc > 0)
            return ls / lc * 1000.0;
        return null;
    }

    // База шага по числу одновременных потоков (Strata, 22.10): у батч-движка шаг растёт с
    // числом строк в окне (1 поток ≈ 8 мс, 3 ≈ 25 мс) — это норма параллельного декода, а не
    // «ждёт соседа». Одна база на стенд относила всё превышение над самым быстрым шагом к
    // ожиданию, и при трёх агентах полоса показывала 65 % «ждёт» на здоровой системе.
    // Для каждого уровня running (≥1) — свой 10-й перцентиль; уровень с выборкой меньше
    // MinPointsForPercentile не публикуется (клиент берёт общую базу).
    internal static IReadOnlyDictionary<int, double> ComputeBaseByRunning(
        IReadOnlyList<(double? Seconds, long? Count, int? Running)> steps)
    {
        var samples = new Dictionary<int, List<double>>();
        foreach (var (seconds, count, running) in steps)
        {
            if (seconds is not { } s || count is not { } c || c < MinItlCount) continue;
            if (running is not { } r || r < 1) continue;
            if (!samples.TryGetValue(r, out var list)) samples[r] = list = [];
            list.Add(s / c * 1000.0);
        }
        var result = new Dictionary<int, double>();
        foreach (var (r, list) in samples)
            if (list.Count >= MinPointsForPercentile)
                result[r] = Percentile10(list);
        return result;
    }

    // Гистерезис по уровням: ключ без кандидата — снимается (данных по уровню больше нет).
    internal static IReadOnlyDictionary<int, double> ApplyHysteresisByRunning(
        IReadOnlyDictionary<int, double>? current, IReadOnlyDictionary<int, double> candidate)
    {
        var result = new Dictionary<int, double>();
        foreach (var (r, next) in candidate)
            result[r] = ApplyHysteresis(current is not null && current.TryGetValue(r, out var prev) ? prev : null, next)!.Value;
        return result;
    }

    // Гистерезис: current — опубликованная база, candidate — новая. Возвращает то, что
    // публикуем. candidate null — данных нет: публикуем null (честно «нет данных»).
    internal static double? ApplyHysteresis(double? current, double? candidate)
    {
        if (candidate is not { } next) return null;
        if (current is not { } prev || prev <= 0) return next;
        return Math.Abs(next - prev) / prev < HysteresisRatio ? prev : next;
    }

    // 10-й перцентиль (как на фронте): отсортированные значения, индекс floor(0.1*n)
    // (ограничен последним). Вход не мутируется.
    internal static double Percentile10(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var idx = Math.Min(sorted.Count - 1, (int)Math.Floor(0.1 * sorted.Count));
        return sorted[idx];
    }
}
