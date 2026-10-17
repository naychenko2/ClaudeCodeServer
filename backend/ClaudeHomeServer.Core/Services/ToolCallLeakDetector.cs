namespace ClaudeHomeServer.Services;

// Детектор утечки вызова инструмента в текст (чистая функция, тестируется без сессии).
//
// Локальная модель (qwen3_coder через vLLM) иногда выдаёт вызов инструмента ТЕКСТОМ
// (upstream vllm-project/vllm#56077: ошибка потокового парсера при спекулятивном
// декодировании) — в text-блоке assistant-сообщения появляются маркеры XML-формата
// вызова. Ход при этом заканчивается end_turn, и платформа принимала его за успешный:
// задача висела в inProgress с claudeResult=success, а вызов так и не отработал.
//
// Закрытый набор маркеров: в обычном тексте модели они не встречаются (проверено по
// транскриптам 16–17.10: 9 утечек на 3696 текстовых сообщений). Любое вхождение — утечка.
public static class ToolCallLeakDetector
{
    // Маркеры XML-формата вызова инструмента.
    private static readonly string[] Signatures =
    {
        "<" + "/invoke>",
        "<" + "/function>",
        "<" + "parameter=",
        "<" + "parameter name=",
        "<" + "function=",
    };

    // Первый найденный маркер или null (утечки нет).
    public static string? Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        foreach (var sig in Signatures)
            if (text.Contains(sig, StringComparison.Ordinal)) return sig;
        return null;
    }
}
