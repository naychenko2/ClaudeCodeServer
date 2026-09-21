using System.Text.Json;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>task-classify</c> (Батарея II, ось A).
///
/// Контракт задан промптом в <c>TaskAiService.ClassifyAsync</c>: ТОЛЬКО JSON вида
/// <c>{"priority":"low|medium|high|urgent","labels":["метка",…]}</c>, до 3 меток,
/// каждая — одно-два слова без решётки.
///
/// JSON достаётся ПРОДУКТОВЫМ разбором (<see cref="TaskAiService.ExtractJsonObject"/>):
/// оракул обязан судить ровно тем, что съест продукт.
///
/// Судится ФОРМА. «Верный ли приоритет» — ось D и другой оракул: у половины реальных
/// задач приоритет спорен и для человека, а замер контракта не должен зависеть от вкуса.
/// Эталонный приоритет лежит в банке (expect) и печатается рядом с ответом — сверять
/// разумность выбора человек будет по колонке результата.
/// </summary>
public static class TaskClassifyOracle
{
    /// <summary>Потолок числа меток из промпта места.</summary>
    public const int MaxLabels = 3;

    /// <summary>Потолок длины метки: продукт молча отбрасывает всё длиннее.</summary>
    public const int MaxLabelChars = 30;

    private static readonly string[] Priorities = ["low", "medium", "high", "urgent"];

    /// <summary>Чем нарушен контракт. null — ответ валиден.</summary>
    public static string? Violation(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return "пустой ответ";

        var json = TaskAiService.ExtractJsonObject(rawAnswer);
        if (json is null) return "в ответе нет JSON-объекта";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return "JSON не разбирается"; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "ответ не объект";

            if (!root.TryGetProperty("priority", out var p)) return "нет ключа priority";
            if (p.ValueKind != JsonValueKind.String) return "priority не строка";
            var priority = p.GetString()?.Trim() ?? "";
            // Регистр не прощаем: продуктовый NormalizePriority сверяет с нижним
            // регистром после ToLowerInvariant, но «Высокий» вместо «high» он уже
            // выбросит — приоритет молча станет пустым, и карточка уедет в medium.
            if (!Priorities.Contains(priority, StringComparer.Ordinal))
                return $"priority вне перечисления: «{priority}»";

            if (!root.TryGetProperty("labels", out var l)) return "нет ключа labels";
            if (l.ValueKind != JsonValueKind.Array) return "labels не массив";

            var count = 0;
            foreach (var el in l.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) return "метка не строка";
                var label = (el.GetString() ?? "").Trim();
                if (label.Length == 0) return "пустая метка";
                if (label.StartsWith('#')) return $"метка с решёткой «{label}»";
                if (label.Length > MaxLabelChars) return $"метка длиннее {MaxLabelChars}: «{label}»";
                // «одно-два слова» из промпта: фраза в метке превращает список меток в
                // список фраз, по которым потом не сгруппировать ни одну задачу.
                if (label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2)
                    return $"метка длиннее двух слов: «{label}»";
                count++;
            }
            if (count > MaxLabels) return $"меток больше {MaxLabels}";
            return null;
        }
    }
}
