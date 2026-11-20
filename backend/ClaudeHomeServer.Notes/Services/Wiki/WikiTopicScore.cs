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
    /// <summary>Порог суммы весов общих редких основ. 11 — нижняя граница корзины E (11+) замера T0:
    /// точность 0,80 при P = 0,7 (.cc-attachments/wiki-q2-topic/report.md, §3, таблица MinScore по P,
    /// строка P = 0,7 → 11; итог — «Рекомендация Веры»). Откалибровано на индексе со всеми страницами
    /// видимых разделов (карточки 227 из 604); при смене состава индекса — перемерить: шкала дрейфует
    /// вместе с долей карточек.</summary>
    public const double MinScore = 11;

    /// <summary>Доля страниц снимка, выше которой основа слишком частая. 0,25 подтверждено T0:
    /// 0,15 хуже, 0,35 от 0,25 не отличается (report.md, замер по MaxDfShare).</summary>
    public const double MaxDfShare = 0.25;

    /// <summary>Сколько общих редких основ нужно минимум: одно общее слово — не тема.</summary>
    public const int MinCommon = 2;

    private const double TitleBoth = 2;

    /// <summary>Страница снимка: путь, заголовок и сводка (null — сводки нет).</summary>
    public readonly record struct Entry(string Path, string Title, string? Summary);

    /// <summary>Индекс снимка: основы каждой страницы (заголовок вместе со сводкой) и df основ.
    /// Строится BuildIndex один раз. Основы одного заголовка нужны лишь для ×2 и считаются лениво,
    /// только у страниц допустимой по числу общих основ пары — на снимок остаётся ~N вызовов
    /// SignificantWords, а не по два на страницу.</summary>
    public sealed class Index
    {
        private readonly Func<string, HashSet<string>> _words;
        private readonly Dictionary<string, HashSet<string>> _titleSets = new(StringComparer.Ordinal);

        internal Index(Func<string, HashSet<string>> words) => _words = words;

        internal Dictionary<string, (HashSet<string> All, string Title)> Pages { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, int> Df { get; } = new(StringComparer.Ordinal);

        /// <summary>N — число страниц снимка.</summary>
        public int PageCount => Pages.Count;

        public bool Contains(string path) => Pages.ContainsKey(path);

        internal HashSet<string> TitleStems(string path)
        {
            if (!_titleSets.TryGetValue(path, out var set))
                _titleSets[path] = set = _words(Pages[path].Title);
            return set;
        }
    }

    /// <summary>Строит индекс снимка: один вызов SignificantWords на страницу (заголовок и
    /// сводка одним текстом). Повтор пути — первая запись остаётся. words — тестовый шов счёта
    /// вызовов; в обычном пути null.</summary>
    public static Index BuildIndex(IEnumerable<Entry> pages, Func<string, HashSet<string>>? words = null)
    {
        words ??= WikiCandidates.SignificantWords;
        var index = new Index(words);
        foreach (var e in pages)
        {
            if (index.Pages.ContainsKey(e.Path))
                continue;
            var all = words(e.Summary is { Length: > 0 } ? e.Title + " " + e.Summary : e.Title);
            index.Pages[e.Path] = (all, e.Title);
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
        var rare = new List<string>();
        foreach (var stem in small)
            if (large.Contains(stem) && index.Df[stem] <= MaxDfShare * n) // частая основа предмета не называет
                rare.Add(stem);
        if (rare.Count < MinCommon)
            return null;
        var titleA = index.TitleStems(pathA);
        var titleB = index.TitleStems(pathB);
        var sum = 0.0;
        foreach (var stem in rare)
        {
            var weight = Math.Log((double)n / index.Df[stem]);
            sum += titleA.Contains(stem) && titleB.Contains(stem) ? weight * TitleBoth : weight;
        }
        return sum >= minScore ? sum : null;
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

    /// <summary>Вложение основ заголовков (C1, page_duplicate по предмету): меньший набор основ
    /// целиком входит в больший и имеет не меньше MinCommon основ — одна общая основа не предмет.
    /// Основы — тот же токенизатор, что у меры (WikiCandidates.SignificantWords, как в BuildIndex).
    /// Пустой заголовок и набор короче MinCommon — false. Порогов и весов нет: в своём разделе
    /// слова-предметы стоят почти в каждом заголовке, их IDF мал, и оценка пары эту дубль-пару
    /// не ловит (перепостановка 20.11, консультация a2b00801) — здесь предикат без порога,
    /// точность даёт судья.</summary>
    public static bool TitleSubset(string titleA, string titleB)
    {
        if (string.IsNullOrEmpty(titleA) || string.IsNullOrEmpty(titleB))
            return false;
        var a = WikiCandidates.SignificantWords(titleA);
        var b = WikiCandidates.SignificantWords(titleB);
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        if (small.Count < MinCommon)
            return false;
        foreach (var stem in small)
            if (!large.Contains(stem))
                return false;
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex DateToken = new(
        @"\b(?:\d{4}[-./]\d{1,2}[-./]\d{1,2}|\d{1,2}[-./]\d{1,2}[-./]\d{2,4})\b",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Заголовки различаются только токеном даты («Отчёт 2026-11-05» и «Отчёт 2026-11-12»):
    /// после снятия дат (ГГГГ-ММ-ДД, ДД.ММ.ГГГГ) и схлопывания пробелов они равны без учёта регистра,
    /// а исходные — нет, и непустой остаток есть. Страницы-версии одного предмета: слияние схлопнуло
    /// бы снимки в алиасы, поэтому подбор merge такие пары не берёт. Предикат рядом с TitleSubset, но
    /// отдельный: TitleSubset не меняется — на нём откалиброваны константы замера T0.</summary>
    public static bool DateVersionsOnly(string titleA, string titleB)
    {
        if (string.IsNullOrWhiteSpace(titleA) || string.IsNullOrWhiteSpace(titleB))
            return false;
        if (string.Equals(titleA.Trim(), titleB.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        var a = StripDates(titleA);
        var b = StripDates(titleB);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDates(string title) =>
        string.Join(' ', DateToken.Replace(title, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
