using System.Globalization;
using System.Text;
using System.Text.Json;
using ClaudeHomeServer.Services.Notes;
using ClaudeHomeServer.Services.Notes.Wiki;
using FluentAssertions;
using Xunit;

namespace ClaudeHomeServer.Tests.Services;

// Сухой прогон «По теме» (T0, выкат 1 очереди 2): замер ТОЧНОСТИ и связности на КОПИИ вики.
// Только чтение — боевые данные не трогаются. Каталог копии — WIKI_DRYRUN_DIR (ожидается
// <корень>/data, внутри pages/ и keeper/); без переменной прогон ПРОПУСКАЕТСЯ
// (DryRunFactAttribute из WikiAutoLinkDryRunTests).
// Артефакты — в WIKI_TOPIC_OUT (по умолчанию <корень>/.cc-attachments/wiki-q2-topic).
// Что считает:
// - для каждой страницы знаний — пара «страница ↔ кандидат» с оценкой WikiTopicScore
//   (MinScore = 0 — все пары с оценкой, без порога) ТЕМ ЖЕ кодом продукта;
// - индекс N и df — по продуктовому снимку: все страницы видимых разделов, ВКЛЮЧАЯ карточки
//   источников и хроники (кандидатами они не бывают, но в N и df входят — замечание ревью T1).
//   Рядом считается второй индекс «знания без карточек и хроник» — оценка «без карточек»;
// - варианты MaxDfShare (0,15 / 0,25 / 0,35): вес тот же (ln(N/df)), меняется отсечка частоты;
//   пересчёт сходится с продуктовым Score на 0,25 (сверка в тесте);
// - K0 (прежняя степень: mentions + mentionedHere + backlinks), K1 (покрытие «тема ↔ тема»)
//   и K2 (доля страниц с резолвящейся ссылкой на страницу знаний) — по всей вики и по
//   страницам ночи 19.11; при каждом пороге: крупнейшая компонента и доля изолированных;
// - стратифицированная выборка пар (по 20 из пяти корзин оценки) — на неё ставится разметка.
// Корзины (границы) подобраны по распределению оценок на снимке 19.11: границы круглые,
// чтобы предложенный MinScore был круглым числом. Оценки округляются до 1e-9: равные по смыслу
// оценки не должны разойтись на ulp.
[Trait("Category", "Bench")]
public class WikiTopicDryRunTests
{
    private sealed record Bucket(string Name, double Lo, double Hi);

    private static readonly Bucket[] Buckets =
    [
        new("A: 3-5", 3, 5),
        new("B: 5-7", 5, 7),
        new("C: 7-9", 7, 9),
        new("D: 9-11", 9, 11),
        new("E: 11+", 11, double.PositiveInfinity),
    ];

    private const double Eps = 1e-9;

    // Страница снимка: путь под pages/, раздел, текст, разобранная модель.
    private sealed record Page(string Path, string? Section, string Text, WikiPage? Model);

    // Пара «страница ↔ кандидат» с оценками (продуктовая, «без карточек», варианты отсечки).
    private sealed record Pair(
        string A, string B, string TitleA, string TitleB, string Section,
        double Score, double ScoreKnowOnly, double ScoreDf15, double ScoreDf35,
        int Rare, string Stems);

    // Контекст страницы знаний: индексы, пул кандидатов «По теме», группы «Связанных».
    private sealed record TargetContext(
        WikiTopicScore.Index Index,
        WikiTopicScore.Index IndexKnowOnly,
        List<string> Pool,
        IReadOnlyList<WikiRelatedGroup> Groups);

    private sealed record KMetrics(
        string Threshold, string Slice, int Pages, int TopicEdges,
        double K0Share, double K0Avg, double K1Share, double K1TopicShare, double K2Share,
        double LargestComponent, double IsolatedShare);

    [DryRunFact]
    public void СухойПрогон_ПоТеме_ПишетПарыИСвязность()
    {
        var dir = Environment.GetEnvironmentVariable("WIKI_DRYRUN_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return; // пропуск: без каталога копии считать нечего

        var pagesRoot = Path.Combine(dir, "pages");
        Directory.Exists(pagesRoot).Should().BeTrue("в каталоге копии должен быть pages/");

        var pages = LoadPages(pagesRoot);
        var byPath = pages.ToDictionary(p => p.Path, StringComparer.Ordinal);
        var drafts = pages.Where(p => p.Model is not null)
            .Select(p => new WikiGate.Draft(p.Path, p.Text)).ToList();
        var knowledge = pages.Where(IsKnowledge).ToList();
        var sourceCards = pages.ToDictionary(p => p.Path, SourceCardsOf, StringComparer.Ordinal);

        // ─── пары «страница ↔ кандидат» с оценкой (порог 0 — все пары с оценкой) ───
        var pairs = new Dictionary<string, Pair>(StringComparer.Ordinal);
        var contexts = new Dictionary<string, TargetContext>(StringComparer.Ordinal);
        foreach (var target in knowledge)
        {
            var ctx = BuildContext(target, pages, byPath, drafts);
            contexts[target.Path] = ctx;
            foreach (var cand in ctx.Pool)
            {
                var product = WikiTopicScore.Score(ctx.Index, target.Path, cand, minScore: 0);
                if (product is null)
                    continue; // общих редких основ меньше MinCommon — пара не оценивается вовсе
                var mine = ScoreWith(ctx.Index, target.Path, cand, WikiTopicScore.MaxDfShare);
                (mine.Sum - product.Value).Should().BeLessThan(Eps,
                    "пересчёт веса сходится с кодом продукта (округление 1e-9)");
                var know = ScoreWith(ctx.IndexKnowOnly, target.Path, cand, WikiTopicScore.MaxDfShare);
                var df15 = ScoreWith(ctx.Index, target.Path, cand, 0.15);
                var df35 = ScoreWith(ctx.Index, target.Path, cand, 0.35);
                var key = PairKey(target.Path, cand);
                if (pairs.ContainsKey(key))
                    continue; // пара считается один раз, независимо от направления
                pairs[key] = new Pair(
                    target.Path, cand, target.Model!.Title, byPath[cand].Model!.Title, target.Section!,
                    Round(product.Value), Round(know.Sum), Round(df15.Sum), Round(df35.Sum),
                    mine.Rare, string.Join(" ", mine.Stems.OrderBy(s => s, StringComparer.Ordinal)));
            }
        }
        pairs.Values.Should().NotBeEmpty("на снимке должны быть пары с оценкой");

        // ─── выборка: по 20 пар из каждой корзины ───
        var sample = SamplePairs(pairs.Values);

        // ─── связность: K0 / K1 / K2 при каждом пороге оценки ───
        var thresholds = ParseThresholds();
        var night19 = NightPages(dir, "2026-11-19");
        var nightKnowledge = knowledge
            .Where(p => night19.Contains(p.Path))
            .Select(p => p.Path)
            .ToHashSet(StringComparer.Ordinal);
        var metrics = new List<KMetrics>();
        foreach (var threshold in thresholds)
        {
            var graph = BuildGraph(knowledge, contexts, sourceCards, threshold);
            metrics.Add(Summarize(graph, knowledge, nightKnowledge, threshold, "вся вики", null));
            metrics.Add(Summarize(graph, knowledge, nightKnowledge, threshold, "ночь 19.11 (тронутые знания)", nightKnowledge));
        }

        // ─── артефакты ───
        var outDir = OutputDir();
        Directory.CreateDirectory(outDir);
        WritePairs(Path.Combine(outDir, "pairs-all.csv"), pairs.Values);
        WriteSample(Path.Combine(outDir, "sample-label.csv"), sample, pages, byPath);
        WriteSampleScores(Path.Combine(outDir, "sample-scores.csv"), sample);
        var payload = new
        {
            generatedAt = DateTime.UtcNow.ToString("o"),
            copyDir = Path.GetFullPath(dir),
            pagesTotal = pages.Count,
            pagesParsed = pages.Count(p => p.Model is not null),
            knowledgePages = knowledge.Count,
            sourceCards = pages.Count(p => p.Model?.Frontmatter.Type == WikiFrontmatter.TypeSource),
            chronicles = pages.Count(p => p.Model?.Frontmatter.Type == WikiFrontmatter.TypeChronicle),
            sections = pages.Where(p => p.Section is not null).Select(p => p.Section!).Distinct().Count(),
            pairsTotal = pairs.Count,
            bucketEdges = Buckets.Select(b => new { b.Name, b.Lo, Hi = double.IsInfinity(b.Hi) ? (double?)null : b.Hi }).ToList(),
            histogram = Histogram(pairs.Values.Select(p => p.Score)),
            metrics,
            night19KnowledgePages = nightKnowledge.Count,
        };
        File.WriteAllText(Path.Combine(outDir, "metrics.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        metrics.Should().NotBeEmpty("замер обязан что-то посчитать");
        sample.Should().NotBeEmpty("выборка пар обязательна");
    }

    // ─── страницы снимка ───

    private static List<Page> LoadPages(string pagesRoot)
    {
        var list = new List<Page>();
        foreach (var full in Directory.EnumerateFiles(pagesRoot, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(pagesRoot, full).Replace(Path.DirectorySeparatorChar, '/');
            var text = File.ReadAllText(full);
            var parsed = WikiPage.Parse(text);
            list.Add(new Page(rel, WikiSections.Parse(rel), text, parsed.Model));
        }
        return [.. list.OrderBy(p => p.Path, StringComparer.Ordinal)];
    }

    // Страница знаний: не карточка источника, не хроника, не проекция «_*».
    private static bool IsKnowledge(Page p) =>
        p.Model is not null &&
        p.Section is not null &&
        p.Model.Frontmatter.Type != WikiFrontmatter.TypeSource &&
        p.Model.Frontmatter.Type != WikiFrontmatter.TypeChronicle &&
        !WikiPaths.IsProjection(p.Path);

    // Карточки источника, на которые страница ссылается сносками: концы ребра, делящие
    // карточку, «темой» не считаются (мера K1).
    private static HashSet<string> SourceCardsOf(Page p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (p.Model is null)
            return set;
        foreach (var footnote in p.Model.Footnotes)
            if (footnote.Target.IsLink)
                set.Add(WikiGate.Norm(WikiPage.LinkTarget(footnote.Target.Text)));
        return set;
    }

    // Контекст страницы знаний: снимок индекса (как у продукта), пул кандидатов «По теме»
    // и группы «Связанных» (из них берётся «занятость» старшими группами).
    private static TargetContext BuildContext(Page target, List<Page> pages, Dictionary<string, Page> byPath,
        List<WikiGate.Draft> drafts)
    {
        var section = target.Section!;
        var allowed = new HashSet<string>(StringComparer.Ordinal) { section, WikiSections.Common };
        var visible = pages
            .Where(p => p.Model is not null && p.Section is not null && !WikiPaths.IsProjection(p.Path)
                        && p.Path != target.Path && allowed.Contains(p.Section!))
            .ToList();
        var entries = visible
            .Select(p => new WikiTopicScore.Entry(p.Path, p.Model!.Title,
                WikiProjections.SummaryOf(p.Model.Frontmatter, p.Text)))
            .ToList();
        entries.Add(new WikiTopicScore.Entry(target.Path, target.Model!.Title,
            WikiProjections.SummaryOf(target.Model!.Frontmatter, target.Text)));
        var index = WikiTopicScore.BuildIndex(entries);
        var knowEntries = entries.Where(e => IsKnowledge(byPath[e.Path])).ToList();
        var indexKnowOnly = WikiTopicScore.BuildIndex(knowEntries);
        var groups = WikiRelated.Build(new WikiGate.Draft(target.Path, target.Text), drafts, limit: null);
        var taken = new HashSet<string>(groups
            .Where(g => g.Kind != WikiRelated.KindTopic)
            .SelectMany(g => g.Items)
            .Select(i => i.PageKey), StringComparer.Ordinal);
        var pool = visible
            .Where(p => p.Model!.Frontmatter.Type != WikiFrontmatter.TypeSource
                        && p.Model.Frontmatter.Type != WikiFrontmatter.TypeChronicle
                        && !taken.Contains(p.Path))
            .Select(p => p.Path)
            .ToList();
        return new TargetContext(index, indexKnowOnly, pool, groups);
    }

    // Оценка пары по индексу с заданной отсечкой частоты основы (MaxDfShare): вес тот же,
    // что у продукта (ln(N/df), ×2 за общий заголовок), меняется только отсечка.
    private static (double Sum, int Rare, List<string> Stems) ScoreWith(
        WikiTopicScore.Index index, string a, string b, double maxDfShare)
    {
        var pa = index.Pages[a];
        var pb = index.Pages[b];
        var ta = index.TitleStems(a);
        var tb = index.TitleStems(b);
        var n = index.PageCount;
        var stems = new List<string>();
        var sum = 0.0;
        foreach (var stem in pa.All)
        {
            if (!pb.All.Contains(stem))
                continue;
            var df = index.Df[stem];
            if (df > maxDfShare * n)
                continue; // частая основа предмета не называет
            stems.Add(stem);
            var weight = Math.Log((double)n / df);
            sum += ta.Contains(stem) && tb.Contains(stem) ? weight * 2 : weight;
        }
        return (sum, stems.Count, stems);
    }

    // Округление до 1e-9: равные по смыслу оценки не должны разойтись на ulp.
    private static double Round(double value) => Math.Round(value, 9);

    private static Bucket BucketOf(double score)
    {
        var value = Round(score);
        foreach (var bucket in Buckets)
            if (value >= bucket.Lo && value < bucket.Hi)
                return bucket;
        return Buckets[^1];
    }

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    // Стратифицированная выборка: по 20 пар из каждой корзины, порядок и зерно фиксированы.
    private static List<(Pair Pair, Bucket Bucket)> SamplePairs(IEnumerable<Pair> pairs)
    {
        var rnd = new Random(20261120);
        var result = new List<(Pair, Bucket)>();
        foreach (var bucket in Buckets)
        {
            var bucketPairs = pairs
                .Where(p => BucketOf(p.Score).Name == bucket.Name)
                .OrderBy(p => p.A, StringComparer.Ordinal)
                .ThenBy(p => p.B, StringComparer.Ordinal)
                .ToList();
            Shuffle(bucketPairs, rnd);
            result.AddRange(bucketPairs.Take(20).Select(p => (p, bucket)));
        }
        return result;
    }

    private static void Shuffle<T>(List<T> list, Random rnd)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // Пороги, при которых считается связность: из переменной окружения или по умолчанию.
    private static IReadOnlyList<double> ParseThresholds()
    {
        var raw = Environment.GetEnvironmentVariable("WIKI_TOPIC_THRESHOLDS");
        if (string.IsNullOrWhiteSpace(raw))
            return [3, 5, 6, 7, 8, 9, 10, 11];
        var list = new List<double>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                list.Add(value);
        return list.Count == 0 ? [3, 5, 6, 7, 8, 9, 10, 11] : list;
    }

    // Каталог артефактов: переменная окружения или <корень>/.cc-attachments/wiki-q2-topic.
    private static string OutputDir()
    {
        var explicitDir = Environment.GetEnvironmentVariable("WIKI_TOPIC_OUT");
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return explicitDir;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "backend")))
                return Path.Combine(dir.FullName, ".cc-attachments", "wiki-q2-topic");
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "wiki-q2-topic");
    }

    // Граф связности при пороге оценки: рёбра mentions/mentionedHere/backlinks (прежние группы)
    // и «По теме» (тот же Rank, что у продукта, с потолком 5). Ребро, у которого концы делят
    // карточку источника, не считается связью «тема ↔ тема».
    private sealed record Graph(
        Dictionary<string, HashSet<string>> Neighbours,
        Dictionary<string, HashSet<string>> TopicNeighbours,
        Dictionary<string, int> K0,
        int TopicEdges);

    private static Graph BuildGraph(List<Page> knowledge, Dictionary<string, TargetContext> contexts,
        Dictionary<string, HashSet<string>> sourceCards, double threshold)
    {
        var knowledgePaths = knowledge.Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var neighbours = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var k0 = new Dictionary<string, int>(StringComparer.Ordinal);
        var topicEdges = 0;
        var topicNeighbours = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Link(Dictionary<string, HashSet<string>> target, string a, string b)
        {
            if (!knowledgePaths.Contains(a) || !knowledgePaths.Contains(b) || a == b)
                return;
            if (sourceCards[a].Intersect(sourceCards[b]).Any())
                return;
            if (!target.TryGetValue(a, out var sa))
                target[a] = sa = new HashSet<string>(StringComparer.Ordinal);
            if (!target.TryGetValue(b, out var sb))
                target[b] = sb = new HashSet<string>(StringComparer.Ordinal);
            sa.Add(b);
            sb.Add(a);
        }

        foreach (var page in knowledge)
        {
            var ctx = contexts[page.Path];
            var legacy = ctx.Groups
                .Where(g => g.Kind is WikiRelated.KindMentions or WikiRelated.KindMentionedHere
                    or WikiRelated.KindBacklinks)
                .ToList();
            k0[page.Path] = legacy.Sum(g => g.Items.Count);
            foreach (var group in legacy)
                foreach (var item in group.Items)
                    Link(neighbours, page.Path, item.PageKey);
            var rank = WikiTopicScore.Rank(ctx.Index, page.Path, ctx.Pool,
                minScore: threshold, take: WikiRelated.TopicLimit);
            foreach (var (path, _) in rank)
            {
                topicEdges++;
                Link(topicNeighbours, page.Path, path);
                Link(neighbours, page.Path, path); // K1 (разрез) — объединение всех рёбер
            }
        }

        return new Graph(neighbours, topicNeighbours, k0, topicEdges);
    }

    // Итог по порогу и срезу: K0 (прежняя степень), K1 (покрытие «тема ↔ тема»), K2 (ссылка
    // в теле на страницу знаний). Крупнейшая компонента и доля изолированных — по всему графу.
    private static KMetrics Summarize(Graph graph, List<Page> knowledge, HashSet<string> nightKnowledge,
        double threshold, string sliceName, HashSet<string>? slice)
    {
        var pages = slice is null
            ? knowledge.Select(p => p.Path).ToList()
            : [.. slice];
        var count = pages.Count;
        var k0Linked = pages.Count(p => graph.K0.GetValueOrDefault(p) >= 1);
        var k0Avg = count == 0 ? 0 : pages.Sum(p => graph.K0.GetValueOrDefault(p)) / (double)count;
        var k1Linked = pages.Count(p => graph.Neighbours.TryGetValue(p, out var set) && set.Count > 0);
        var k1Topic = pages.Count(p => graph.TopicNeighbours.TryGetValue(p, out var topicSet) && topicSet.Count > 0);
        var k2 = K2Share(knowledge, slice);
        var (largest, isolated) = Components(graph, knowledge);
        return new KMetrics(
            threshold.ToString("0.##", CultureInfo.InvariantCulture), sliceName, count, graph.TopicEdges,
            count == 0 ? 0 : (double)k0Linked / count, k0Avg,
            count == 0 ? 0 : (double)k1Linked / count,
            count == 0 ? 0 : (double)k1Topic / count, k2, largest, isolated);
    }

    // Крупнейшая компонента и доля изолированных (без соседей) по графу знаний.
    private static (double Largest, double Isolated) Components(Graph graph, List<Page> knowledge)
    {
        var total = knowledge.Count;
        if (total == 0)
            return (0, 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var largest = 0;
        var isolated = 0;
        foreach (var page in knowledge)
        {
            if (!graph.Neighbours.TryGetValue(page.Path, out var set) || set.Count == 0)
            {
                isolated++;
                seen.Add(page.Path);
                continue;
            }
            if (!seen.Add(page.Path))
                continue;
            var stack = new Stack<string>();
            stack.Push(page.Path);
            var size = 0;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                size++;
                if (!graph.Neighbours.TryGetValue(current, out var adjacent))
                    continue;
                foreach (var next in adjacent)
                    if (seen.Add(next))
                        stack.Push(next);
            }
            if (size > largest)
                largest = size;
        }
        return ((double)largest / total, (double)isolated / total);
    }

    // K2 — доля страниц знаний с хотя бы одной резолвящейся ссылкой на страницу знаний
    // (не на карточку источника). Резолв — тот же, что у гейта: свой раздел, потом вверх.
    private static double K2Share(List<Page> knowledge, HashSet<string>? slice)
    {
        var map = new Dictionary<(string, string), Page>();
        foreach (var page in knowledge)
            map.TryAdd((page.Section!, WikiGate.Norm(page.Model!.Title)), page);
        var count = 0;
        var total = 0;
        foreach (var page in knowledge)
        {
            if (slice is not null && !slice.Contains(page.Path))
                continue;
            total++;
            if (HasKnowledgeLink(page, map))
                count++;
        }
        return total == 0 ? 0 : (double)count / total;
    }

    // Есть ли у страницы ссылка, резолвящаяся в страницу знаний.
    private static bool HasKnowledgeLink(Page page, Dictionary<(string, string), Page> map)
    {
        var chain = new List<string> { page.Section! };
        if (page.Section != WikiSections.Common)
            chain.Add(WikiSections.Common);
        foreach (var link in page.Model!.OutboundLinks())
        {
            var target = WikiPage.LinkTarget(link.Text);
            Page? hit = null;
            if (target.Contains('/'))
            {
                var section = WikiSections.Parse(target);
                if (section is null || !chain.Contains(section))
                    continue;
                hit = map.GetValueOrDefault((section, WikiGate.Norm(target.Split('/')[^1])));
            }
            else
            {
                foreach (var section in chain)
                {
                    hit = map.GetValueOrDefault((section, WikiGate.Norm(target)));
                    if (hit is not null)
                        break;
                }
            }
            if (hit is null || hit.Path == page.Path || !IsKnowledge(hit))
                continue;
            return true;
        }
        return false;
    }

    // Страницы, тронутые пакетами даты (create/append, применённые) — срез «ночь 19.11».
    private static HashSet<string> NightPages(string dir, string date)
    {
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var pkgDir = Path.Combine(dir, "keeper", "packages");
        if (!Directory.Exists(pkgDir))
            return touched;
        foreach (var file in Directory.EnumerateFiles(pkgDir, "*.json"))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("createdAt", out var createdAt))
                    continue;
                var createdAtText = createdAt.ValueKind == JsonValueKind.String ? createdAt.GetString() : null;
                if (createdAtText is null || !createdAtText.StartsWith(date, StringComparison.Ordinal))
                    continue;
                var status = root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
                    ? st.GetString()
                    : null;
                if (status is not ("applied" or "accepted" or "partially_accepted"))
                    continue;
                if (!root.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var op in ops.EnumerateArray())
                {
                    var opName = op.TryGetProperty("op", out var o) ? o.GetString() : null;
                    if (opName is not ("create" or "append"))
                        continue;
                    var decision = op.TryGetProperty("decision", out var d) ? d.GetString() : null;
                    var judge = op.TryGetProperty("judgeDecision", out var j) ? j.GetString() : null;
                    var human = op.TryGetProperty("humanDecision", out var h) ? h.GetString() : null;
                    if (decision != "auto" && judge != "accepted" && human != "accepted")
                        continue;
                    var page = op.TryGetProperty("page", out var pg) ? pg.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(page))
                        touched.Add(page);
                }
            }
        }
        return touched;
    }

    // Распределение пар по корзинам оценки.
    private static Dictionary<string, int> Histogram(IEnumerable<double> scores)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in Buckets)
            counts[bucket.Name] = 0;
        foreach (var score in scores)
        {
            var name = BucketOf(score).Name;
            counts[name] = counts.GetValueOrDefault(name) + 1;
        }
        return counts;
    }

    private static string Clean(string value) => value.Replace(';', ' ').Trim();

    // Все пары с оценками — по ним считается кривая точности.
    private static void WritePairs(string path, IEnumerable<Pair> pairs)
    {
        var lines = new List<string>
        {
            string.Join(";", "A", "B", "TitleA", "TitleB", "Section", "Score", "ScoreKnowOnly",
                "ScoreDf15", "ScoreDf35", "Rare", "Stems"),
        };
        foreach (var p in pairs.OrderBy(p => p.A, StringComparer.Ordinal).ThenBy(p => p.B, StringComparer.Ordinal))
            lines.Add(string.Join(";", p.A, p.B, Clean(p.TitleA), Clean(p.TitleB), p.Section,
                Fmt(p.Score), Fmt(p.ScoreKnowOnly), Fmt(p.ScoreDf15), Fmt(p.ScoreDf35), p.Rare, p.Stems));
        File.WriteAllLines(path, lines);
    }

    private static string Fmt(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);

    // Лист для слепой разметки: без оценки и без корзины — только две страницы.
    private static void WriteSample(string path, List<(Pair Pair, Bucket Bucket)> sample,
        List<Page> pages, Dictionary<string, Page> byPath)
    {
        var lines = new List<string>
        {
            string.Join(";", "Id", "PathA", "TitleA", "SummaryA", "PathB", "TitleB", "SummaryB"),
        };
        var id = 0;
        foreach (var (pair, _) in sample)
        {
            id++;
            lines.Add(string.Join(";", id.ToString(CultureInfo.InvariantCulture),
                pair.A, Clean(pair.TitleA), Clean(Summary(byPath[pair.A])),
                pair.B, Clean(pair.TitleB), Clean(Summary(byPath[pair.B]))));
        }
        File.WriteAllLines(path, lines);
    }

    // Оценки той же выборки — по ним разметка соединяется с кривой точности.
    private static void WriteSampleScores(string path, List<(Pair Pair, Bucket Bucket)> sample)
    {
        var lines = new List<string>
        {
            string.Join(";", "Id", "Bucket", "Score", "ScoreKnowOnly", "ScoreDf15", "ScoreDf35", "Rare", "Stems"),
        };
        var id = 0;
        foreach (var (pair, bucket) in sample)
        {
            id++;
            lines.Add(string.Join(";", id.ToString(CultureInfo.InvariantCulture), bucket.Name,
                Fmt(pair.Score), Fmt(pair.ScoreKnowOnly), Fmt(pair.ScoreDf15), Fmt(pair.ScoreDf35),
                pair.Rare, pair.Stems));
        }
        File.WriteAllLines(path, lines);
    }

    private static string Summary(Page page)
    {
        var text = WikiProjections.SummaryOf(page.Model!.Frontmatter, page.Text) ?? "";
        text = text.Replace(';', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 300 ? text[..300] : text;
    }
}
