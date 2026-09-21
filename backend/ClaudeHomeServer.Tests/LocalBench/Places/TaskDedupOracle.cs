using System.Text.Json;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>task-dedup</c> (Батарея II, ось A).
///
/// Контракт задан промптом в <c>TaskAiService.FindDuplicateAsync</c>: ТОЛЬКО JSON вида
/// <c>{"duplicateId":"&lt;id из списка или null&gt;","reason":"кратко почему"}</c>,
/// «не выдумывай id».
///
/// Судится ровно это: id ИЗ СПИСКА кандидатов либо null. Выдуманный id продукт отбивает
/// сам (страховка от галлюцинаций в <c>FindDuplicateAsync</c>) и молча возвращает «дубля
/// нет» — по выходу места такой отказ неотличим от честного null, и увидеть его можно
/// только здесь, в сыром ответе.
///
/// «Тот ли дубль найден» оракул не судит: эталон лежит в банке (expect.duplicateId) и
/// печатается рядом с ответом — разумность выбора человек сверяет по колонке результата.
/// </summary>
public static class TaskDedupOracle
{
    /// <summary>Чем нарушен контракт. null — ответ валиден.</summary>
    public static string? Violation(string? rawAnswer, IReadOnlyCollection<string> candidateIds)
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
            if (!root.TryGetProperty("duplicateId", out var d)) return "нет ключа duplicateId";

            switch (d.ValueKind)
            {
                case JsonValueKind.Null:
                    return null;   // «дубля нет» — половина кейсов банка именно такая
                case JsonValueKind.String:
                    var id = d.GetString()?.Trim() ?? "";
                    // Строковое «null» — обычный ответ модели, и продукт его переживает
                    // (id не из списка → дубля нет). Контракт он всё же нарушает: ключ
                    // объявлен как id либо null, а не как слово «null».
                    if (id.Length == 0) return "duplicateId — пустая строка";
                    if (id.Equals("null", StringComparison.OrdinalIgnoreCase))
                        return "duplicateId строкой «null» вместо JSON-null";
                    return candidateIds.Contains(id)
                        ? null
                        : $"duplicateId не из списка кандидатов: «{Cut(id)}»";
                default:
                    return $"duplicateId не строка и не null ({d.ValueKind})";
            }
        }
    }

    private static string Cut(string s) => s.Length <= 40 ? s : s[..39] + "…";
}
