using System.Text.Json;
using ClaudeHomeServer.Services.Tasks;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>task-normalize-title</c> (тест 10.6 Батареи II).
///
/// Контракт места задан промптом в <see cref="TaskAiService.NormalizeTitleAsync"/>:
/// ТОЛЬКО JSON вида <c>{"title":"…","dueHint":"…|null"}</c>, заголовок — одна строка в
/// повелительном наклонении без служебных префиксов и обрамляющих кавычек.
///
/// JSON достаётся ПРОДУКТОВЫМ разбором (<see cref="TaskAiService.ExtractJsonObject"/>):
/// оракул обязан судить ровно тем, что съест продукт, — свой парсер расходился бы с ним
/// в терпимости к преамбуле и ```-обёртке и давал бы валидность, которой у места нет.
///
/// Оракул судит ФОРМУ, а не вкус: «смысл сохранён» и «падеж верный» — ось D, другой
/// оракул и другая цена ошибки.
/// </summary>
public static class TaskNormalizeTitleOracle
{
    /// <summary>
    /// Потолок длины заголовка. Своего лимита у места нет — заголовок живёт одной строкой
    /// карточки задачи, и ответ длиннее этого перестаёт быть заголовком независимо от того,
    /// что продукт его формально примет.
    /// </summary>
    public const int MaxTitleChars = 120;

    // Служебные приставки, которыми модель норовит начать ответ вместо чистого заголовка.
    private static readonly string[] ForbiddenPrefixes =
        ["задача:", "заголовок:", "title:", "результат:", "ответ:"];

    /// <summary>
    /// Чем нарушен контракт. null — ответ валиден.
    /// </summary>
    public static string? Violation(string? rawAnswer)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return "пустой ответ";

        var json = TaskAiService.ExtractJsonObject(rawAnswer);
        if (json is null) return "в ответе нет JSON-объекта";

        JsonElement root;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return "JSON не разбирается"; }
        using (doc)
        {
            root = doc.RootElement;

            if (!root.TryGetProperty("title", out var titleElement))
                return "нет ключа title";
            if (titleElement.ValueKind != JsonValueKind.String)
                return "title не строка";

            var title = titleElement.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(title)) return "title пустой";
            if (title.Contains('\n') || title.Contains('\r')) return "title в несколько строк";
            if (title.Length > MaxTitleChars) return $"title длиннее {MaxTitleChars} символов";

            var trimmed = title.Trim();
            if (IsQuoted(trimmed)) return "title в обрамляющих кавычках";
            foreach (var prefix in ForbiddenPrefixes)
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return $"служебный префикс «{prefix}» в title";

            // dueHint необязателен, но если пришёл — обязан быть строкой или null:
            // число или объект вызывающий разберёт как «срока нет» и молча его потеряет.
            if (root.TryGetProperty("dueHint", out var due)
                && due.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                return "dueHint не строка и не null";

            return null;
        }
    }

    private static bool IsQuoted(string s) =>
        s.Length >= 2
        && ((s[0] == '"' && s[^1] == '"')
            || (s[0] == '«' && s[^1] == '»')
            || (s[0] == '\'' && s[^1] == '\''));
}
