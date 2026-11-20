namespace ClaudeHomeServer.Services.Notes.Wiki;

// «Связанные» (2Б очереди 1): чистая проекция при показе страницы — что относится к ней по
// её же содержанию, а не по отдельно хранимым связям. Вход: страница и снимок страниц её
// раздела плюс цепочки вверх — то же множество, что `allowed` у гейта (WikiGate.CheckLinks,
// WikiGate.cs:332): его собирает вызывающий (Q1-S6b), проекция диска ни читает, ни пишет.
//
// Группы по порядку приоритета (дубль между группами остаётся только в старшей):
// source — общая карточка источника в сносках; chronicle — хроника того чата, что является
// источником этих карточек; mentions — упоминают эту страницу; mentionedHere — упомянуты
// здесь; backlinks — OutboundLinks (WikiPage.cs:258) указывает сюда; topic — «По теме»:
// страницы видимых разделов с редкими общими основами (WikiTopicScore), последняя группа —
// в неё попадает только то, чего нет в старших.
//
// Карточки source и проекции `_*` из групп упоминаний и обратных ссылок исключены: источник
// имеет свою группу, проекцию генерирует код. Пустые группы не отдаются; группа — не длиннее
// GroupLimit. Порядок группы и порядок групп детерминированы: страница по Ordinal, группы по
// приоритету — один и тот же снимок даёт один и тот же ответ при любом порядке входа.
//
// Видимость — та же, что у гейта (ADR-036, решение 4): свой раздел плюс цепочка вверх. Цепочку
// здесь целиком вывести нельзя (сфера — живьём через ISphereDirectory, WikiSections.UpChain):
// common — верх любой цепочки, кроме своей, он видим всегда; сфера видима только странице
// проекта и только та, которую вызывающий включил в снимок, — настоящий цепочку знает он,
// всё прочее в снимке — боковое и выпадает.
public static class WikiRelated
{
    /// <summary>Потолок группы: «Связанные» — блок страницы, а не каталог.</summary>
    public const int GroupLimit = 8;

    public const string KindSource = "source";
    public const string KindChronicle = "chronicle";
    public const string KindMentions = "mentions";
    public const string KindMentionedHere = "mentionedHere";
    public const string KindBacklinks = "backlinks";
    public const string KindTopic = "topic";

    /// <summary>Потолок «По теме»: действует и при limit: null — группа подсказка, а не степень.</summary>
    public const int TopicLimit = 5;

    // Порядок приоритета: дубль остаётся только в первой допустившей его группе.
    private static readonly string[] KindOrder =
        [KindSource, KindChronicle, KindMentions, KindMentionedHere, KindBacklinks, KindTopic];

    /// <summary>Проекция «Связанные» для одной страницы. page — сама страница (путь под pages/
    /// и текст); pages — снимок страниц её раздела и цепочки вверх (тот же набор, что state
    /// гейта для allowed-разделов): своя страница, неразобранный путь, проекция и неразобранная
    /// страница из него выпадают — та же мера, что у гейта (WikiGate.cs:101-137).
    /// limit — потолок группы: по умолчанию прежний GroupLimit (вызовы без него ведут себя как
    /// раньше); null — без потолка (A5: степени WikiConnectivity не должны срезаться —
    /// страница с 10 обратными ссылками даёт степень 10, а не 8).</summary>
    public static IReadOnlyList<WikiRelatedGroup> Build(WikiGate.Draft page, IReadOnlyList<WikiGate.Draft> pages,
        int? limit = GroupLimit) =>
        Build(page, pages, limit, null);

    /// <summary>Тестовый срез (c3 вердикта 141df4ac): сколько вызовов WikiGate.Norm делает
    /// полный Build на этом снимке — сторож O(N+L) резолва вместо прежнего O(N·L·N).</summary>
    internal static int CountNormCalls(WikiGate.Draft page, IReadOnlyList<WikiGate.Draft> pages)
    {
        var counter = new NormCounter();
        Build(page, pages, GroupLimit, counter);
        return counter.Calls;
    }

    /// <summary>Тестовый срез: сколько вызовов WikiCandidates.SignificantWords делает полный
    /// Build — индекс «По теме» строится один раз на Build, а не на каждую пару.</summary>
    internal static int CountWordsCalls(WikiGate.Draft page, IReadOnlyList<WikiGate.Draft> pages)
    {
        var counter = new NormCounter();
        Build(page, pages, GroupLimit, counter);
        return counter.WordsCalls;
    }

    private static IReadOnlyList<WikiRelatedGroup> Build(WikiGate.Draft page, IReadOnlyList<WikiGate.Draft> pages,
        int? limit, NormCounter? counter)
    {
        var targetSection = WikiSections.Parse(page.Path);
        if (targetSection is null || WikiPaths.IsProjection(page.Path))
            return [];
        var target = WikiPage.Parse(page.Content).Model;
        if (target is null)
            return [];

        var candidates = new List<Cand>();
        foreach (var draft in pages)
        {
            if (draft.Path == page.Path)
                continue; // своя страница — не «связанная»
            var section = WikiSections.Parse(draft.Path);
            if (section is null || WikiPaths.IsProjection(draft.Path))
                continue;
            var model = WikiPage.Parse(draft.Content).Model;
            if (model is null)
                continue; // неразобранное только мешает (та же мера, что у чужого раздела гейта)
            candidates.Add(new Cand(draft.Path, section, draft.Content, model));
        }

        var allowed = AllowedSections(targetSection, candidates);
        // Словарь резолва (раздел, нормализованный заголовок) → кандидат: строится один раз до
        // циклов — резолв ссылки больше не нормализует заголовок каждого кандидата на каждую
        // ссылу (O(N·L·N) → O(N+L), пункт 2 вердикта). Пространство — снимок вместе с целью:
        // ссылка кандидата должна опознавать и саму страницу (иначе «указывает сюда» не
        // резолвилась бы никогда). Первое вхождение заголовка остаётся — тот же порядок, что
        // у прежнего FirstOrDefault по снимку. В группы цель не входит: обход групп идёт по
        // candidates, цель в них не попадает.
        var index = BuildIndex([.. candidates, new Cand(page.Path, targetSection, page.Content, target)], counter);
        var groups = new Dictionary<string, List<Cand>>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);

        // 1. source — общая карточка источника в сносках страницы. Сноска ведёт только к карточке —
        // та же мера, что у гейта («сноска не на карточку» — нарушение, WikiGate.cs:256).
        var cited = new List<Cand>();
        foreach (var footnote in target.Footnotes)
        {
            if (!footnote.Target.IsLink)
                continue;
            var card = ResolveLink(footnote.Target.Text, targetSection, allowed, index, counter);
            if (card is null || card.Path == page.Path || card.Page.Frontmatter.Type != WikiFrontmatter.TypeSource)
                continue; // сноска на себя — не «связанный» источник
            if (cited.Any(c => c.Path == card.Path))
                continue; // одна карточка в нескольких сносках — один элемент
            cited.Add(card);
            Add(groups, taken, KindSource, card);
        }

        // 2. chronicle — хроника того же чата, что является источником этих карточек: ref
        // карточки origin: session несёт id чата (session:{id}#m…), а путь её хроники — та же
        // формула, которой разбор её пишет (WikiChatIngestService.ChroniclePath): сегмент
        // «Хроники» и файл «{id}.md». Хроники чата в снимке нет (не-проектный дом, откат) —
        // группы нет: молчание о несуществующем, не догадка о пути.
        foreach (var card in cited)
        {
            if (card.Page.Frontmatter.Origin != WikiGate.OriginSession ||
                !WikiPage.TryParseSessionRef(card.Page.Frontmatter.Ref ?? "", out var sessionId, out _, out _))
                continue;
            foreach (var c in candidates)
            {
                if (!allowed.Contains(c.Section))
                    continue; // хроника бокового раздела — не видима (тот же allowed, что у
                             // остальных групп): карточка из «Общего» не тянет хронику чужого проекта
                if (c.Page.Frontmatter.Type != WikiFrontmatter.TypeChronicle)
                    continue;
                var segments = c.Path.Split('/');
                if (segments.Contains(WikiChatIngestService.ChronicleDirName, StringComparer.Ordinal) &&
                    segments[^1] == sessionId + ".md")
                    Add(groups, taken, KindChronicle, c);
            }
        }

        // 3. mentions — упоминают эту страницу: заголовок цели в тексте кандидата словом вне
        // кода и ни одной ссылки на неё.
        foreach (var c in Navigable(candidates, allowed))
        {
            var lower = c.Content.ToLowerInvariant();
            if (!IsMention(lower, NotesService.CodeMask(lower), target.Title, counter))
                continue;
            if (LinksTo(c.Page, c.Section, page.Path, allowed, index, counter))
                continue; // ссылка есть — это backlink, не упоминание
            Add(groups, taken, KindMentions, c);
        }

        // 4. mentionedHere — упомянуты здесь: заголовок кандидата в тексте цели словом вне
        // кода и ни одной ссылки цели на него.
        var targetLower = page.Content.ToLowerInvariant();
        var targetMask = NotesService.CodeMask(targetLower);
        foreach (var c in Navigable(candidates, allowed))
        {
            if (!IsMention(targetLower, targetMask, c.Page.Title, counter))
                continue;
            if (LinksTo(target, targetSection, c.Path, allowed, index, counter))
                continue; // ссылка есть — это не «упомянуто без ссылки»
            Add(groups, taken, KindMentionedHere, c);
        }

        // 5. backlinks — те, чьи OutboundLinks указывают сюда.
        foreach (var c in Navigable(candidates, allowed))
        {
            if (!LinksTo(c.Page, c.Section, page.Path, allowed, index, counter))
                continue;
            Add(groups, taken, KindBacklinks, c);
        }

        // 6. topic — по редким общим основам. Индекс (df) — по всему снимку видимых разделов
        // вместе с целью, один раз на Build. Кандидаты — как у остальных групп, но без хроник и
        // без уже занятых старшими группами; порядок — по оценке, он же и остаётся в выводе.
        var snapshot = candidates.Where(c => allowed.Contains(c.Section))
            .Append(new Cand(page.Path, targetSection, page.Content, target));
        var topicIndex = WikiTopicScore.BuildIndex(
            snapshot.Select(c => new WikiTopicScore.Entry(c.Path, c.Page.Title,
                WikiProjections.SummaryOf(c.Page.Frontmatter, c.Content))),
            counter is null ? null : counter.CountWords);
        var byPath = candidates.ToDictionary(c => c.Path, StringComparer.Ordinal);
        var topicPool = Navigable(candidates, allowed)
            .Where(c => c.Page.Frontmatter.Type != WikiFrontmatter.TypeChronicle && !taken.Contains(c.Path))
            .Select(c => c.Path);
        foreach (var (path, _) in WikiTopicScore.Rank(topicIndex, page.Path, topicPool, take: TopicLimit))
            Add(groups, taken, KindTopic, byPath[path]);

        return [.. KindOrder
            .Where(kind => groups.TryGetValue(kind, out var g) && g.Count > 0)
            .Select(kind => new WikiRelatedGroup(kind, [.. (kind == KindTopic
                    ? groups[kind] // порядок «По теме» — оценка, а не путь
                    : groups[kind].OrderBy(c => c.Path, StringComparer.Ordinal).ToList())
                .Take(limit ?? int.MaxValue) // null — без потолка (A5); срез — только вывод группы
                .Select(c => new WikiRelatedItem(c.Path, c.Page.Title, c.Page.Frontmatter.Type))]))];
    }

    /// <summary>Общий матчер упоминаний: вынесен из WikiLint.UnlinkedMentions (тот же смысл,
    /// что у маски «вне кода» после фикса 7928ce94): заголовок после нормализации встречается
    /// в тексте как отдельное слово вне кода. lower-текст и маску страница-носитель считает
    /// один раз на страницу — их даёт вызывающий; порог длины (3) и нормализация здесь.
    /// Проверка «ссылки нет» — за вызывающим: lint знает рёбра снимка, проекция — разрешённые
    /// [[…]]. Тот же матчер зовёт и метрика UnlinkedMentionPairs (WikiLint.Metrics).</summary>
    internal static bool IsMention(string lowerText, bool[] codeMask, string title, NormCounter? counter = null)
    {
        var normalized = Norm(title, counter);
        return normalized.Length >= 3 && NotesService.FindWord(lowerText, normalized, codeMask) >= 0;
    }

    private sealed record Cand(string Path, string Section, string Content, WikiPage Page);

    // Счётчик нормализаций для тестового среза CountNormCalls; в обычном пути Build(null).
    internal sealed class NormCounter
    {
        public int Calls;
        public int WordsCalls;

        public HashSet<string> CountWords(string text)
        {
            WordsCalls++;
            return WikiCandidates.SignificantWords(text);
        }
    }

    private static string Norm(string text, NormCounter? counter)
    {
        if (counter is not null)
            counter.Calls++;
        return WikiGate.Norm(text);
    }

    // Словарь резолва: (раздел, нормализованный заголовок) → кандидат. Строится один раз до
    // циклов (O(N) нормализаций); первое вхождение заголовка остаётся — тот же выбор, что у
    // прежнего FirstOrDefault по снимку.
    private static Dictionary<(string Section, string Title), Cand> BuildIndex(List<Cand> searchable, NormCounter? counter)
    {
        var index = new Dictionary<(string, string), Cand>();
        foreach (var c in searchable)
            index.TryAdd((c.Section, Norm(c.Page.Title, counter)), c);
        return index;
    }

    private static void Add(Dictionary<string, List<Cand>> groups, HashSet<string> taken, string kind, Cand c)
    {
        if (!taken.Add(c.Path))
            return; // дубль между группами — остаётся только в старшей (порядок обхода — приоритет)
        if (!groups.TryGetValue(kind, out var list))
            groups[kind] = list = [];
        list.Add(c);
    }

    // Кандидаты групп упоминаний и обратных ссылок: страницы видимых разделов; карточки
    // источника — вне (у источника своя группа), проекции — вне (сняты при сборе).
    private static IEnumerable<Cand> Navigable(List<Cand> candidates, HashSet<string> allowed) =>
        candidates.Where(c => allowed.Contains(c.Section) &&
            c.Page.Frontmatter.Type != WikiFrontmatter.TypeSource);

    // Видимость раздела: свой + common (верх любой цепочки, кроме своей) + сфера, только для
    // страницы проекта и только та, что есть в снимке (её туда положил знающий цепочку
    // вызывающий). Та же область, что `allowed` у гейта (WikiGate.cs:332).
    private static HashSet<string> AllowedSections(string section, List<Cand> candidates)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { section };
        if (section != WikiSections.Common)
            allowed.Add(WikiSections.Common);
        if (WikiSections.TryProjectId(section, out _))
            foreach (var c in candidates)
                if (WikiSections.TrySphereId(c.Section, out _))
                    allowed.Add(c.Section);
        return allowed;
    }

    // Порядок резолва голой ссылки: свой раздел, потом вверх (сфера — только проекта и только
    // из снимка; common — верх любой цепочки). Та же очерёдность, что у FindPage гейта
    // (свой, потом upChain = [сфера, common], WikiGate.cs:353-356).
    private static IEnumerable<string> ChainOf(string fromSection, HashSet<string> allowed)
    {
        yield return fromSection;
        if (fromSection == WikiSections.Common)
            yield break;
        if (WikiSections.TryProjectId(fromSection, out _))
            foreach (var s in allowed.Where(s => WikiSections.TrySphereId(s, out _)).OrderBy(s => s, StringComparer.Ordinal))
                yield return s;
        yield return WikiSections.Common;
    }

    // Резолв цели ссылки — тот же, что у гейта (WikiGate.cs:336-362): голая цель — свой раздел,
    // потом вверх; цель с префиксом — этот раздел, последний сегмент — заголовок страницы.
    private static Cand? ResolveLink(string text, string fromSection, HashSet<string> allowed,
        Dictionary<(string Section, string Title), Cand> index, NormCounter? counter)
    {
        if (text.Contains('/'))
        {
            var section = WikiSections.Parse(text);
            if (section is null || !allowed.Contains(section))
                return null;
            return index.TryGetValue((section, Norm(text.Split('/')[^1], counter)), out var prefixed) ? prefixed : null;
        }
        var bare = Norm(text, counter); // одна нормализация цели на ссылу, не на раздел цепочки
        foreach (var section in ChainOf(fromSection, allowed))
        {
            if (index.TryGetValue((section, bare), out var hit))
                return hit;
        }
        return null;
    }

    // Указывает ли страница на цель: хоть одна OutboundLink резолвится в неё. Направление —
    // аргументы: «кто на кого».
    private static bool LinksTo(WikiPage fromPage, string fromSection, string toPath,
        HashSet<string> allowed, Dictionary<(string Section, string Title), Cand> index, NormCounter? counter) =>
        fromPage.OutboundLinks().Any(link => ResolveLink(link.Text, fromSection, allowed, index, counter)?.Path == toPath);
}

/// <summary>Одна группа «Связанных» (контракт Q1-S7): kind — имя группы (KindSource…KindBacklinks),
/// items — страницы по Ordinal пути, не больше GroupLimit.</summary>
public sealed record WikiRelatedGroup(string Kind, IReadOnlyList<WikiRelatedItem> Items);

/// <summary>Один элемент группы: pageKey — путь под pages/, title — заголовок страницы,
/// type — значение type её шапки (для карточек источника — «source»).</summary>
public sealed record WikiRelatedItem(string PageKey, string Title, string? Type);
