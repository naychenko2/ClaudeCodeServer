using System.Text;
using System.Text.RegularExpressions;

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
//
// Мера A — только основной агент (parentId == null): сабагенту text-блоки CLI в stdout
// не транслирует (полный поток эмитит SubagentStreamWatcher из транскрипта), поэтому
// утечка вызова у сабагента мимо детектора проходит — осознанное ограничение.
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

    // Инлайн-код в markdown: `…` (в одной строке; незакрытый — до конца строки).
    private static readonly Regex InlineCode = new(@"`[^`\n]+`", RegexOptions.Compiled);

    // Имя инструмента в утёкшем вызове: XML-формат Qwen (<function=ИМЯ>) или Anthropic
    // (<invoke name="ИМЯ">). Нужно, чтобы отличить слип формата от вызова инструмента,
    // которого у сессии нет: Strata такой вызов возвращает текстом, и просьба «повтори
    // в штатном формате» не поможет никогда (22.10, задача A1 «Погода»).
    private static readonly Regex FunctionName = new(
        @"<function=([A-Za-z0-9_\-\.]+)>|<invoke name=""([A-Za-z0-9_\-\.]+)""", RegexOptions.Compiled);

    // Первый найденный маркер или null (утечки нет).
    public static string? Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var stripped = StripCodeSpans(text);
        foreach (var sig in Signatures)
            if (stripped.Contains(sig, StringComparison.Ordinal)) return sig;
        return null;
    }

    // Имя инструмента из первого утёкшего вызова (вне кода) или null — имени не видно.
    public static string? ExtractToolName(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = FunctionName.Match(StripCodeSpans(text));
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    // Вырезает содержимое markdown-код-блоков и инлайн-кода: маркеры XML-формата
    // вызова внутри кода — цитата (модель объясняет формат), а не утечка.
    //   • fenced-блок ```…``` (с любым языком после открывающих бэктиков; незакрытый
    //     блок — до конца текста);
    //   • инлайн-код `…`.
    private static string StripCodeSpans(string text)
    {
        // 1) Fenced-блоки: строка, начинающаяся с ``` (после неё — язык), до
        //    закрывающей строки ```; незакрытый блок — до конца текста.
        var sb = new StringBuilder();
        var inFence = false;
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue; // и открывающая (с языком), и закрывающая строка — код
            }
            if (inFence) continue;
            sb.Append(line).Append('\n');
        }

        // 2) Инлайн-код `…` (в одной строке).
        return InlineCode.Replace(sb.ToString(), " ");
    }
}
