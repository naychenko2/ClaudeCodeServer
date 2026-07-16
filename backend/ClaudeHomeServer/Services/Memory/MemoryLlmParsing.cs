namespace ClaudeHomeServer.Services.Memory;

// Общие примитивы разбора ответа LLM для autolearn: извлечение сбалансированного JSON-фрагмента из
// «грязного» текста (преамбула/```-fence) и маппинг сырых записей в concrete-элементы стека. Персона
// и команда отличаются только enum'ом типа и типом элемента — задаются делегатами. Персона-специфичный
// разбор фокуса остаётся в персонном сервисе.
public static class MemoryLlmParsing
{
    // Сырой элемент из ответа модели (общий формат {type, text, salience?})
    public sealed record ItemRaw(string? Type, string? Text, double? Salience = null);

    // Маппинг сырых элементов в concrete-элементы: пустой текст пропускаем, тип парсим делегатом,
    // важность — дефолт 1.0 либо кламп в 0.05..1; не более 8 записей за раз.
    public static IReadOnlyList<TItem> MapItems<TItem, TType>(
        List<ItemRaw> parsed, Func<string?, TType> parseType, Func<TType, string, double, TItem> makeItem)
    {
        var result = new List<TItem>();
        foreach (var it in parsed)
        {
            var text = it.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            var type = parseType(it.Type);
            // Важность: отсутствует → 1.0, иначе кламп в 0.05..1
            var salience = it.Salience is null ? 1.0 : Math.Clamp(it.Salience.Value, 0.05, 1.0);
            result.Add(makeItem(type, text, salience));
        }
        return result.Take(8).ToList();
    }

    // Первый сбалансированный JSON-фрагмент между open/close (устойчиво к преамбуле/fence)
    public static string? ExtractBalanced(string raw, char open, char close)
    {
        var start = raw.IndexOf(open);
        if (start < 0) return null;
        int depth = 0; bool inStr = false, esc = false;
        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') inStr = true;
            else if (c == open) depth++;
            else if (c == close && --depth == 0) return raw[start..(i + 1)];
        }
        return null;
    }
}
