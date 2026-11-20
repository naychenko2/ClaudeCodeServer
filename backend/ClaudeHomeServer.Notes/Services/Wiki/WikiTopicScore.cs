namespace ClaudeHomeServer.Services.Notes.Wiki;

// «По теме» (выкат 1 очереди 2): чистая оценка близости двух страниц по редким общим основам.
// Основы — WikiCandidates.SignificantWords (второго токенизатора нет), текст страницы —
// заголовок плюс сводка (WikiProjections.SummaryOf, второго определения сводки нет).
// Вес основы — ln(N/df) по снимку (весь снимок, а не раздел страницы); основа, которая есть
// больше чем у MaxDfShare страниц, не считается — она не называет предмет; основа из обоих
// заголовков весит ×2. Пара допустима, только если общих редких основ не меньше MinCommon
// и сумма не меньше minScore. Диска не читает, ничего не пишет; индекс (df) строится один
// раз на снимок, оценка пары — по готовому индексу.
public static class WikiTopicScore
{
    /// <summary>Порог суммы весов общих редких основ. Временное значение: окончательное даст замер T0 (T1b).</summary>
    public const double MinScore = 6;

    /// <summary>Доля страниц снимка, выше которой основа слишком частая. Временное значение (T0 → T1b).</summary>
    public const double MaxDfShare = 0.25;

    /// <summary>Сколько общих редких основ нужно минимум: одно общее слово — не тема.</summary>
    public const int MinCommon = 2;

    private const double TitleBoth = 2;

    /// <summary>Страница снимка: путь, заголовок и сводка (null — сводки нет).</summary>
    public readonly record struct Entry(string Path, string Title, string? Summary);

    /// <summary>Индекс снимка: основы каждой страницы и df основ. Строится BuildIndex один раз.</summary>
    public sealed class Index
    {
        internal Dictionary<string, (HashSet<string> All, HashSet<string> Title)> Pages { get; } =
            new(StringComparer.Ordinal);

        internal Dictionary<string, int> Df { get; } = new(StringComparer.Ordinal);

        /// <summary>N — число страниц снимка.</summary>
        public int PageCount => Pages.Count;

        public bool Contains(string path) => Pages.ContainsKey(path);
    }

    /// <summary>Строит индекс снимка: по два вызова SignificantWords на страницу (заголовок,
    /// сводка) — не на пару. Повтор пути — первая запись остаётся. words — тестовый шов
    /// счёта вызовов; в обычном пути null.</summary>
    public static Index BuildIndex(IEnumerable<Entry> pages, Func<string, HashSet<string>>? words = null)
    {
        words ??= WikiCandidates.SignificantWords;
        var index = new Index();
        foreach (var e in pages)
        {
            if (index.Pages.ContainsKey(e.Path))
                continue;
            var title = words(e.Title);
            var all = new HashSet<string>(title, StringComparer.Ordinal);
            if (e.Summary is { Length: > 0 })
                all.UnionWith(words(e.Summary));
            index.Pages[e.Path] = (all, title);
            foreach (var stem in all)
                index.Df[stem] = index.Df.GetValueOrDefault(stem) + 1;
        }
        return index;
    }

    /// <summary>Оценка пары по готовому индексу: сумма весов общих редких основ или null, если
    /// пара недопустима (нет страницы в индексе, общих редких основ меньше MinCommon, сумма
    /// меньше minScore). minScore = 0 — «все пары с оценкой, без порога» (замер T0); число
    /// общих основ проверяется и тогда.</summary>
    public static double? Score(Index index, string pathA, string pathB, double minScore = MinScore)
    {
        if (!index.Pages.TryGetValue(pathA, out var a) || !index.Pages.TryGetValue(pathB, out var b))
            return null;
        var n = index.PageCount;
        var (small, large) = a.All.Count <= b.All.Count ? (a.All, b.All) : (b.All, a.All);
        var common = 0;
        var sum = 0.0;
        foreach (var stem in small)
        {
            if (!large.Contains(stem))
                continue;
            var df = index.Df[stem];
            if (df > MaxDfShare * n)
                continue; // слишком частая основа предмета не называет
            common++;
            var weight = Math.Log((double)n / df);
            if (a.Title.Contains(stem) && b.Title.Contains(stem))
                weight *= TitleBoth;
            sum += weight;
        }
        return common >= MinCommon && sum >= minScore ? sum : null;
    }

    /// <summary>Кандидаты страницы по убыванию оценки; при равенстве — по Ordinal пути.
    /// Недопустимые пары не входят. take — потолок (null — без потолка).</summary>
    public static List<(string Path, double Score)> Rank(Index index, string path,
        IEnumerable<string> candidates, double minScore = MinScore, int? take = null)
    {
        var scored = new List<(string Path, double Score)>();
        foreach (var c in candidates)
        {
            if (c == path)
                continue;
            if (Score(index, path, c, minScore) is { } s)
                scored.Add((c, s));
        }
        return [.. scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.Ordinal)
            .Take(take ?? int.MaxValue)];
    }
}
