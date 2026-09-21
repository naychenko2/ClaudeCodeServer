using System.Text.Json;

namespace ClaudeHomeServer.Tests.LocalBench.Places;

/// <summary>
/// Машинный оракул контракта места <c>notes-tags</c> (Батарея II, ось A).
///
/// Контракт задан промптом в <c>NotesAiService.SuggestTagsAsync</c>: до 5 коротких тегов
/// (одно слово или слова-через-дефис, без #, по-русски), ТОЛЬКО JSON-массивом строк, и
/// «если подходят теги из списка существующих — используй их».
///
/// Последнее судится СТРОГО: тег вне словаря — нарушение. Смысл места в едином словаре
/// базы, и свежий синоним на каждую заметку («тестирование» рядом с «qa») ломает ровно
/// то, ради чего теги и ставятся. Чтобы строгость была честной, словарь банка собран так,
/// что покрывает тему каждой заметки (реальные теги базы плюс реальные метки задач
/// владельца): выдуманный тег означает отказ дисциплины, а не дыру в словаре.
///
/// Разбор повторяет продуктовый <c>NotesAiService.ParseArray</c> (от первого «[» до
/// последнего «]»). Известное ограничение: продуктовый метод приватен, и разойдись он с
/// этой копией — замер узнает об этом не раньше, чем кто-то сверит их глазами.
/// </summary>
public static class NotesTagsOracle
{
    /// <summary>Потолок числа тегов из промпта места.</summary>
    public const int MaxTags = 5;

    /// <summary>Границы длины тега: продукт отбрасывает всё за их пределами.</summary>
    public const int MinTagChars = 2;
    public const int MaxTagChars = 30;

    /// <summary>Чем нарушен контракт. null — ответ валиден.</summary>
    public static string? Violation(string? rawAnswer, IReadOnlyCollection<string> vocabulary)
    {
        if (string.IsNullOrWhiteSpace(rawAnswer)) return "пустой ответ";

        var json = ExtractJsonArray(rawAnswer);
        if (json is null) return "в ответе нет JSON-массива";

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return "JSON не разбирается"; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return "ответ не массив";

            var tags = new List<string>();
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String) return "элемент массива не строка";
                tags.Add(el.GetString() ?? "");
            }

            // Пустой массив — не «тегов не нашлось», а молчаливая деградация: заметка
            // остаётся без тегов, и человек не отличит это от «модель не ответила».
            if (tags.Count == 0) return "пустой массив тегов";
            if (tags.Count > MaxTags) return $"тегов больше {MaxTags}";

            var known = new HashSet<string>(vocabulary, StringComparer.OrdinalIgnoreCase);
            foreach (var raw in tags)
            {
                var tag = raw.Trim();
                if (tag.Length == 0) return "пустой тег";
                if (tag.StartsWith('#')) return $"тег с решёткой «{tag}»";
                if (tag.Any(char.IsWhiteSpace)) return $"тег из нескольких слов «{tag}»";
                if (tag.Length is < MinTagChars or > MaxTagChars)
                    return $"длина тега вне {MinTagChars}..{MaxTagChars}: «{tag}»";
                if (!known.Contains(tag)) return $"тег вне словаря: «{tag}»";
            }
            return null;
        }
    }

    /// <summary>
    /// Сколько тегов ответа входит в словарь и сколько их всего — для колонки результата:
    /// по этой дроби видно, промахнулась модель одним тегом или выдумала весь набор.
    /// </summary>
    public static (int Known, int Total) VocabularyHit(string? rawAnswer,
        IReadOnlyCollection<string> vocabulary)
    {
        var json = rawAnswer is null ? null : ExtractJsonArray(rawAnswer);
        if (json is null) return (0, 0);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (0, 0);
            var known = new HashSet<string>(vocabulary, StringComparer.OrdinalIgnoreCase);
            var tags = doc.RootElement.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim()).ToArray();
            return (tags.Count(known.Contains), tags.Length);
        }
        catch (JsonException) { return (0, 0); }
    }

    // Повтор продуктового ParseArray: текст вокруг массива отбрасывается.
    private static string? ExtractJsonArray(string raw)
    {
        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        return start < 0 || end <= start ? null : raw[start..(end + 1)];
    }
}
