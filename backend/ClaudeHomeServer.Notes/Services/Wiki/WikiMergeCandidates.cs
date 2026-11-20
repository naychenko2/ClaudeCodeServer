namespace ClaudeHomeServer.Services.Notes.Wiki;

// C4 (очередь 2, волна C): подбор пар merge — кто кого сливает. Чистая функция: диска не читает,
// ничего не пишет. Вход — снимок страниц (тот же WikiGate.Draft, что у WikiRelated), выход — список
// (A, B) путей страниц; в пакете op=merge это становится Page=A, Target=B (C6).
//
// Две ветки, обе — только внутри одного раздела (цепочка вверх не берётся: merge живёт в разделе,
// где лежат обе страницы, — та же граница, что у применения, WikiApplyService.MergeInto:976):
//  • «дубль» — две страницы одного раздела с вложением основ заголовков
//    (WikiTopicScore.TitleSubset, C1 — готовый предикат, своей токенизации здесь нет);
//    направление: A — страница с меньшим числом утверждений, при равенстве — чей путь больше
//    по Ordinal. Ложную пару отклонит судья (C5).
//  • «мелкая страница» — короткая страница (сводка короче порога и не больше одного утверждения,
//    тип не state/source/chronicle) сливается в ближайшую по «По теме» (WikiTopicScore.Score,
//    порог MinScore). Мера сходства одна, второго скорера нет (перепостановка 20.11).
//
// Одна пара на A: страница, ставшая A, второй раз A не становится; если A подходит под обе ветки,
// приоритет у «дубля». Предусловия A (авторство по сноскам — WikiProjections.Authorship, без
// «Заметок») и открытые пакеты (A и B) проверяются здесь для подбора; применение проверяет
// их заново (C2). Признак adopt живёт в журнале (MergeInto.IsAdopted), а журнал чистая функция
// не читает: такие страницы вызывающий кладёт в openPackagePaths/quarantinedPaths.
// Порядок детерминирован: раздел по Ordinal, внутри — пары по Ordinal пути A; он не зависит
// от порядка входа.
public static class WikiMergeCandidates
{
    /// <summary>Потолок пар за вызов: подбор — заявка на ночь, не каталог.</summary>
    public const int MaxTotal = 10;

    /// <summary>Потолок пар одного раздела: раздел чинится постепенно.</summary>
    public const int MaxPerSection = 3;

    // «Мелкая» страница: сводка (та же проекция, что у строки каталога — WikiProjections.SummaryOf)
    // короче этой длины и не больше одного утверждения. Короткой нечего держать отдельно:
    // её содержание принадлежит главной странице предмета.
    private const int SmallSummaryMaxChars = 400;
    private const int SmallMaxStatements = 1;

    // Секция перенесённых утверждений: их автор — прежняя строка, действующим утверждением
    // страницы они не считаются (тот же смысл, что у HistorySectionName в WikiDedup/WikiRetract).
    private const string HistorySectionName = "История";

    /// <summary>Пары merge (A, B): A сливается в B. openPackagePaths — страницы любого открытого
    /// пакета (ни A, ни B — их состояние меняет другой пакет); quarantinedPaths — страницы в
    /// карантине (только для A: B может быть и карантинной — она остаётся). maxTotal/maxPerSection
    /// — потолки вывода. Порядок: раздел по Ordinal, внутри — пары по Ordinal пути A.</summary>
    public static List<(string A, string B)> Pick(
        IReadOnlyList<WikiGate.Draft> pages,
        IReadOnlySet<string> openPackagePaths,
        IReadOnlySet<string> quarantinedPaths,
        int maxTotal = MaxTotal,
        int maxPerSection = MaxPerSection)
    {
        if (maxTotal <= 0 || maxPerSection <= 0)
            return [];

        var candidates = new List<Cand>();
        foreach (var d in pages)
        {
            if (WikiPaths.IsProjection(d.Path))
                continue; // проекции — не страницы
            if (WikiSections.Parse(d.Path) is not { } section)
                continue; // путь вне разделов — не кандидат (тот же fail-closed, что у видимости)
            if (WikiPage.Parse(d.Content).Model is not { } model)
                continue; // неразобранная только мешает
            candidates.Add(new Cand(d.Path, section, d.Content, model));
        }

        // Авторство — та же мера, что у проекций: по сноскам действующих утверждений
        // (WikiProjections.Authorship). Признак берётся для всех страниц снимка; проверяется
        // только у A — B остаётся как есть и авторства своего не меняет.
        var authorship = WikiProjections.Authorship(pages);

        // Индекс «По теме» (df) строится один раз на вызов — по всему снимку разобранных
        // страниц (та же область индекса, что у topic-группы WikiRelated).
        var index = WikiTopicScore.BuildIndex(candidates
            .Select(c => new WikiTopicScore.Entry(c.Path, c.Model.Title,
                WikiProjections.SummaryOf(c.Model.Frontmatter, c.Content))));

        var bySection = new Dictionary<string, List<Cand>>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (!bySection.TryGetValue(c.Section, out var list))
                bySection[c.Section] = list = [];
            list.Add(c);
        }

        var result = new List<(string A, string B)>();
        foreach (var section in bySection.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            var group = bySection[section].OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
            var usedA = new HashSet<string>(StringComparer.Ordinal);
            var pairs = new List<(string A, string B)>();

            // 1. «Дубль»: вложение основ заголовков. Пара unordered; направление считается по
            // числу утверждений. Сначала «дубль» — он забирает A раньше, чем «мелкая» ветка.
            foreach (var x in Enumerable.Range(0, group.Count))
            foreach (var y in Enumerable.Range(x + 1, group.Count - x - 1))
            {
                var p = group[x];
                var q = group[y];
                if (!WikiTopicScore.TitleSubset(p.Model.Title, q.Model.Title))
                    continue;
                var (a, b) = Direction(p, q);
                if (usedA.Contains(a.Path) || !CanMergeInto(a, openPackagePaths, quarantinedPaths, authorship))
                    continue;
                if (openPackagePaths.Contains(b.Path))
                    continue;
                usedA.Add(a.Path);
                pairs.Add((a.Path, b.Path));
            }

            // 2. «Мелкая страница»: A — короткая и не state/source/chronicle; B — ближайшая
            // по оценке в том же разделе (Rank: по убыванию оценки, при равенстве — Ordinal пути).
            var pool = group.Where(c => !openPackagePaths.Contains(c.Path))
                .Select(c => c.Path); // B — любая не-открытая страница раздела, включая уже взятую как A
            foreach (var a in group)
            {
                if (usedA.Contains(a.Path) || !CanMergeInto(a, openPackagePaths, quarantinedPaths, authorship))
                    continue;
                if (!IsSmall(a))
                    continue;
                var best = WikiTopicScore.Rank(index, a.Path, pool.Where(p => p != a.Path)).FirstOrDefault();
                if (best.Path is null)
                    continue;
                usedA.Add(a.Path);
                pairs.Add((a.Path, best.Path));
            }

            result.AddRange(pairs.OrderBy(p => p.A, StringComparer.Ordinal).Take(maxPerSection));
            if (result.Count >= maxTotal)
                break;
        }

        return [.. result.Take(maxTotal)];
    }

    private sealed record Cand(string Path, string Section, string Content, WikiPage Model);

    // Предусловия A: не в открытом пакете, не в карантине, авторства ai (по сноскам действующих
    // утверждений — ни одной сноски на карточку origin: human), без секции «Заметки» человека.
    private static bool CanMergeInto(Cand a, IReadOnlySet<string> open, IReadOnlySet<string> quarantined,
        IReadOnlyDictionary<string, string> authorship) =>
        !open.Contains(a.Path) &&
        !quarantined.Contains(a.Path) &&
        a.Model.Notes is null &&
        authorship.TryGetValue(a.Path, out var author) && author == WikiProjections.AuthorshipAi;

    // «Мелкая» страница: тип не state/source/chronicle (состояние, карточка данных и хроника
    // живут своими правилами), сводка короче порога (её отсутствие — длина 0), утверждений
    // не больше одного.
    private static bool IsSmall(Cand a)
    {
        var t = a.Model.Frontmatter.Type;
        if (t is WikiFrontmatter.TypeState or WikiFrontmatter.TypeSource or WikiFrontmatter.TypeChronicle)
            return false;
        var summary = WikiProjections.SummaryOf(a.Model.Frontmatter, a.Content);
        if (summary is { Length: >= SmallSummaryMaxChars })
            return false;
        return CountStatements(a.Model) <= SmallMaxStatements;
    }

    // Направление пары «дубля»: A — с меньшим числом утверждений; при равенстве — чей путь
    // больше по Ordinal. B — вторая страницы пары.
    private static (Cand A, Cand B) Direction(Cand p, Cand q)
    {
        var (cp, cq) = (CountStatements(p.Model), CountStatements(q.Model));
        if (cp < cq)
            return (p, q);
        if (cq < cp)
            return (q, p);
        return string.CompareOrdinal(p.Path, q.Path) >= 0 ? (p, q) : (q, p);
    }

    // Число утверждений страницы: строки секций утверждений без «Истории» (тот же смысл
    // действующих утверждений, что у журнала: WikiJournalReader.ActiveAssertionLines;
    // пустых строк в модели нет — WikiPage.Parse их не несёт).
    private static int CountStatements(WikiPage page) =>
        page.Sections
            .Where(s => s.Name != HistorySectionName && WikiPage.AssertionSectionNames.Contains(s.Name))
            .Sum(s => s.Lines.Count);
}
