namespace ClaudeHomeServer.Services.Notes.Wiki;

// A5 (очередь 2, волна A): чистый замер связности корпуса — та же формула, которой Вера меряла
// боевую вику после ночи 19.11 (Q1-QA4, «Связность по формуле Александра»): степень страницы =
// |mentions| + |mentionedHere| + |backlinks| из «Связанных» (WikiRelated), а source и chronicle
// — отдельным счётчиком, в степень не входят: карточка источника и хроника — служебные связи
// происхождения, а не связь темы с темой. Доля со связью — страницы со степенью ≥ 1.
//
// Степень считается несрезанной: Build вызывается с limit = null — иначе страница с 10 обратными
// ссылками дал бы 8 (потолок GroupLimit искажает среднее на живом корпусе: Вера видела
// степени 9). Срез — только вывод группы, дедупликация между группами до него: дубль остаётся
// в старшей группе и не удваивает степень.
//
// Три числа замера (очередь 2, разрез 20.11): K0 — прежняя степень (mentions + mentionedHere +
// backlinks, без topic; средняя, для сравнения с 19.11, AvgDegree); K1 — доля страниц, у которых
// есть хотя бы одна связь «тема ↔ тема» с другим источником: ребро из mentions, mentionedHere,
// backlinks или topic (входящие считаются — они уже в группах страницы-цели), концы которого не
// делят карточку источника (общая source-группа) и сосед — не карточка и не хроника; K2 — доля
// страниц с ≥ 1 резолвящейся [[…]] на страницу знаний (не на карточку). K1 не засчитывает связь,
// которую сам и создал: порог topic — из WikiTopicScore (калибровка T0), а пара «одна карточка
// источника» не даёт ребра. Индекс «По теме» — по видимым разделам страницы (один на набор allowed), как при показе;
// глобальный индекс по всему снимку запрещён: другой df — другие рёбра, чем видит человек.
//
// Чистая функция без I/O: вход — снимок страниц (тот же, что даёт вызывающий WikiRelated.Build)
// и subset — пути измеряемых страниц. Путь вне снимка или не разбираемый как страница
// (проекция, чужой путь) не измеряется и в Count не входит: метрика не догадывается о тексте,
// которого не видит.
public static class WikiConnectivity
{
    /// <summary>Результат замера: AvgDegree (K0) — средняя степень по измеренным, LinkedShare —
    /// доля страниц со степенью ≥ 1, Count — сколько страниц измерено (subset ∩ снимок);
    /// K1 и K2 — доли по измеренным (см. шапку). При Count = 0 все числа — 0 (пустой замер,
    /// а не деление на ноль).</summary>
    public sealed record Result(double AvgDegree, double LinkedShare, int Count, double K1 = 0, double K2 = 0)
    {
        public double K0 => AvgDegree;
    }

    private static readonly string[] DegreeKinds =
        [WikiRelated.KindMentions, WikiRelated.KindMentionedHere, WikiRelated.KindBacklinks];

    private static readonly string[] EdgeKinds =
        [WikiRelated.KindMentions, WikiRelated.KindMentionedHere, WikiRelated.KindBacklinks, WikiRelated.KindTopic];

    /// <summary>Признаки одной страницы: Degree — степень (K0), K1Edge — есть ребро «тема ↔ тема»,
    /// K2 — есть резолвящаяся [[…]] на страницу знаний. Из них склеивается любой срез страниц:
    /// ночь меряет один раз по объединению срезов и раскладывает результат по двум.</summary>
    public sealed record PageStat(int Degree, bool K1Edge, bool K2);

    /// <summary>Замер по subset на снимке pages: pages — полный снимок (его видит Build —
    /// связи измеряются против всего снимка, а не против subset).</summary>
    public static Result Measure(IReadOnlyList<WikiGate.Draft> pages, IReadOnlyList<string> subset) =>
        Measure(pages, subset, null);

    /// <summary>Тестовый шов: counter считает вызовы SignificantWords (сторож цены — индекс
    /// «По теме» один раз на набор видимых разделов, а не на каждую страницу).</summary>
    internal static Result Measure(IReadOnlyList<WikiGate.Draft> pages, IReadOnlyList<string> subset,
        WikiRelated.NormCounter? counter)
    {
        var stats = MeasurePages(pages, subset, counter);
        return Aggregate(subset.Where(stats.ContainsKey).Select(p => stats[p]));
    }

    /// <summary>Признаки каждой измеримой страницы subset (путь вне снимка или неразбираемый — без
    /// записи). Индекс «По теме» — по видимым разделам страницы, как при показе.</summary>
    public static IReadOnlyDictionary<string, PageStat> MeasurePages(IReadOnlyList<WikiGate.Draft> pages,
        IReadOnlyList<string> subset) => MeasurePages(pages, subset, null);

    internal static IReadOnlyDictionary<string, PageStat> MeasurePages(IReadOnlyList<WikiGate.Draft> pages,
        IReadOnlyList<string> subset, WikiRelated.NormCounter? counter)
    {
        var topicIndexes = new WikiRelated.TopicIndexCache(pages, counter);
        return MeasurePages(pages, subset, counter, topicIndexes);
    }

    internal static IReadOnlyDictionary<string, PageStat> MeasurePages(IReadOnlyList<WikiGate.Draft> pages,
        IReadOnlyList<string> subset, WikiRelated.NormCounter? counter, WikiRelated.TopicIndexCache topicIndexes)
    {
        var byPath = new Dictionary<string, WikiGate.Draft>(StringComparer.Ordinal);
        foreach (var d in pages)
            byPath.TryAdd(d.Path, d);
        var memo = new Dictionary<string, IReadOnlyList<WikiRelatedGroup>>(StringComparer.Ordinal);
        IReadOnlyList<WikiRelatedGroup> GroupsOf(string path)
        {
            if (!memo.TryGetValue(path, out var g))
            {
                var section = WikiSections.Parse(path);
                // Индекс — по видимым разделам самой страницы (тот же состав, что при показе).
                memo[path] = g = section is null
                    ? []
                    : WikiRelated.Build(byPath[path], pages, topicIndexes.For(section), limit: null, counter: counter);
            }
            return g;
        }
        HashSet<string> CardsOf(string path) => GroupsOf(path)
            .Where(g => g.Kind == WikiRelated.KindSource)
            .SelectMany(g => g.Items.Select(i => i.PageKey))
            .ToHashSet(StringComparer.Ordinal);

        var result = new Dictionary<string, PageStat>(StringComparer.Ordinal);
        foreach (var path in subset)
        {
            if (result.ContainsKey(path) || !byPath.TryGetValue(path, out var page))
                continue; // вне снимка: меры нет
            var groups = GroupsOf(path);
            // source и chronicle — отдельный счётчик: в степень не входят (формула замера).
            var degree = groups.Where(g => DegreeKinds.Contains(g.Kind)).Sum(g => g.Items.Count);

            var myCards = CardsOf(path);
            var hasEdge = groups.Where(g => EdgeKinds.Contains(g.Kind))
                .SelectMany(g => g.Items)
                .Any(i => i.Type is not (WikiFrontmatter.TypeSource or WikiFrontmatter.TypeChronicle) &&
                          byPath.ContainsKey(i.PageKey) && !myCards.Overlaps(CardsOf(i.PageKey)));
            result[path] = new PageStat(degree, hasEdge, WikiRelated.LinksToKnowledgePage(page, pages));
        }
        return result;
    }

    /// <summary>Свёртка признаков в агрегат; пусто — нули (а не деление на ноль).</summary>
    public static Result Aggregate(IEnumerable<PageStat> stats)
    {
        int sum = 0, linked = 0, k1 = 0, k2 = 0, count = 0;
        foreach (var s in stats)
        {
            sum += s.Degree;
            if (s.Degree >= 1)
                linked++;
            if (s.K1Edge)
                k1++;
            if (s.K2)
                k2++;
            count++;
        }
        if (count == 0)
            return new Result(0, 0, 0);
        return new Result((double)sum / count, (double)linked / count, count, (double)k1 / count, (double)k2 / count);
    }
}
