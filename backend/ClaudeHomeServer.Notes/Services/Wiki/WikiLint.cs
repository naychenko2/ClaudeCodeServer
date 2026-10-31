using System.Text;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Механический lint раздела (ADR-035, решение 2 «Обнаружение дрейфа» и «Последствия»: базовая
// линия метрик; ADR-036, решения 1 «сироты», 4 «out_of_reach», 5 «предложение повышения»).
// Чистая функция: ни диска, ни модели, ни DateTime.UtcNow — время приходит параметром.
//
// Сигнатура — снимок + раздел + статус манифеста + время + контекст вызывающего. Пятый параметр
// нужен потому, что снимок графа этих данных не несёт: в нём нет шапки страниц (owns/updated —
// см. WikiFrontmatter), нет текстов (упоминания без ссылки) и нет текущей сферы
// проекта (UpChain требует ISphereDirectory — WikiSections.cs:78). Context — только данные,
// которые собрал вызывающий (он читает диск и разбирает шапки): внутри линтера I/O нет.
//
// Источники страницы — цели её сносок (WikiPageFacts.Footnotes), а не поле шапки sources: в
// WikiFrontmatter его заполняет только Parse (WikiFrontmatter.cs:189), а ни один писатель
// страниц не задаёт — и находка на таком поле была бы ложной, как и «без изменений» на пустом
// updated. Дату вызывающий берёт из шапки, а когда
// её нет — из mtime файла: дата с диска — факт вызывающего, линтер диска не читает
// (решение по ревью S3-2).
//
// Область находок — страницы вики заданного раздела (ADR-035:325 — строки идут в _health.md
// этого раздела): человеческие заметки не дают ни одной находки, но входят в метрики, которые
// считаются по всей базе и сравниваются с базовой линией S0b-2. Одно исключение по области:
// уникальность owns — по объединению раздела и его цепочки вверх (ADR-036 §4).
//
// Аннотации и узлы-комментарии (frontmatter annotates) линтер НЕ учитывает: синтезируемых
// рёбер «комментарий → документ» в снимке нет вовсе — их строит только публичный GetGraph
// (includeAnnotations), NotesService.cs:729-752, — поэтому по снимку такой узел выглядел бы
// сиротой, которой он не является (решение Софьи M3 по ревью S3-1). Какие узлы исключить,
// знает вызывающий: он видит шапку — Context.AnnotationNodeIds.
//
// Призраки — ВСЕ узлы-призраки снимка: публичный граф отсекает их по степени > 0
// (NotesService.cs:771-773), метрике базовой линии нужен полный счёт (решение Софьи M4).
//
// Недоверенный ввод (заголовки страниц, имена призраков, значения owns) в Message не
// подставляется: он остаётся полями данных (Related/Value), строку для _health.md рендерит
// вызывающий — он же её экранирует.
// internal: вход линтера — внутренний снимок графа (NotesGraphSnapshot), тот же шов
// вертикали, что GetGraphSnapshot. Наружу из вертикали выходит отчёт (публичные типы ниже),
// а не вызов.
internal static class WikiLint
{
    /// <summary>Сколько ссылок из раздела на несуществующую цель делают её находкой. Рёбра
    /// снимка продекуплированы в пределах страницы (NotesService.cs:482), поэтому «ссылка» =
    /// страница, а не вхождение в тексте.</summary>
    public const int GhostMinRefs = 3;

    /// <summary>«Почти дубли» — расстояние Левенштейна по нормализованным заголовкам.</summary>
    public const int NearDuplicateMaxDistance = 2;

    /// <summary>«Без изменений» — с этого возраста страницы. Полуприкрытый интервал: ровно
    /// 90 дней — уже находка, 89 — ещё нет.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(90);

    /// <summary>Отчёт по разделу: находки — только по его страницам, метрики — по всей базе.
    /// now — единственное время функции.</summary>
    public static WikiLintReport Run(
        NotesGraphSnapshot snapshot,
        string section,
        WikiManifestReadResult manifestStatus,
        DateTimeOffset now,
        WikiLintContext context)
    {
        var analysis = new Analysis(snapshot, context);
        return new WikiLintReport(
            section,
            now,
            analysis.FindingsFor(section, manifestStatus, now),
            analysis.Metrics());
    }

    /// <summary>Тот же ключ id, что у NotesService.EncodeId (NotesService.cs:1474): base64url
    /// без «=», «+»→«-», «/»→«_». Живёт здесь, а не переиспользуется: тот метод private, а
    /// контракт линтера — id узла из снимка. «ghost:…» и «doc:…» — не id заметки: null.</summary>
    internal static (string SourceKey, string RelPath)? DecodeNodeId(string id)
    {
        var b64 = id.Replace('-', '+').Replace('_', '/');
        switch (b64.Length % 4)
        {
            case 2: b64 += "=="; break;
            case 3: b64 += "="; break;
        }
        string raw;
        try { raw = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
        catch (FormatException) { return null; }
        var sep = raw.IndexOf('|');
        if (sep < 0) return null;
        return (raw[..sep], raw[(sep + 1)..]);
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    // Дневники не считаются сиротами — та же поправка базовой линии («сироты без Journal/»).
    private static bool IsJournal(string relPath) => relPath.StartsWith("Journal/", StringComparison.Ordinal);

    private static void Add<T>(Dictionary<string, List<T>> map, string key, T value)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        list.Add(value);
    }

    private static List<string> Distinct(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();

    private static double Share(int part, int total) => total == 0 ? 0 : (double)part / total;

    // ─── Индекс снимка: один обход, дальше только чтение ─────────────────────────

    private sealed class Analysis
    {
        private readonly NotesGraphSnapshot _snapshot;
        private readonly WikiLintContext _context;
        private readonly HashSet<string> _annotations;

        private readonly Dictionary<string, NodeInfo> _nodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ghostIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _ghostNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _outgoing = new(StringComparer.Ordinal);
        private readonly HashSet<string> _incoming = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<PageRef>> _bySection = new(StringComparer.Ordinal);
        private readonly Dictionary<string, WikiPageFacts> _facts = new(StringComparer.Ordinal);
        private readonly List<NodeInfo> _baseNotes = [];
        private readonly List<NodeInfo> _titleIndex = [];

        public Analysis(NotesGraphSnapshot snapshot, WikiLintContext context)
        {
            _snapshot = snapshot;
            _context = context;
            _annotations = new HashSet<string>(context.AnnotationNodeIds, StringComparer.Ordinal);

            foreach (var (id, name) in snapshot.Ghosts)
            {
                _ghostIds.Add(id);
                _ghostNames[id] = name;
            }

            foreach (var node in snapshot.Nodes)
            {
                if (node.Ghost)
                {
                    _ghostIds.Add(node.Id);
                    _ghostNames[node.Id] = node.Title;
                    continue;
                }
                var decoded = DecodeNodeId(node.Id);
                if (decoded is null)
                    continue;
                var (sourceKey, relPath) = decoded.Value;
                // Узел вики без разбираемого пути — не страница ни одного раздела (fail-closed
                // ADR-036, решение 2). Проекции раздела (_index/_log/_health) — не страницы:
                // тот же предикат, что у графа заметок и датасета Dify.
                var section = sourceKey == NotesService.WikiKey && !WikiPaths.IsProjection(relPath)
                    ? WikiSections.Parse(relPath)
                    : null;
                var info = new NodeInfo(node.Id, sourceKey, relPath, node.Title, section);
                _nodes[node.Id] = info;
                // Узел-аннотация — не заметка базовой линии («заметка | узел модели без
                // frontmatter annotates»), не сирота и не участник метрик (M3).
                if (_annotations.Contains(node.Id))
                    continue;
                _baseNotes.Add(info);
                _titleIndex.Add(info);
                if (section is not null)
                    Add(_bySection, section, new PageRef(node.Id, relPath, node.Title));
            }

            foreach (var pages in _bySection.Values)
                pages.Sort((a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));

            foreach (var edge in snapshot.Edges)
            {
                Add(_outgoing, edge.Source, edge.Target);
                _incoming.Add(edge.Target);
            }

            foreach (var fact in context.Pages)
                _facts[fact.RelPath] = fact;
        }

        private readonly record struct NodeInfo(string Id, string SourceKey, string RelPath, string Title, string? Section);

        private readonly record struct PageRef(string Id, string RelPath, string Title);

        private List<string> Outgoing(string id) =>
            _outgoing.TryGetValue(id, out var targets) ? targets : [];

        private string? NameOf(string targetId) =>
            _ghostNames.TryGetValue(targetId, out var name) ? name : targetId;

        // Страницы раздела: узел графа + его факты. Фактов нет — шапку и текст страницы линтер
        // не видит, находок по ней не строит (молчание о ней — цена неполного контекста,
        // отмечена в комментарии класса).
        private List<(PageRef Node, WikiPageFacts Facts)> SectionPages(string section)
        {
            var list = new List<(PageRef, WikiPageFacts)>();
            if (!_bySection.TryGetValue(section, out var refs))
                return list;
            foreach (var r in refs)
                if (_facts.TryGetValue(r.RelPath, out var f))
                    list.Add((r, f));
            return list;
        }

        // ─── Находки раздела ─────────────────────────────────────────────────────

        public List<WikiFinding> FindingsFor(string section, WikiManifestReadResult manifest, DateTimeOffset now)
        {
            var findings = new List<WikiFinding>();
            var pages = SectionPages(section);

            var reachable = new HashSet<string>(_context.ReachableSections, StringComparer.Ordinal);
            reachable.Add(section);

            DanglingTargets(pages, findings);
            Orphans(pages, findings);
            UnlinkedMentions(pages, findings);
            NearDuplicates(pages, findings);
            MissingSources(pages, findings);
            OwnsConflicts(reachable, findings);
            Drifts(pages, manifest, findings);
            Stale(pages, now, findings);
            OutOfReach(pages, reachable, findings);
            if (manifest.State == WikiManifestState.Invalid)
                findings.Add(new WikiFinding(
                    WikiFindingKind.Drift, null, [], null, 0,
                    "манифест раздела нечитаем — сверка дрейфа невозможна"));
            foreach (var suggestion in Promotions(section))
                findings.Add(suggestion);

            return findings
                .OrderBy(f => f.Kind)
                .ThenBy(f => f.PageRelPath ?? "", StringComparer.Ordinal)
                .ThenBy(f => f.Value ?? "", StringComparer.Ordinal)
                .ThenBy(f => string.Join('\0', f.Related), StringComparer.Ordinal)
                .ToList();
        }

        // 1. Призраки: цель, которой нет в базе и на которую из раздела ссылаются ≥ порога раз.
        // Одна находка на цель: она про отсутствующую страницу, а не про каждую ссылающуюся.
        private void DanglingTargets(List<(PageRef, WikiPageFacts)> pages, List<WikiFinding> findings)
        {
            var byTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (node, _) in pages)
                foreach (var target in Outgoing(node.Id))
                    if (_ghostIds.Contains(target))
                        Add(byTarget, target, node.RelPath);
            foreach (var (target, refs) in byTarget)
            {
                var distinct = Distinct(refs);
                if (distinct.Count < GhostMinRefs)
                    continue;
                findings.Add(new WikiFinding(
                    WikiFindingKind.DanglingTarget, null, distinct, NameOf(target), distinct.Count,
                    $"несуществующую цель, на которую ссылаются {distinct.Count} стр. раздела"));
            }
        }

        // 2. Сирота: страница, на которую нет ни одной ссылки (определение базовой линии).
        private void Orphans(List<(PageRef, WikiPageFacts)> pages, List<WikiFinding> findings)
        {
            foreach (var (node, _) in pages)
                if (!_incoming.Contains(node.Id))
                    findings.Add(new WikiFinding(
                        WikiFindingKind.Orphan, node.RelPath, [], null, 0,
                        "страница, на которую нет ни одной ссылки"));
        }

        // 3. Упоминание без ссылки: заголовок другой записи базы встречается в тексте страницы
        // как отдельное слово, и разрешённой ссылки на неё нет (определение базовой линии).
        // Вхождения внутри кода не считаются — тот же смысл, что у маски «вне кода» после
        // фикса 7928ce94 (ADR-035, «Последствия», зависимость 0).
        private void UnlinkedMentions(List<(PageRef, WikiPageFacts)> pages, List<WikiFinding> findings)
        {
            foreach (var (node, facts) in pages)
            {
                var linked = new HashSet<string>(Outgoing(node.Id), StringComparer.Ordinal);
                var lower = facts.Text.ToLowerInvariant();
                var mask = CodeMask(lower);
                foreach (var other in _titleIndex)
                {
                    if (other.Id == node.Id)
                        continue;
                    var title = Norm(other.Title);
                    if (title.Length < 3 || FindWord(lower, title, mask) < 0)
                        continue;
                    if (linked.Contains(other.Id))
                        continue;
                    findings.Add(new WikiFinding(
                        WikiFindingKind.UnlinkedMention, node.RelPath, [other.RelPath], other.Title, 1,
                        "заголовок другой записи встречается текстом без ссылки"));
                }
            }
        }

        // 4. Почти дубли: заголовки двух страниц раздела на расстоянии ≤ порога. Пара один раз:
        // обход только пар i<j по отсортированному по пути списку.
        private void NearDuplicates(List<(PageRef, WikiPageFacts)> pages, List<WikiFinding> findings)
        {
            for (var i = 0; i < pages.Count; i++)
                for (var j = i + 1; j < pages.Count; j++)
                {
                    var (a, _) = pages[i];
                    var (b, _) = pages[j];
                    var distance = Levenshtein(Norm(a.Title), Norm(b.Title));
                    if (distance > NearDuplicateMaxDistance)
                        continue;
                    findings.Add(new WikiFinding(
                        WikiFindingKind.NearDuplicate, null,
                        [a.RelPath, b.RelPath], null, distance,
                        $"заголовки двух страниц различаются на {distance}"));
                }
        }

        // 5. Страница без источников: карточка источника — и есть источник (ADR-035, решение 3);
        // у страницы знаний их не может не быть: утверждение без цитаты не применяется.
        // Источник — сноска текста `[^sN]: [[Карточка]] — «цитата»` (так живут цитаты, ADR-035 §3),
        // т. е. список Footnotes: поле шапки sources не ведёт ни один писатель страниц, и по нему
        // каждая страница знаний была бы без источников.
        private void MissingSources(List<(PageRef, WikiPageFacts)> pages, List<WikiFinding> findings)
        {
            foreach (var (node, facts) in pages)
                if (facts.Type != WikiFrontmatter.TypeSource && facts.Footnotes.Count == 0)
                    findings.Add(new WikiFinding(
                        WikiFindingKind.MissingSources, node.RelPath, [], null, 0,
                        "у страницы нет ни одной сноски на карточку источника"));
        }

        // 6. Один owns у двух страниц: уникальность owns — в объединении раздела и его цепочки
        // вверх (ADR-035 §3, ADR-036 §4: «проверяют гейт и lint»), одна находка на owns.
        // Цепочка — разделы из ReachableSections (свой линтер добавляет сам): их страницы уже
        // в снимке и в фактах вызывающего, отдельного списка для них в контексте нет. Соседний
        // проект в объединение не входит: та же сущность в двух соседних проектах допустима и
        // служит сигналом к повышению (решение 5), а не конфликтом.
        private void OwnsConflicts(HashSet<string> reachable, List<WikiFinding> findings)
        {
            var byOwns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var section in reachable)
                foreach (var (_, facts) in SectionPages(section))
                    foreach (var owns in facts.Owns)
                        Add(byOwns, owns, facts.RelPath);
            foreach (var (owns, paths) in byOwns)
            {
                var distinct = Distinct(paths);
                if (distinct.Count < 2)
                    continue;
                findings.Add(new WikiFinding(
                    WikiFindingKind.OwnsConflict, null, distinct, owns, distinct.Count,
                    $"один owns у {distinct.Count} стр."));
            }
        }

        // 7. Дрейф: страницы нет в манифеста — её содержимое на диске не сверялось ни с одним
        // применением пакета (манифест — единственная запись того, что Хранитель применил).
        // Нечитаемый манифест — состояние раздела: одна строка, PageRelPath null.
        private void Drifts(List<(PageRef, WikiPageFacts)> pages, WikiManifestReadResult manifest, List<WikiFinding> findings)
        {
            if (manifest.State == WikiManifestState.Invalid)
                return;
            foreach (var (node, _) in pages)
                if (!manifest.Manifest.ContainsKey(node.RelPath))
                    findings.Add(new WikiFinding(
                        WikiFindingKind.Drift, node.RelPath, [], null, 0,
                        "страницы нет в манифеста раздела — её содержимое не сверялось с применением"));
        }

        // 8. Без изменений ≥ 90 дней. Полуприкрытый интервал: ровно 90 — находка.
        // Дату даёт вызывающий: из шапки, а когда её там нет — из mtime файла (WikiLintService).
        // Страницы без даты вовсе — не тихий пропуск: их сочтено в метрике PagesWithoutDate
        // (без неё «нет ни одной находки Stale» читалось бы как «всё свежее», хотя возраст
        // таких страниц не проверялся).
        private void Stale(List<(PageRef, WikiPageFacts)> pages, DateTimeOffset now, List<WikiFinding> findings)
        {
            foreach (var (node, facts) in pages)
            {
                if (facts.Updated is null)
                    continue;
                var updated = facts.Updated.Value;
                var days = (int)(now - updated).TotalDays;
                if (now - updated < StaleAfter)
                    continue;
                findings.Add(new WikiFinding(
                    WikiFindingKind.Stale, node.RelPath, [], null, days,
                    $"без изменений {days} дн."));
            }
        }

        // 9. out_of_reach: ссылка на страницу вне раздела и его цепочки вверх (ADR-036,
        // решение 4). Смена сферы делает прежнюю цель боковой: цепочку считает вызывающий
        // живьём через ISphereDirectory, здесь — только сравнение с ней.
        private void OutOfReach(List<(PageRef, WikiPageFacts)> pages, HashSet<string> reachable, List<WikiFinding> findings)
        {
            foreach (var (node, _) in pages)
                foreach (var target in Outgoing(node.Id))
                {
                    if (!_nodes.TryGetValue(target, out var to))
                        continue;
                    var targetSection = to.Section;
                    // Свой раздел — в reachable всегда (его добавляет этот метод), поэтому
                    // здесь же, а не в контексте, и сравнение с ним же.
                    if (targetSection is null || reachable.Contains(targetSection))
                        continue;
                    findings.Add(new WikiFinding(
                        WikiFindingKind.OutOfReach, node.RelPath, [to.RelPath], targetSection, 1,
                        "ссылка на страницу вне раздела и его цепочки вверх"));
                }
        }

        // ─── Предложение повышения (ADR-036, решение 5) ──────────────────────────

        // Тот же ключ (нормализованный заголовок или алиас) либо тот же owns в ≥ 2 проектах
        // сферы; или ≥ 2 проекта цитируют источник с одним хэшем. Это сигнал человеку в
        // _health.md, а не пакет модели: повышение расширяет видимость и решается кнопкой.
        private List<WikiFinding> Promotions(string section)
        {
            if (!WikiSections.TryProjectId(section, out _))
                return [];
            var projects = Distinct(_context.SphereProjectSections.Append(section))
                .Where(key => WikiSections.TryProjectId(key, out _))
                .ToList();
            if (projects.Count < 2)
                return [];

            var findings = SharedKeys(projects);
            findings.AddRange(SharedSourceHashes(projects));
            return findings;
        }

        private List<WikiFinding> SharedKeys(List<string> projects)
        {
            var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                if (!_bySection.TryGetValue(project, out var refs))
                    continue;
                foreach (var page in refs)
                    foreach (var key in EntityKeys(page))
                        Add(byKey, key, page.RelPath);
            }
            var findings = new List<WikiFinding>();
            foreach (var (key, paths) in byKey)
            {
                var distinct = Distinct(paths);
                if (distinct.Count < 2)
                    continue;
                findings.Add(new WikiFinding(
                    WikiFindingKind.PromoteSuggestion, null, distinct, key, distinct.Count,
                    "та же сущность в нескольких проектах сферы"));
            }
            return findings;
        }

        private List<WikiFinding> SharedSourceHashes(List<string> projects)
        {
            var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var project in projects)
            {
                if (!_bySection.TryGetValue(project, out var refs))
                    continue;
                foreach (var page in refs)
                {
                    var hash = CardHash(page);
                    if (hash is null)
                        continue;
                    Add(byHash, hash, page.RelPath);
                }
            }
            var findings = new List<WikiFinding>();
            foreach (var (hash, paths) in byHash)
            {
                var distinct = Distinct(paths);
                if (distinct.Count < 2)
                    continue;
                findings.Add(new WikiFinding(
                    WikiFindingKind.PromoteSuggestion, null, distinct, hash, distinct.Count,
                    "источник с одним хэшем цитируют несколько проектов сферы"));
            }
            return findings;
        }

        // Ключи сущности страницы: заголовок и алиасы после нормализации ИЛИ owns
        // (ADR-036 §5). owns — самостоятельный признак, не смешиваем с заголовком: два проекта
        // с разными заголовками и одним owns — ровно тот сигнал, который решение 5 зовёт.
        private IEnumerable<string> EntityKeys(PageRef page)
        {
            yield return Norm(page.Title);
            if (_facts.TryGetValue(page.RelPath, out var facts))
            {
                foreach (var alias in facts.Aliases)
                    yield return Norm(alias);
                foreach (var owns in facts.Owns)
                    yield return Norm(owns);
            }
        }

        // Хэш карточки источника (поле hash шапки type: source, ADR-035, решение 3): у
        // нечитаемой или отсутствующей шапки карточки нет — молчание, не догадка.
        private string? CardHash(PageRef page) =>
            _facts.TryGetValue(page.RelPath, out var facts) && facts.Type == WikiFrontmatter.TypeSource
                ? facts.Hash
                : null;

        // ─── Метрики по всей базе ────────────────────────────────────────────────

        // Те же определения, что в базовой линии S0b-2 (docs/research/llm-wiki-baseline-2026-10.md,
        // таблица «Что именно считается»): заметка = узел без аннотаций, ссылка = ребро (цели
        // уникализированы в пределах источника — та же декуп, что в модели), призрачная цель =
        // нерезолвленная, сирота = без входящих ссылок. Метрики не зависят от того, какие
        // факты дал вызывающий, кроме строк «по страницам вики» — они помечены в WikiMetrics.
        public WikiMetrics Metrics()
        {
            var links = _snapshot.Edges.Count;
            var danglingLinks = _snapshot.Edges.Count(e => _ghostIds.Contains(e.Target));
            var outsideJournal = _baseNotes.Where(n => !IsJournal(n.RelPath)).ToList();
            var orphans = _baseNotes.Count(n => !_incoming.Contains(n.Id));
            var orphansOutsideJournal = outsideJournal.Count(n => !_incoming.Contains(n.Id));
            var wikiPages = _bySection.Values.SelectMany(p => p).ToList();

            var measured = wikiPages
                .Where(p => _facts.ContainsKey(p.RelPath))
                .Select(p => (Node: p, Facts: _facts[p.RelPath]))
                .ToList();

            var unlinked = 0;
            foreach (var (page, facts) in measured)
            {
                var linked = new HashSet<string>(Outgoing(page.Id), StringComparer.Ordinal);
                var lower = facts.Text.ToLowerInvariant();
                var mask = CodeMask(lower);
                foreach (var other in _titleIndex)
                {
                    if (other.Id == page.Id)
                        continue;
                    var title = Norm(other.Title);
                    if (title.Length < 3 || FindWord(lower, title, mask) < 0)
                        continue;
                    if (linked.Contains(other.Id))
                        continue;
                    unlinked++;
                }
            }

            // Страницы знаний без источников — по сноскам (та же мера, что у находки
            // MissingSources): карточки источника снок не несут по форме страницы (ADR-035 §3 —
            // «карточка… данных, не рассказ»), поэтому вне счёта, как и раньше по type.
            var withoutSources = measured.Count(
                p => p.Facts.Type != WikiFrontmatter.TypeSource && p.Facts.Footnotes.Count == 0);
            // Страницы знаний без даты вовсе — ни в шапке, ни с mtime (дата с диска — тоже
            // факт вызывающего): их возраст нигде не проверялся, и ноль находок Stale при
            // таких страницах — не «всё свежее», а «ничего не известно».
            var withoutDate = measured.Count(
                p => p.Facts.Type != WikiFrontmatter.TypeSource && p.Facts.Updated is null);

            return new WikiMetrics(
                _baseNotes.Count,
                links,
                danglingLinks,
                Share(danglingLinks, links),
                _ghostIds.Count,
                orphans,
                Share(orphans, _baseNotes.Count),
                orphansOutsideJournal,
                Share(orphansOutsideJournal, outsideJournal.Count),
                wikiPages.Count,
                withoutSources,
                unlinked,
                withoutDate);
        }
    }

    // ─── Помощники разбора текста и расстояния ───────────────────────────────────

    // Маска «внутри кода»: ```-блоки и inline-`…`. Та же роль, что у CodeMask в NotesService:
    // пример синтаксиса в коде — не упоминание и не связь (дефект 7928ce94).
    private static bool[] CodeMask(string lower)
    {
        var mask = new bool[lower.Length];
        var fence = false;
        for (var i = 0; i < lower.Length; i++)
        {
            if (i + 2 < lower.Length && lower[i] == '`' && lower[i + 1] == '`' && lower[i + 2] == '`')
            {
                mask[i] = mask[i + 1] = mask[i + 2] = true;
                fence = !fence;
                i += 2;
                continue;
            }
            if (fence)
            {
                mask[i] = true;
                continue;
            }
            if (lower[i] == '`')
            {
                var end = lower.IndexOf('`', i + 1);
                if (end > i)
                {
                    for (var k = i; k <= end; k++)
                        mask[k] = true;
                    i = end;
                    continue;
                }
            }
        }
        return mask;
    }

    // Индекс вхождения needle как отдельного слова (границы — не буквы/цифры), иначе -1.
    private static int FindWord(string haystack, string needle, bool[] mask)
    {
        var from = 0;
        while (true)
        {
            var i = haystack.IndexOf(needle, from, StringComparison.Ordinal);
            if (i < 0) return -1;
            if (mask[i]) { from = i + 1; continue; }
            var before = i == 0 || !char.IsLetterOrDigit(haystack[i - 1]);
            var afterIdx = i + needle.Length;
            var after = afterIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIdx]);
            if (before && after) return i;
            from = i + 1;
        }
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>Данные, которые знает вызывающий (он читает диск, разбирает шапки и знает сферы;
/// внутри линтера — никакого I/O). Всё — готовые данные, ничего не вычисляется на месте.</summary>
public readonly record struct WikiLintContext(
    // Разделы, куда страница этого раздела может ссылаться: свой раздел + цепочка вверх
    // (WikiSections.UpChain). Всё вне неё — out_of_reach (ADR-036, решение 4).
    IReadOnlyList<string> ReachableSections,
    // Разделы проектов той же сферы, что и раздел (пусто — сферы нет): область «предложения
    // повышения» (ADR-036, решение 5).
    IReadOnlyList<string> SphereProjectSections,
    // Идентичности узлов-аннотаций (frontmatter annotates): не заметки базовой линии, не
    // сироты, не участники метрик (решение Софьи M3).
    IReadOnlyList<string> AnnotationNodeIds,
    // Факты страниц вики: для находок — страницы этого раздела, для метрик «по страницам
    // вики» — все, чьи шапки и текст вызывающий прочитал.
    IReadOnlyList<WikiPageFacts> Pages);

/// <summary>Факты страницы, разобранные вызывающим из её шапки (WikiFrontmatter) и текста.
/// Только данные: Type/Owns/SourceRefs — поля шапки; Footnotes — явный список источников
/// страницы, цели её сносок `[^sN]: [[Карточка]]` в порядке определения (счётчик Footnotes.Count
/// и есть число источников: по ADR-035 §3 живёт цитата, а поле шапки sources не заполняет ни
/// один писатель страниц); Updated — дата шапки, а когда её в шапке нет — mtime файла
/// (дата с диска — факт вызывающего, не вывод линтера); Text — содержимое файла.
/// Hash значим только у карточки источника (type: source) — это хэш её источника.</summary>
public readonly record struct WikiPageFacts(
    string RelPath,
    string Title,
    string Type,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Owns,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> Footnotes,
    DateTimeOffset? Updated,
    string? Hash,
    string Text);

/// <summary>Отчёт lint одного раздела. Находки упорядочены (вид → путь → ключ → связанные):
/// один и тот же снимок даёт один и тот же отчёт.</summary>
public readonly record struct WikiLintReport(
    string Section,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<WikiFinding> Findings,
    WikiMetrics Metrics);

/// <summary>Находка lint. Недоверенные строки (заголовки, имена призраков, значения owns,
/// хэши) — только в полях данных; Message содержит лишь форму вида и числа. Рендер строки в
/// _health.md и экранирование — за вызывающим.</summary>
public readonly record struct WikiFinding(
    WikiFindingKind Kind,
    // Путь страницы-предмета под pages/; null — находка о группе страниц или о состоянии
    // раздела (призрак, пара дублей, общий owns, нечитаемый манифест, предложение повышения).
    string? PageRelPath,
    // Связанные страницы: остальные ссылающиеся на призрака, вторая из пары, все носители
    // owns, страницы с одним ключом или одним хэшем, путь цели ссылки.
    IReadOnlyList<string> Related,
    // Ключ, о котором находка: отображаемое имя призрака, ключ раздела цели, значение owns,
    // нормализованный ключ сущности, хэш источника, заголовок упомянутой записи.
    string? Value,
    // Счёт: сколько ссылок или страниц, какое расстояние, сколько дней.
    int Count,
    // Форма находки и числа. Без недоверенных строк.
    string Message);

public enum WikiFindingKind
{
    /// <summary>Несуществующая цель, на которую из раздела ссылаются ≥ 3 стр.</summary>
    DanglingTarget,
    /// <summary>Страница, на которую нет ни одной ссылки.</summary>
    Orphan,
    /// <summary>Заголовок другой записи встречается текстом страницы без ссылки.</summary>
    UnlinkedMention,
    /// <summary>Заголовки двух страниц раздела на расстоянии Левенштейна ≤ 2.</summary>
    NearDuplicate,
    /// <summary>У страницы знаний нет ни одного источника — ни одной сноски на карточку.</summary>
    MissingSources,
    /// <summary>Один owns у двух страниц раздела или его цепочки вверх (ADR-036 §4).</summary>
    OwnsConflict,
    /// <summary>Страницы нет в манифеста / манифест нечитается (ADR-035, решение 2).</summary>
    Drift,
    /// <summary>Без изменений ≥ 90 дней.</summary>
    Stale,
    /// <summary>Ссылка на страницу вне раздела и его цепочки вверх (ADR-036, решение 4).</summary>
    OutOfReach,
    /// <summary>Тот же ключ в ≥ 2 проектах сферы или общий источник с одним хэшем (решение 5).</summary>
    PromoteSuggestion,
}

/// <summary>Метрики по всей базе — те же определения, что в базовой линии S0b-2
/// (docs/research/llm-wiki-baseline-2026-10.md, таблица «Что именно считается»). Доли — 0 при
/// пустом знаменателе. Аннотации в знаменатель не входят (M3); призраки — все узлы снимка,
/// включая тех, у кого нет рёбер (M4). Последние четыре строки — про страницы вики: они считаются
/// по тем страницам, чьи факты дал вызывающий, и без его чтения полны не будут; аналогов в
/// базовой линии нет — такой половины базы она не считала.</summary>
public readonly record struct WikiMetrics(
    int NoteCount,
    int LinkCount,
    int DanglingLinkCount,
    double DanglingLinkShare,
    int DanglingTargetCount,
    int OrphanCount,
    double OrphanShare,
    // Сироты без Journal/ — headline-число базовой линии.
    int OrphanCountExcludingJournal,
    double OrphanShareExcludingJournal,
    int WikiPageCount,
    // Страницы знаний без ни одной сноски на карточку (та же мера, что у находки MissingSources).
    int WikiPagesWithoutSources,
    int UnlinkedMentionPairs,
    // Страницы знаний, чей возраст не проверялся: даты нет ни в шапке, ни с mtime файла.
    int PagesWithoutDate);
