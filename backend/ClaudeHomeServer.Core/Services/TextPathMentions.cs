using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services;

// Пути файлов, упомянутые в свободном тексте («правлю backend/Services/Foo.cs», «…в api.ts:123»):
// токен с ≥1 разделителем пути и расширением. Лишние совпадения не страшны — результат идёт
// якорем ранжирования, точный матч делает OrdinalIgnoreCase-сравнение на стороне потребителя.
//
// Вынесено из `DossierRecallService.ExtractPathsFromText` (Этап 5, узкие швы Turn): сам сервис
// паспортов этот метод не звал НИ РАЗУ — единственным вызывающим был контрибьютор промпта
// в Turn. Это stateless-разбор текста, а не поведение вертикали, поэтому примитив уехал
// в спину по прецеденту `Slugifier`/`PathNormalizer`, а не спрятался за швом.
public static class TextPathMentions
{
    private static readonly Regex PathRx = new(
        @"[A-Za-z0-9_.\-]+(?:[/\\][A-Za-z0-9_.\-]+)+\.[A-Za-z0-9]{1,10}",
        RegexOptions.Compiled);

    public static IReadOnlyList<string> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var result = new List<string>();
        foreach (Match m in PathRx.Matches(text))
        {
            var p = m.Value.Replace('\\', '/');
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            result.Add(p);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
