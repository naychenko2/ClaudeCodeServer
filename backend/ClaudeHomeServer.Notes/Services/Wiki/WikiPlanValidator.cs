using System.Security.Cryptography;
using System.Text;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Проверки плана модели до применения и до сборки пакета (ADR-035, §12б этапы 6–8,
// §12г меры 1–3). Чистая функция без модели и без диска: на вход — утверждения прохода A,
// операции плана прохода B, источник (origin + карточка + текст выдержки), раздел
// назначения, цепочка вверх и снимок страниц этих разделов; на выход — операции, принятые
// в пакет (с классом и решением политики — ставит код, не модель), операции «ждущие»
// сверх лимита, отклонённые с причиной и общие нарушения состояния.
//
// Порядок: форма цепочки вверх → форма операции → разрешение страницы → подтверждение
// цитаты (QuoteMatcher, мера 1) → анти-петля → повтор утверждения (дедуп меры 2) → цель
// операции (кроме supersede: его цель проверяется рендером — в том состоянии, что будет
// записано) → лимиты (CheckLimits ДО сборки, счётчик включает текущий элемент) → политика
// (S4d) → черновой рендер → весь WikiGate с текстами источников (дословность — только для
// карточек этого пакета, GivenOnly). Отклонение по первой причине: дальше операция не
// идёт; внутренняя ошибка одной операции отклоняет её, а не роняет валидатор наружу.
//
// Текст плана и источника — недоверенный ввод модели: ничто из него не исполняется и не
// становится путём без проверки: существующая страница разрешается только по снимку,
// новая — именем, собранным папкой раздела и перечитанным WikiSections.Parse.
//
// Класс операции — по источнику ЭТОЙ операции (origin × op), не из самооценки плана
// (хвост S4d, г). Дедуп страниц — через S4d, и только по разделу назначения и цепочке
// вверх: никогда по соседних проектов или личного (хвост S4d, б).
//
// Граница: рендер здесь — черновой, ради проверок. Полные поля шапки создаваемой
// страницы (type, summary, sources) план не называет — их решает шаг применения; здесь
// стоит заглушка topic: по ней гейт проверяет содержание — утверждения, сноски, ссылки.
// Хроника плана (about/decided/…) здесь не разбирается: страница хроники — механическая
// операция применения (§12б), не намерение модели.
public static class WikiPlanValidator
{
    /// <summary>Утверждение прохода A: текст + дословная цитата (ответ модели).</summary>
    public sealed record Claim(string? Text, string? Quote);

    /// <summary>Операция плана прохода B: page — заголовок или путь страницы, claim — индекс
    /// утверждения, target — цель (замещаемое/оспариваемое утверждение или имя другой
    /// страницы). Всё — недоверенный ввод.</summary>
    public sealed record PlanOp(string? Op, string? Page, int? Claim, string? Target);

    public sealed record Plan(IReadOnlyList<PlanOp>? Operations);

    /// <summary>Источник плана: origin из закрытого перечня, Card — заголовок карточки
    /// (цель сносок принятых операций), Text — текст выдержки (по ней подтверждается
    /// цитата).</summary>
    public sealed record Source(string? Origin, string? Card, string? Text);

    /// <summary>Операция, не вошедшая в пакет: Index — индекс в plan.Operations.</summary>
    public sealed record Issue(int Index, string Reason);

    public sealed record Result(
        /// <summary>Операции, прошедшие проверки кода: с классом и решением политики
        /// (auto применяет код, inbox ждёт человека — оба входят в пакет).</summary>
        IReadOnlyList<WikiPackage.Operation> Accepted,
        /// <summary>Сверх лимитов меры 3: «ждёт следующей ночи», не «Входящие».</summary>
        IReadOnlyList<Issue> Waiting,
        /// <summary>Отклонённые проверками: форма, цитата, анти-петля, повтор, цель.</summary>
        IReadOnlyList<Issue> Rejected,
        /// <summary>Нарушения без связи с операцией плана: кривой вход, уже лежащее на
        /// страницах состояние раздела — их видит вызывающий, план от них не зависит.</summary>
        IReadOnlyList<string> Violations);

    public static Result Validate(
        Plan? plan,
        IReadOnlyList<Claim>? claims,
        Source? source,
        string? sectionKey,
        IReadOnlyList<string>? upChain,
        IReadOnlyList<WikiGate.Draft>? sectionPages,
        WikiApplyPolicy.ApplyContext context)
    {
        var rejected = new List<Issue>();
        var waiting = new List<Issue>();
        var violations = new List<string>();
        var ctx = context ?? new();

        // ── 1. Fail-closed входа: ни null, ни кривой вход не становится исключением. ──
        if (plan is null || plan.Operations is null)
            return new([], [], rejected, [.. violations, "план null или операции плана null — проверять нечего"]);
        if (source is null || source.Origin is not { } origin || !WikiGate.AiOriginValues.Contains(origin))
            return new([], [], IndexAll(plan, "источник не определён: origin вне закрытого перечня или источник null"),
                [.. violations, "план без подтверждённого источника не применяется"]);
        if (string.IsNullOrEmpty(source.Text))
            return new([], [], IndexAll(plan, "текста источника нет — цитату не подтвердить"),
                [.. violations, "план без текста источника не применяется"]);
        if (string.IsNullOrEmpty(sectionKey) || !WikiSections.IsWritable(sectionKey).Writable)
            return new([], [], IndexAll(plan, $"раздел «{sectionKey ?? "—"}» не задан или не является записываемым"),
                [.. violations, "пустой или неразобранный раздел — плана нет"]);

        // Цепочку «вверх» даёт вызывающий (WikiSections.UpChain от раздела назначения).
        // Сфер и проектов он здесь не знает, поэтому валидатор держит её форму: только
        // spheres/* и common, без projects/*, повторов, пустых элементов и самого раздела.
        // Невалидная цепочка отвергает весь пакет ДО снимка и дедупа: чужой путь не войдёт
        // ни в дедуп-пул, ни в причину операции, а нарушение цепочки не утонет в общем
        // списке без пути (ревью S6b-2, M3).
        var chain = upChain ?? [];
        var chainError = ChainError(chain, sectionKey);
        if (chainError is not null)
            return new([], [], IndexAll(plan, chainError), [.. violations, "цепочка вверх не валидна — план не применяется"]);

        var allowed = new HashSet<string>(StringComparer.Ordinal) { sectionKey };
        allowed.UnionWith(chain);

        // ── 2. Снимок: только раздел назначения и цепочка вверх (хвост S4d, б). ──
        // Путь, не принадлежащий этим разделам, в состояние не входит: соседний проект
        // или личное не дают ни дедупа, ни резолва ссылок. Неразобранная страница
        // остаётся как есть: её увидит гейт как нарушение состояния.
        var pages = new List<Snap>();
        foreach (var d in sectionPages ?? [])
        {
            var sec = WikiSections.Parse(d.Path);
            if (sec is null || !allowed.Contains(sec))
                continue;
            pages.Add(new Snap(d.Path, sec, d.Content, WikiPage.Parse(d.Content).Model));
        }
        var targetPages = pages.Where(p => p.Section == sectionKey).ToList();

        // Карточка источника — цель сносок принятых операций. Нет её в разделе или вверх
        // по цепочке — сноска не резолвится: каждая опирающаяся на неё операция отклоняется.
        var card = FindByTitle(pages, source.Card);
        if (card is null)
            return new([], [], IndexAll(plan, $"карточка источника «{source.Card}» не найдена в разделе и вверх по цепочке"),
                [.. violations, "план без карточки источника не применяется"]);

        // ── 3. Разбор операций плана. Счётчики включают ТЕКУЩИЙ элемент (хвост S4d, в). ──
        var newPages = ctx.Counters.NewKnowledgePagesInSection;
        var statements = ctx.Counters.StatementsInPackage;
        var candidates = new List<(int Index, WikiPackage.Operation Op, string Quote, bool Rendered)>();
        var rejectedAt = new HashSet<int>();

        for (var i = 0; i < plan.Operations.Count; i++)
        {
            var op = plan.Operations[i];
            if (op is null)
            {
                rejected.Add(new(i, "null в списке операций плана"));
                rejectedAt.Add(i);
                continue;
            }
            if (op.Op is null || !WikiPackage.OpValues.Contains(op.Op))
            {
                rejected.Add(new(i, $"операция «{op.Op ?? "—"}» вне перечня — в пакет она не входит"));
                rejectedAt.Add(i);
                continue;
            }
            if (IsProjectionRef(op.Page))
            {
                rejected.Add(new(i, $"«{op.Page}»: в '_*' (\"_schema\", \"_index\", \"_log\", \"_health\") пишет код, план туда не пишет"));
                rejectedAt.Add(i);
                continue;
            }

            var isCreate = op.Op == WikiPackage.OpCreate;
            // Страница: заголовок или путь — только раздела назначения (c4). Для create
            // это всегда новая страница; для прочих — существующая страница раздела,
            // «повышение» в другой раздел — действие человека, не план.
            Snap? page;
            string reason;
            if (isCreate)
                page = ResolveNewPage(op.Page, sectionKey, out reason);
            else
                page = ResolveExistingPage(op.Page, sectionKey, targetPages, out reason);
            if (page is null)
            {
                rejected.Add(new(i, reason));
                rejectedAt.Add(i);
                continue;
            }
            if (!isCreate && page.Model is null)
            {
                rejected.Add(new(i, $"страница «{page.Path}» не разбирается — менять неразобранную нельзя"));
                rejectedAt.Add(i);
                continue;
            }
            var title = isCreate ? page.Path.Split('/')[^1][..^3] : page.Model!.Title;

            // Утверждение: индекс в списке прохода A, текст и цитата — непустые.
            var claim = op.Claim is { } ci && claims is not null && ci >= 0 && ci < claims.Count
                ? claims[ci]
                : null;
            if (claim is null)
            {
                rejected.Add(new(i, "операция без утверждения: индекс claim вне списка утверждений или утверждений нет"));
                rejectedAt.Add(i);
                continue;
            }
            if (string.IsNullOrWhiteSpace(claim.Text) || string.IsNullOrWhiteSpace(claim.Quote))
            {
                rejected.Add(new(i, "утверждение без текста или без цитаты"));
                rejectedAt.Add(i);
                continue;
            }

            // Мера 1: цитата дословно в тексте источника. Нет — анти-петля (c3): цитата
            // дословна на странице вики, а не в источнике — модель пересказала вики.
            if (!QuoteMatcher.ContainsVerbatim(source.Text, claim.Quote))
            {
                rejected.Add(new(i,
                    page.Model is not null && QuoteMatcher.ContainsVerbatim(page.Content, claim.Quote)
                        ? $"anti_loop: цитата дословна на странице «{page.Path}», а не в источнике — пересказ вики не добавляется"
                        : "цитата отсутствует в тексте источника дословно"));
                rejectedAt.Add(i);
                continue;
            }

            // Мера 2: та же нормализованная цитата или тот же текст уже на странице —
            // повтор отбрасывается молча, без «Входящих» и без пакета.
            if (page.Model is not null && WikiDedup.IsDuplicateStatement(page.Model, card.Model!.Title, claim.Text, claim.Quote))
            {
                rejected.Add(new(i, $"повтор: утверждение уже на странице «{page.Path}»"));
                rejectedAt.Add(i);
                continue;
            }

            // Форма намерения: supersede/conflict — цель-утверждение, оно обязано
            // дословно стоять на странице (план копирует его дословно); link — цель
            // непустая, а резолвится она в итоговом состоянии тем же правилом, что и
            // любая ссылка (вбок/вниз — нарушение гейта).
            if (op.Op is WikiPackage.OpSupersede or WikiPackage.OpConflict or WikiPackage.OpLink
                && string.IsNullOrWhiteSpace(op.Target))
            {
                rejected.Add(new(i, op.Op == WikiPackage.OpLink
                    ? "операция «link» без цели (имени другой страницы)"
                    : $"операция «{op.Op}» без цели (замещаемого или оспариваемого утверждения)"));
                rejectedAt.Add(i);
                continue;
            }
            // Цель supersede здесь НЕ ищется: замещаемое утверждение проверяется рендером —
            // в том состоянии, которое будет записано (после предыдущих принятых операций).
            // Проверка по исходному снимку расходилась с рендером: план из двух правок одного
            // факта ронял валидатор исключением (ревью S6b-2, M1).
            // Карточка источника — не страница знаний: «Сводки» и места утверждению в ней
            // нет, но отказ «нет секции» модель обходит молча. Причина прямая: в карточку
            // операцию не принять, новое знание — через create (WC-2).
            if (!isCreate && page.Model!.Frontmatter.Type == WikiFrontmatter.TypeSource)
            {
                rejected.Add(new(i,
                    $"страница «{page.Path}» — карточка источника, а не страница знаний: для нового знания — create"));
                rejectedAt.Add(i);
                continue;
            }
            string? targetSection = op.Op switch
            {
                WikiPackage.OpConflict => "Противоречия",
                _ when op.Op is WikiPackage.OpAppend or WikiPackage.OpLink => "Сводка",
                _ => null,
            };
            if (targetSection is not null && !page.Model!.Sections.Any(s => s.Name == targetSection))
            {
                rejected.Add(new(i, $"на странице «{page.Path}» нет секции «{targetSection}» — изменять некуда"));
                rejectedAt.Add(i);
                continue;
            }

            // Мера 3 ДО сборки пакета: лимит новых страниц — на каждую create страницы
            // знаний, лимит утверждений — на каждое содержание пакета. Превышение —
            // «ждёт следующей ночи», не «Входящие» (счётчик уже включает текущий элемент).
            var pagesCount = isCreate ? newPages + 1 : newPages;
            var statementsCount = statements + 1;
            var limit = WikiApplyPolicy.CheckLimits(
                new(pagesCount, statementsCount, ctx.Counters.PackagesForPage), ctx.IsBacklogPass);
            if (limit.Waits)
            {
                waiting.Add(new(i, limit.WaitingReason!));
                rejectedAt.Add(i);
                continue;
            }
            if (isCreate)
                newPages = pagesCount;
            statements = statementsCount;

            // Дедуп новой страницы — по разделу назначения и цепочке вверх (хвост S4d, б).
            // Совпадение — «возможный дубль»: решает человек (политика), и в черновом
            // состоянии новой страницы нет: состояния с двумя одинаковыми именами вики
            // не бывает, его применение — исход решения человека, а не плана.
            var duplicate = isCreate
                ? WikiDedup.FindPageDuplicate(title, null, null, DedupPool(pages, allowed))
                : null;
            if (isCreate && duplicate is null && pages.Any(p => p.Path == page.Path))
            {
                rejected.Add(new(i, $"путь новой страницы «{page.Path}» занят: create не перекрывает существующий файл"));
                rejectedAt.Add(i);
                continue;
            }

            // Класс — по источнику этой операции (хвост S4d, г); решение — по политике S4d.
            // Для supersede/conflict класс и автор цели — по сноскам утверждения на странице
            // СЕЙЧАС, а не по ответу модели (ADR-038, решение 4): цель human — свой класс «…@human».
            var isReplacement = op.Op is WikiPackage.OpSupersede or WikiPackage.OpConflict;
            var targetCards = isReplacement ? TargetCards(page, op.Target, pages) : [];
            var humanTarget = targetCards.Any(c => c.Origin == WikiGate.OriginHuman);
            var opClass = WikiPackage.ClassKey(origin, op.Op, humanTarget);
            var verdict = WikiApplyPolicy.Decide(
                new()
                {
                    Op = op.Op,
                    Page = page.Path,
                    Target = op.Target,
                    Text = claim.Text,
                    Class = opClass,
                    Decision = WikiPackage.DecisionAuto, // placeholder: Decide его не читает
                },
                ctx with
                {
                    Counters = new(pagesCount, statementsCount, ctx.Counters.PackagesForPage),
                    PageDuplicate = duplicate,
                    TargetPageStatus = page.Model?.Frontmatter.Status,
                    TargetSourceTime = isReplacement ? TargetSourceTimeOf(targetCards) : null,
                    TargetAuthor = !isReplacement
                        ? null
                        : humanTarget ? WikiPackage.AuthorHuman : WikiPackage.AuthorAi,
                    TargetHasNoHistory = op.Op == WikiPackage.OpSupersede
                        && page.Model is { } model && model.Sections.All(s => s.Name != "История"),
                });

            candidates.Add((i, new()
            {
                Op = op.Op,
                Page = page.Path,
                Target = op.Target,
                Text = claim.Text,
                Class = opClass,
                Decision = verdict.Decision,
                Reason = verdict.Reason,
                BaseHashes = isCreate
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string> { [page.Path] = Sha256Hex(page.Content) },
            }, claim.Quote, duplicate is null));
        }

        // ── 4. Черновой рендер, итоговое состояние и весь WikiGate. ──
        var state = new List<WikiGate.Draft>(pages.Select(p => new WikiGate.Draft(p.Path, p.Content)));
        // База правил автора — состояние раздела ДО рендера плана: карточка human и «Заметки»,
        // уже лежащие на страницах, нарушением ai-плана не считаются.
        var baseState = state.ToArray();
        var touched = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var (index, op, quote, rendered) in candidates)
        {
            if (!rendered)
                continue;
            // Рендер читает страницу ПОСЛЕ предыдущих принятых операций: вторая операция на
            // той же странице видит результат первой — и цель её проверяется там же, где
            // рендер (ревью S6b-2, M1).
            WikiGate.Draft? draftOrNull;
            try
            {
                draftOrNull = RenderApply(state, op, quote, card.Model!.Title);
            }
            catch (Exception)
            {
                // Подстраховка: план недоверен, и ни одна внутренняя ошибка одной операции
                // не должна ронять валидатор наружу — она отклоняет свою операцию.
                if (rejectedAt.Add(index))
                    rejected.Add(new(index, "внутренняя ошибка обработки операции"));
                continue;
            }
            if (draftOrNull is null)
            {
                if (rejectedAt.Add(index))
                    rejected.Add(new(index, $"цель supersede не на странице «{op.Page}»: замещать нечего"));
                continue;
            }
            var draft = draftOrNull.Value;
            state.RemoveAll(d => d.Path == op.Page);
            state.Add(draft);
            if (!touched.TryGetValue(op.Page, out var ids))
                touched[op.Page] = ids = [];
            ids.Add(index);
        }

        // Тексты источников, которые есть у этого пакета: только карточка его источника.
        // GivenOnly — потому и проверяются только сноски на неё: чужие (принятые прошлыми
        // ночами) сноски страницы не могут отвергнуть новую операцию (ревью S6b-2, M2).
        var sourceTexts = new Dictionary<string, string> { [card.Path] = source.Text };
        foreach (var v in WikiGate.Check(state, sectionKey, chain, sourceTexts, WikiGate.VerbatimScope.GivenOnly,
                     WikiPackage.AuthorAi, baseState))
        {
            var path = v.Split(':', 2)[0];
            if (touched.TryGetValue(path, out var ids))
            {
                // Нарушение итоговой страницы — отказ всех операций, её изменивших:
                // разбирать, чья строка его открыла, будет шаг применения.
                foreach (var id in ids)
                    if (rejectedAt.Add(id))
                        rejected.Add(new(id, v));
            }
            else
            {
                violations.Add(v); // уже лежит на страницах раздела: план от него не зависит
            }
        }

        rejected.Sort((a, b) => a.Index.CompareTo(b.Index));
        return new(
            candidates.Where(c => !rejectedAt.Contains(c.Index)).Select(c => c.Op).ToList(),
            waiting,
            rejected,
            violations);
    }

    // ─── Разбор страницы плана ─────────────────────────────────────────────────
    // Путь разбирается только WikiSections.Parse и только раздела назначения; заголовок
    // существующей страницы — по той же нормализации, чем мерит uniqueness гейт; имя
    // новой страницы собирается папкой раздела и перечитывается Parse: недоверенное имя
    // модели само путём не становится.

    private sealed record Snap(string Path, string Section, string Content, WikiPage? Model);

    private static bool IsProjectionRef(string? pageRef)
    {
        if (string.IsNullOrWhiteSpace(pageRef))
            return false;
        var last = pageRef.Split('/')[^1];
        return last.StartsWith('_');
    }

    private static Snap? ResolveExistingPage(string? pageRef, string sectionKey, List<Snap> targetPages, out string reason)
    {
        if (string.IsNullOrWhiteSpace(pageRef))
        {
            reason = "операция без страницы";
            return null;
        }
        if (WikiSections.Parse(pageRef) is { } asPath)
        {
            if (asPath != sectionKey)
            {
                reason = $"страница «{pageRef}» не раздела «{sectionKey}»";
                return null;
            }
            var byPath = targetPages.FirstOrDefault(p => p.Path == pageRef);
            if (byPath is not null)
            {
                reason = "";
                return byPath;
            }
            reason = $"страницы «{pageRef}» нет в снимке раздела «{sectionKey}»";
            return null;
        }
        var byTitle = FindByTitle(targetPages, pageRef);
        if (byTitle is not null)
        {
            reason = "";
            return byTitle;
        }
        reason = $"страницы «{pageRef}» нет в разделе «{sectionKey}»: менять несуществующую нельзя";
        return null;
    }

    private static Snap? ResolveNewPage(string? pageRef, string sectionKey, out string reason)
    {
        if (string.IsNullOrWhiteSpace(pageRef))
        {
            reason = "create без страницы";
            return null;
        }
        var title = pageRef;
        if (WikiSections.Parse(pageRef) is { } asPath)
        {
            if (asPath != sectionKey)
            {
                reason = $"страница «{pageRef}» не раздела «{sectionKey}»";
                return null;
            }
            var file = pageRef.Split('/')[^1];
            if (!file.EndsWith(".md", StringComparison.Ordinal))
            {
                reason = $"имя новой страницы «{file}» не файл .md";
                return null;
            }
            title = file[..^3];
        }

        title = title.Trim();
        if (title.Length == 0 || title.Contains('/') || title.Contains('\\') ||
            title.Any(c => c < ' ') || title is "." or ".." || title.StartsWith('_'))
        {
            reason = $"«{pageRef}» не может быть именем новой страницы";
            return null;
        }
        var newPath = WikiSections.Folder(sectionKey)! + title + ".md";
        if (WikiSections.Parse(newPath) != sectionKey)
        {
            reason = $"имя новой страницы «{title}» не даёт пути раздела «{sectionKey}»";
            return null;
        }
        reason = "";
        return new Snap(newPath, sectionKey, "", null);
    }

    private static Snap? FindByTitle(List<Snap> pages, string? title)
    {
        if (string.IsNullOrEmpty(title))
            return null;
        var norm = WikiDedup.NormalizeName(title);
        if (norm.Length == 0)
            return null;
        foreach (var p in pages)
            if (p.Model is not null && WikiDedup.NormalizeName(p.Model.Title) == norm)
                return p;
        return null;
    }

    // Строка утверждения с данным текстом — сравнение той же нормализацией, чем дедуп и
    // дословность. Возвращает секцию и индекс строки (Section null — цели на странице нет).
    private readonly record struct Hit(string? Section, int Line);

    // Карточки источников утверждения-цели: строка с целью → её сноски [^sN] → карточки по
    // заголовку цели сноски. Нет страницы, цели или карточки в снимке — пусто (цель ИИ по
    // умолчанию: «автор human» утверждает только найденная карточка origin: human).
    private static IReadOnlyList<WikiFrontmatter> TargetCards(Snap page, string? target, List<Snap> pages)
    {
        if (page.Model is not { } model)
            return [];
        var hit = FindStatement(model, target);
        if (hit.Section is null)
            return [];
        var line = model.Sections.First(sec => sec.Name == hit.Section).Lines[hit.Line];
        var cards = new List<WikiFrontmatter>();
        foreach (System.Text.RegularExpressions.Match m in TargetFootnoteRx.Matches(line))
        {
            if (!int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var n)
                || n < 1 || n > model.Footnotes.Count)
                continue;
            var card = FindByTitle(pages, model.Footnotes[n - 1].Target.Text);
            if (card?.Model is { } cm && cm.Frontmatter.Type == WikiFrontmatter.TypeSource)
                cards.Add(cm.Frontmatter);
        }
        return cards;
    }

    // Время источника по карточке: written (когда написан источник); у карточки human — captured
    // (правка и фиксация одно событие, WikiHumanRender). Не captured вообще: у бэклога старых чатов
    // оно — сегодня. Самое новое среди карточек; хотя бы одна без времени (или карточек нет) — null.
    private static DateTimeOffset? TargetSourceTimeOf(IReadOnlyList<WikiFrontmatter> cards)
    {
        if (cards.Count == 0)
            return null;
        DateTimeOffset? newest = null;
        foreach (var card in cards)
        {
            var text = card.Origin == WikiGate.OriginHuman ? card.Captured : card.Written;
            if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                return null;
            if (newest is null || t > newest)
                newest = t;
        }
        return newest;
    }

    private static readonly System.Text.RegularExpressions.Regex TargetFootnoteRx =
        new(@"\[\^s([0-9]+)\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static Hit FindStatement(WikiPage page, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return new(null, -1);
        foreach (var section in page.Sections)
        {
            // «История» хранит заменённые утверждения — целью новой замены они не бывают
            // (иначе вторая замена той же цели нашла бы копию в «Истории», а не отклонилась).
            if (section.Name == "История" || !WikiPage.AssertionSectionNames.Contains(section.Name))
                continue;
            for (var l = 0; l < section.Lines.Count; l++)
                if (QuoteMatcher.ContainsVerbatim(section.Lines[l], target))
                    return new(section.Name, l);
        }
        return new(null, -1);
    }

    // Форма цепочки «вверх» (ADR-036, решение 4): только сферы и общее, без проектов,
    // повторов, пустых элементов и самого раздела назначения. Содержательную цепочку
    // (какая сфера у какого проекта) знает вызывающий — WikiSections.UpChain; здесь
    // держится только то, что не даёт чужому пути войти в снимок и в причину операции.
    private static string? ChainError(IReadOnlyList<string> chain, string sectionKey)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in chain)
        {
            if (string.IsNullOrEmpty(key))
                return "цепочка вверх: пустой элемент цепочки";
            if (key == sectionKey)
                return $"цепочка вверх: '{key}' — это сам раздел назначения, вверх по цепочке он не идёт";
            if (key != WikiSections.Common && !WikiSections.TrySphereId(key, out _))
                return $"цепочка вверх: '{key}' недопустима: вверх идут только spheres/* и {WikiSections.Common}, без projects/*";
            if (!seen.Add(key))
                return $"цепочка вверх: '{key}' повторён";
        }
        return null;
    }

    private static List<WikiDedup.ExistingPage> DedupPool(List<Snap> pages, HashSet<string> allowed) =>
        pages.Where(p => p.Model is not null && allowed.Contains(p.Section))
             .Select(p => new WikiDedup.ExistingPage(p.Path, p.Model!))
             .ToList();

    // ─── Черновой рендер операции в страницу ───────────────────────────────────
    // append/link → «Сводка», conflict → «Противоречия», supersede → замена строки цели
    // в её секции; create → новая страница с шапкой-заглушкой (полные поля решает
    // применение). Сноска каждой добавленной строки — на карточку источника, с той самой
    // цитатой, что подтверждена мерой 1: утверждение принятого плана без сноски невозможн.
    /// <summary>Черновой рендер операции в состояние. null — цель supersede не найдена в
    /// этом состоянии страницы (её уже заместила предыдущая принятая операция): операции
    /// нечего замещать, вызывающий отклоняет её с причиной. internal (S4b-1): тот же рендер
    /// зовёт проверка применения — второй копии рендера в проекте нет; поведение не менялось.</summary>
    internal static WikiGate.Draft? RenderApply(List<WikiGate.Draft> state, WikiPackage.Operation op, string quote, string cardTitle)
    {
        // Черновой рендер: страница уже в состоянии или её нет (create) — для create
        // страница строится заново; неразобранная существующая сюда не доходит, она
        // отклонена раньше.
        var existing = state.FirstOrDefault(x => x.Path == op.Page);
        var page = existing.Path == op.Page ? WikiPage.Parse(existing.Content).Model : null;
        if (page is null)
        {
            page = new WikiPage
            {
                Title = op.Page.Split('/')[^1][..^3],
                Frontmatter = new WikiFrontmatter { Type = WikiFrontmatter.TypeTopic },
                Sections = WikiPage.SectionNames
                    .Select(n => new WikiPage.Section { Name = n, Lines = new List<string>() })
                    .ToList(),
            };
        }

        // Цель supersede ищется здесь и один раз — в состоянии после предыдущих принятых
        // операций: та же точка, куда смотрит запись (ревью S6b-2, M1).
        var hit = op.Op == WikiPackage.OpSupersede ? FindStatement(page, op.Target) : default;
        if (op.Op == WikiPackage.OpSupersede && hit.Section is null)
            return null;

        var sectionName = op.Op switch
        {
            WikiPackage.OpConflict => "Противоречия",
            WikiPackage.OpSupersede => hit.Section!,
            _ => "Сводка",
        };
        var sections = page.Sections
            .Select(s => (s.Name, Lines: s.Lines.ToList()))
            .ToList();
        var targetIdx = sections.FindIndex(s => s.Name == sectionName);

        var link = op.Op == WikiPackage.OpLink ? $" [[{op.Target}]]" : "";
        var line = $"{op.Text}{link} [^s{page.Footnotes.Count + 1}]";
        if (op.Op == WikiPackage.OpSupersede)
        {
            var old = sections[targetIdx].Lines[hit.Line];
            sections[targetIdx].Lines[hit.Line] = line;
            // Прежняя формулировка — дословно, со своими сносками, в «Историю» (ADR-035 §3,
            // ADR-038 предохранитель 1): по ней retract находит пару и возвращает формулировку
            // человека. Из самой «Истории» не переносим; такая же строка там уже есть — не дублируем.
            var historyIdx = sections.FindIndex(s => s.Name == "История");
            if (historyIdx >= 0 && historyIdx != targetIdx && !sections[historyIdx].Lines.Contains(old))
                sections[historyIdx].Lines.Add(old);
        }
        else
            sections[targetIdx].Lines.Add(line);

        var footnotes = page.Footnotes
            .Append(new WikiPage.WikiFootnote(new WikiPage.Target(true, cardTitle), quote))
            .ToList();

        return new WikiGate.Draft(op.Page, new WikiPage
        {
            Title = page.Title,
            Frontmatter = page.Frontmatter,
            Sections = sections.Select(s => new WikiPage.Section { Name = s.Name, Lines = s.Lines }).ToList(),
            Footnotes = footnotes,
        }.Render());
    }

    private static string Sha256Hex(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static List<Issue> IndexAll(Plan plan, string reason) =>
        Enumerable.Range(0, plan.Operations?.Count ?? 0).Select(i => new Issue(i, reason)).ToList();
}
