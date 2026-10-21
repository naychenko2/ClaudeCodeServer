using ClaudeHomeServer.Models;

namespace ClaudeHomeServer.Services.Spend;

// Единая точка правды «цена от CLI ненадёжна» (доработка Б9b, ревью Глеба): один и тот же
// предикат используется в итоге расхода (SpendFeatureReader) и в разрезах фичи
// (FeatureBreakdown) — иначе одна и та же запись даёт в одном ответе две разные цифры
// (stats.spend.costUsd = 0, но breakdown.roles[step].costUsd = 2.0).
//
// Порядок проверок: Source=free (надёжный 0) ВЫШЕ «ненадёжно»: локальная модель с «glm»
// в имени — надёжный 0, а не ненадёжная модель (минор Глеба).
//
// Список моделей: конфиг `Spend:UnreliableCostModels` (список подстрок/идентификаторов,
// регистронезависимо); секция отсутствует или пуста — встроенный дефолт (deepseek, glm —
// модели, названные в решении 20.10). Предикат оценивается по ИМЕНИ модели из записи при
// чтении, а не запросом к Llm-реестру: прайс-таблица реестра — про подписку, а ненадёжна
// именно цена, которую CLI отчитал за стороннюю модель.
public static class SpendCostReliability
{
    // Встроенный дефолт: модели, названные в решении (20.10). Конфиг `Spend:UnreliableCostModels`
    // заменяет его целиком.
    public static readonly string[] DefaultUnreliableModels = ["deepseek", "glm"];

    // Цена записи ненадёжна: имя модели содержит одну из подстрок (регистронезависимо).
    // Source=free — надёжный 0, проверяется первым (выше «ненадёжно»).
    public static bool IsUnreliable(string? model, string source, string[]? unreliableModels = null)
    {
        if (source == SpendSources.Free) return false;
        if (model is null) return false;
        var models = (unreliableModels ?? DefaultUnreliableModels)
            .Select(m => m.Trim().ToLowerInvariant())
            .Where(m => m.Length > 0)
            .ToArray();
        return models.Any(p => model.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    // Надёжная стоимость ЗАПИСИ (decimal?) — единая точка правды для денег (доработка Б9c,
    // решение Александра): на неё переходят итог расхода (ComputeTotals), разрез (FeatureBreakdown)
    // и сплиттер (SharedSpendSplitter) — своих формул «цена надёжна» больше нет.
    //   0m  — free (локальная модель): надёжный 0, даже если сырая запись несёт цену;
    //   null — цена ненадёжна: (1) флаг UnreliableCost (модель в списке `Spend:UnreliableCostModels`),
    //          (2) нет цены при Source != free (CLI не отчитал), (3) CostUsd не конечен (NaN или ∞);
    //   иначе (decimal)CostUsd.
    public static decimal? ReliableCostOf(FeatureSpendRecord r)
    {
        if (r.Source == SpendSources.Free) return 0m;
        if (r.UnreliableCost) return null;
        if (r.CostUsd is null) return null;
        if (double.IsNaN(r.CostUsd.Value) || double.IsInfinity(r.CostUsd.Value)) return null;
        return (decimal)r.CostUsd.Value;
    }
}
