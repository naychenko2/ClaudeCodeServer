namespace ClaudeHomeServer.Services.Memory;

// Полнотекстовый fallback памяти (когда семантический слой Dify недоступен). Оба стека токенизируют
// строки — это общий примитив Tokenize. Дальше стеки расходятся ОСОЗНАННО (поведение recall менять
// нельзя), поэтому у каждого своя точка входа:
//  - Relevance (персона): доля терминов запроса, встретившихся ПОДСТРОКОЙ в «текст + теги» записи;
//    результат 0..1 идёт во взвешенный скоринг PersonaMemoryScorer. Пустой запрос → все по 0.5.
//    Делимитеры без \r/()/трима, без стоп-слов.
//  - Rank (команда): ранжирование по числу общих ТОКЕНОВ запроса и текста записи (без тегов);
//    результат — сами записи, без скоринга. Делимитеры с \r/()/тримом и стоп-словами.
public static class MemoryFulltext
{
    // Делимитеры и стоп-слова стеков (различия сохраняют исходное поведение каждого fallback'а)
    private static readonly char[] PersonaDelimiters = { ' ', ',', '.', ';', ':', '!', '?', '\n', '\t' };
    private static readonly char[] TeamDelimiters = { ' ', ',', '.', ';', ':', '!', '?', '\n', '\r', '\t', '(', ')' };

    private static readonly HashSet<string> TeamStopWords = new(StringComparer.OrdinalIgnoreCase)
    { "и", "в", "на", "с", "по", "для", "не", "что", "это", "как", "to", "the", "a", "of", "and", "for", "in" };

    // Общий токенизатор: lower → split → термины длиннее 2 символов → (опц.) без стоп-слов → distinct
    private static string[] Tokenize(string s, char[] delimiters, StringSplitOptions options, HashSet<string>? stop) =>
        s.ToLowerInvariant().Split(delimiters, options)
            .Where(t => t.Length > 2 && (stop is null || !stop.Contains(t)))
            .Distinct()
            .ToArray();

    // Персона: relevance по entryId (0..1) — доля терминов запроса, найденных подстрокой в «текст + теги».
    // Пустой запрос — все записи равнозначно релевантны (0.5), свежесть решит в скоринге.
    public static Dictionary<string, double> Relevance<TEntry>(
        IReadOnlyList<TEntry> entries, string query,
        Func<TEntry, string> id, Func<TEntry, string> text, Func<TEntry, IReadOnlyList<string>?> tags)
    {
        var terms = Tokenize(query, PersonaDelimiters, StringSplitOptions.RemoveEmptyEntries, null);
        var result = new Dictionary<string, double>();
        if (terms.Length == 0)
        {
            foreach (var e in entries) result[id(e)] = 0.5;
            return result;
        }
        foreach (var e in entries)
        {
            var hay = (text(e) + " " + string.Join(' ', tags(e) ?? [])).ToLowerInvariant();
            var matched = terms.Count(t => hay.Contains(t));
            if (matched > 0) result[id(e)] = (double)matched / terms.Length;
        }
        return result;
    }

    // Команда: ранжирование по перекрытию токенов запроса и текста записи (устойчиво к отсутствию Dify)
    public static List<TEntry> Rank<TEntry>(
        IReadOnlyList<TEntry> snapshot, string query, int topK, Func<TEntry, string> text)
    {
        var q = Tokenize(query, TeamDelimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, TeamStopWords);
        if (q.Length == 0) return [];
        return snapshot
            .Select(e => (e, score: Tokenize(text(e), TeamDelimiters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, TeamStopWords).Count(t => q.Contains(t))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => x.e)
            .ToList();
    }
}
