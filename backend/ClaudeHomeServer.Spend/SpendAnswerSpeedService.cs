using ClaudeHomeServer.Models;
using ClaudeHomeServer.Services.Llm;

namespace ClaudeHomeServer.Services.Spend;

// Скорость ответа облачных ходов — сессионная, сопоставимая с Artificial Analysis:
// токены сессии ÷ время ответов модели (без времени работы инструментов).
//
// Для облака это единственное сопоставимое определение: per-request учёта у облака нет
// (ходы CCS идут мимо шлюза LLM), а сессионный знаменатель в CCS есть —
// ResultMessage.DurationApiMs (duration_api_ms от CLI): суммарное время запросов к API за ход.
//
// Считаем ТОЛЬКО по детальным записям (SpendStore.DetailsBetween): в дневных агрегатах
// (DailySpendRow) длительностей нет, поэтому за окном детального окна агрегат пуст —
// это честнее, чем считать по среднему времени хода (DurationMs включает инструменты).
public sealed record SpendAnswerSpeedRowDto(
    string Provider, string Model, double? EndToEndTps, double? MedianTps,
    int Requests, long OutputTokens, long ApiMs);

public sealed record SpendAnswerSpeedDto(
    string From, string To, int MinTokensForMedian,
    IReadOnlyList<SpendAnswerSpeedRowDto> Models);

// Агрегатор скорости ответа по провайдерам и моделям.
public sealed class SpendAnswerSpeedService(SpendStore store, IModelResolver llmProviders)
{
    // Порог медианы: короткие ответы (десятки токенов) занижают медиану из-за времени
    // первого токена, поэтому в медиану идут только ходы с не меньшим числом токенов.
    // Тот же порог, что у локальной скорости ответа (LocalModelAnswerSpeed).
    public const int MinTokensForMedian = 32;

    public SpendAnswerSpeedDto AnswerSpeed(DateOnly from, DateOnly to)
    {
        var rows = new List<SpendAnswerSpeedRowDto>();
        var groups = store.DetailsBetween(from, to)
            // Ход без времени API или без выданных токенов в скорость не идёт:
            // знаменатель и числитель выдумывать нельзя.
            .Where(r => r.DurationApiMs is > 0 && r.OutputTokens > 0)
            // Источники без токенов (генерации картинок, озвучка) — не ходы модели.
            .Where(r => !SpendSources.IsTokenless(r.Source))
            // Локальный провайдер (свой движок) — не облако: его скорость считает
            // отдельный сборщик (LocalModelAnswerSpeed), здесь он бы смешался с облаком.
            .Where(r => !llmProviders.IsLocal(r.Provider))
            .GroupBy(r => (r.Provider, Model: llmProviders.ResolveModelOrDefault(r.Model, r.Provider)));

        foreach (var g in groups)
        {
            var items = g.ToList();
            long outputTokens = items.Sum(x => x.OutputTokens);
            long apiMs = items.Sum(x => x.DurationApiMs!.Value);
            double? endToEndTps = outputTokens > 0 && apiMs > 0
                ? (double)outputTokens / apiMs * 1000
                : null;
            double? medianTps = Median(ForMedian(items));
            rows.Add(new SpendAnswerSpeedRowDto(g.Key.Provider, g.Key.Model, endToEndTps,
                medianTps, items.Count, outputTokens, apiMs));
        }

        // Сортировка — по скорости по убыванию; группы без данных (null) в конце.
        var ordered = rows
            .OrderByDescending(r => r.EndToEndTps ?? double.NegativeInfinity)
            .ThenBy(r => r.Provider, StringComparer.Ordinal)
            .ThenBy(r => r.Model, StringComparer.Ordinal)
            .ToList();

        return new SpendAnswerSpeedDto(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"),
            MinTokensForMedian, ordered);
    }

    // Значения для медианы: по ходам с не меньшим числом выданных токенов
    private static List<double> ForMedian(List<SpendRecord> items) =>
        items.Where(x => x.OutputTokens >= MinTokensForMedian)
            .Select(x => (double)x.OutputTokens / x.DurationApiMs!.Value * 1000)
            .OrderBy(x => x)
            .ToList();

    // Медиана: нечётное число значений — среднее, чётное — среднее двух центральных
    private static double? Median(List<double> sorted)
    {
        if (sorted.Count == 0) return null;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
