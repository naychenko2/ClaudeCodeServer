namespace ClaudeHomeServer.Tests.LocalBench;

/// <summary>
/// Разбор входа кейса оси B на части, которые место получает по-настоящему.
///
/// Вход кейса лежит в банке ОДНОЙ строкой (<see cref="LocalBenchCase.Input"/>) намеренно, а
/// не полями структуры. Причина в том, что эта строка обслуживает СРАЗУ ТРОИХ: по ней
/// заземляется разметка фактов, её же видит судья выдумок в файле пар и из неё же тест
/// собирает вход места. Разложи её по полям — и судья с разметкой начали бы смотреть на
/// одно, а модель получать другое, причём расхождение было бы невидимым.
///
/// Разметка строки — заголовки markdown, и только они:
///  • <c>## КОММИТ</c> / <c>## ИЗМЕНЕНИЯ</c> / <c>## ОБСУЖДЕНИЕ</c> / <c>## ЗАДАЧА</c> —
///    разделы входа (нужны паспорту изменения, у остальных мест раздела может не быть вовсе);
///  • <c>### Пользователь</c> / <c>### Ассистент</c> — реплики ленты чата.
/// Вход без единого <c>## </c>-заголовка считается одним разделом — так устроены входы
/// документных мест, где вход и есть текст документа.
/// </summary>
public static class LocalBenchInputs
{
    public const string CommitSection = "КОММИТ";
    public const string ChangesSection = "ИЗМЕНЕНИЯ";
    public const string FeedSection = "ОБСУЖДЕНИЕ";
    public const string TaskSection = "ЗАДАЧА";

    private const string UserMarker = "### Пользователь";
    private const string AssistantMarker = "### Ассистент";

    /// <summary>
    /// Разделы входа по именам заголовков. Заголовков нет — единственный раздел с пустым
    /// именем, он же весь вход.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Sections(string input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var name = "";
        var body = new List<string>();
        foreach (var line in SplitLines(input))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                name = line[3..].Trim();
                continue;
            }
            body.Add(line);
        }
        Flush();
        return result;

        void Flush()
        {
            if (body.Count == 0 && name.Length == 0) return;
            result[name] = string.Join("\n", body).Trim();
            body = [];
        }
    }

    /// <summary>Раздел по имени; нет такого — пустая строка.</summary>
    public static string Section(string input, string name) =>
        Sections(input).TryGetValue(name, out var body) ? body : "";

    /// <summary>
    /// Реплики ленты: пары «кто — что» по маркерам. Текст до первого маркера отбрасывается
    /// (это шапка раздела, а не реплика).
    /// </summary>
    public static IReadOnlyList<(bool FromUser, string Text)> Messages(string feed)
    {
        var result = new List<(bool, string)>();
        bool? fromUser = null;
        var body = new List<string>();
        foreach (var line in SplitLines(feed))
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Equals(UserMarker, StringComparison.Ordinal)
                || trimmed.Equals(AssistantMarker, StringComparison.Ordinal))
            {
                Flush();
                fromUser = trimmed.Equals(UserMarker, StringComparison.Ordinal);
                continue;
            }
            body.Add(line);
        }
        Flush();
        return result;

        void Flush()
        {
            if (fromUser is not { } who) { body = []; return; }
            var text = string.Join("\n", body).Trim();
            if (text.Length > 0) result.Add((who, text));
            body = [];
        }
    }

    /// <summary>Лента чата прямо из входа кейса (раздел ОБСУЖДЕНИЕ либо весь вход).</summary>
    public static IReadOnlyList<(bool FromUser, string Text)> Feed(string input)
    {
        var feed = Section(input, FeedSection);
        return Messages(feed.Length > 0 ? feed : input);
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
}
