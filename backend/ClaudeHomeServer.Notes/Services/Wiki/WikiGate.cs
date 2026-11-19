using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Гейт страницы вики (ADR-035, решение 3; ADR-036, решение 4): чистая функция
// (состояние после изменения, раздел) → нарушения. Состояние — тексты страниц
// раздела и разделов выше по цепочке (их даёт вызывающий: WikiSections.UpChain);
// гейт сам их разбирает и проверяет правила. Нарушение — строка с причиной для
// человека и для Хранителя: «на ремонт модели даётся одна попытка с перечнем нарушений».
// Дословность цитаты проверяется, когда вызывающий дал тексты источников (sourceTexts,
// захват — S6h/S6j): сноска сверяется с текстом своей карточки через QuoteMatcher
// (ADR-035, §12г мера 1). При sourceTexts == null правило не выполняется — это режим
// по умолчанию до слоя захвата, а не «пройдено молча». Область правила — verbatimScope:
// All (по умолчанию) требует текст для каждой сноски; GivenOnly проверяет только сноски
// на карточки, чей текст дан (ревью S6b-2, M2).
public static class WikiGate
{
    // Закрытый перечень origin карточки источника (ADR-035, решение 3).
    public const string OriginNote = "note";
    public const string OriginFile = "file";
    public const string OriginSession = "session";
    public const string OriginUrl = "url";
    public const string OriginTask = "task";
    public const string OriginDossier = "dossier";
    public const string OriginMemory = "memory";
    // Снимок текста, написанного человеком (ADR-038): карточку создаёт только пакет автора human.
    public const string OriginHuman = "human";

    public static readonly IReadOnlyList<string> OriginValues =
        [OriginNote, OriginFile, OriginSession, OriginUrl, OriginTask, OriginDossier, OriginMemory, OriginHuman];

    /// <summary>Origin, которые вправе заявлять пакеты ai (ingest, ночной разбор, MCP): весь
    /// перечень без human. Заявка ai с origin: human отклоняется на входе, до карточки.</summary>
    public static readonly IReadOnlyList<string> AiOriginValues =
        [.. OriginValues.Where(o => o != OriginHuman)];

    // Задача-чекбокс на странице вики запрещена формой страницы (ADR-035, решение 2).
    private static readonly Regex CheckboxRx = new(@"^\s*[-*] \[[ xX]\]", RegexOptions.CultureInvariant);
    private static readonly Regex FootnoteRefRx = new(@"\[\^([^]\r\n]+)\]", RegexOptions.CultureInvariant);

    /// <summary>Страница состояния: путь относительно pages/ и текст файла.</summary>
    public readonly record struct Draft(string Path, string Content);

    /// <summary>Область правила дословности (применяется, когда sourceTexts задан).</summary>
    public enum VerbatimScope
    {
        /// <summary>Каждая сноска требует текст своей карточки: нет текста — нарушение
        /// (fail-closed). Поведение по умолчанию.</summary>
        All,
        /// <summary>Только сноски на карточки, чей текст есть в словаре. Сноска на карточку
        /// вне словаря не проверяется и нарушения не даёт: вызывающий знает тексты своих
        /// источников и не может чужой (принятой ранее) сноской отвергнуть новую запись —
        /// её дословность проверялась, когда её добавляли. Сноски на карточки из словаря
        /// проверяются как в All (ревью S6b-2, M2).</summary>
        GivenOnly,
    }

    /// <summary>Проверяет состояние после изменения перед записью в раздел sectionKey.
    /// Контракт state: страницы раздела sectionKey плюс страницы разделов цепочки upChain;
    /// путь, не принадлежащий вики, и страница вне этих разделов — нарушение (fail-closed).
    /// upChain — цепочка «вверх» от раздела (WikiSections.UpChain): по ней резолвятся
    /// ссылки и проверяется уникальность owns; гейт сам сверяет её с ADR-036, решение 4:
    /// допустимы только spheres/* и common, без повторов. Пустой список — нарушений нет.
    /// sourceTexts — тексты источников по пути карточки (ключ — Draft.Path карточки):
    /// правило дословности (ADR-035, §12г мера 1). null — правило не проверяется (захват
    /// источников — S6h/S6j); задан — сноска карточки вне словаря даёт нарушение только в
    /// All, цитата без дословного вхождения в тексте своей карточки — нарушение всегда.
    /// verbatimScope — область этого правила (см. VerbatimScope); по умолчанию All.
    /// author — автор пакета (WikiPackage.AuthorAi по умолчанию, fail-closed). Для не-human
    /// гейт запрещает создавать карточку origin: human и менять секцию «Заметки»: «новое» —
    /// это отличие от baseState (состояние раздела до пакета; null — базы нет, и всё, что
    /// относится к human, считается новым).</summary>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<Draft> state,
        string sectionKey,
        IReadOnlyList<string> rawUpChain,
        IReadOnlyDictionary<string, string>? sourceTexts = null,
        VerbatimScope verbatimScope = VerbatimScope.All,
        string author = WikiPackage.AuthorAi,
        IReadOnlyList<Draft>? baseState = null)
    {
        var violations = new List<string>();
        if (!WikiPackage.AuthorValues.Contains(author))
            violations.Add($"автор пакета «{author}» не из перечня {string.Join(", ", WikiPackage.AuthorValues)}");
        var upChain = ValidateUpChain(rawUpChain, violations);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { sectionKey };
        allowed.UnionWith(upChain);

        // Запись в раздел, где IsWritable=false (сферы — следующая фича, ADR-036, решение 7).
        var writable = WikiSections.IsWritable(sectionKey);
        if (!writable.Writable)
            violations.Add($"нельзя писать в раздел '{sectionKey}': {writable.Reason}");

        var target = new List<(Draft Draft, WikiPage Page)>();
        var context = new List<(string Section, WikiPage Page)>();
        // Путь карточки нужен правилу дословности: по нему вызывающий дал текст источника.
        var allSections = new List<(string Section, WikiPage Page, string Path)>();
        foreach (var draft in state)
        {
            var section = WikiSections.Parse(draft.Path);
            if (section is null)
            {
                violations.Add($"{draft.Path}: путь не принадлежит ни одному разделу вики");
                continue;
            }
            var parsed = WikiPage.Parse(draft.Content);
            if (section == sectionKey)
            {
                if (parsed.Model is null)
                {
                    // Форма шапки и секций: неразобранная страница — нарушение по каждой причине.
                    foreach (var error in parsed.Errors)
                        violations.Add($"{draft.Path}: {error}");
                    continue;
                }
                var fileName = draft.Path.Split('/')[^1];
                if (fileName.StartsWith('_'))
                {
                    // _index/_log/_health — проекции, их генерирует код; страница туда не пишется.
                    violations.Add($"{draft.Path}: в '_*' пишёт код, страница туда не пишется");
                    continue;
                }
                target.Add((draft, parsed.Model));
                allSections.Add((section, parsed.Model, draft.Path));
            }
            else
            {
                // Чужой раздел нужен резолву: голая ссылка на имя, живущее только там,
                // — нарушение «вбок/вниз», а не тихая подмена. Неразобранное здесь только
                // мешает: в нарушениях оно не участвует.
                if (parsed.Model is null)
                    continue;
                allSections.Add((section, parsed.Model, draft.Path));
                if (allowed.Contains(section))
                    context.Add((section, parsed.Model));
            }
            if (section != sectionKey && !allowed.Contains(section))
                violations.Add($"{draft.Path}: страница раздела '{section}' вне раздела '{sectionKey}' и его цепочки вверх");
        }

        CheckPageForm(target, sectionKey, upChain, allSections, sourceTexts, verbatimScope, violations);
        CheckAuthorRules(target, author, baseState, violations);
        CheckUniqueness(target, context, violations);
        CheckLinks(target, sectionKey, upChain, allSections, violations);
        return violations;
    }

    // Права автора (ADR-038): карточку origin: human и секцию «Заметки» создаёт/меняет только
    // пакет human. «Новое» — отличие от состояния до пакета: ссылаться на уже существующую
    // карточку human и сохранять неизменной существующую «Заметки» пакету ai можно.
    private static void CheckAuthorRules(
        List<(Draft Draft, WikiPage Page)> target,
        string author,
        IReadOnlyList<Draft>? baseState,
        List<string> violations)
    {
        if (author == WikiPackage.AuthorHuman)
            return;
        var baseByPath = new Dictionary<string, WikiPage?>(StringComparer.Ordinal);
        foreach (var d in baseState ?? [])
            baseByPath[d.Path] = WikiPage.Parse(d.Content).Model;
        foreach (var (draft, page) in target)
        {
            baseByPath.TryGetValue(draft.Path, out var before);
            if (page.Frontmatter.Type == WikiFrontmatter.TypeSource
                && page.Frontmatter.Origin == OriginHuman
                && !(before is not null && before.Equals(page)))
                violations.Add($"{draft.Path}: карточку origin: human создаёт только пакет автора human");
            var notes = page.Notes?.Lines ?? [];
            var notesBefore = before?.Notes?.Lines ?? [];
            if (!notes.SequenceEqual(notesBefore))
                violations.Add($"{draft.Path}: секцию «{WikiPage.NotesSectionName}» меняет только пакет автора human");
        }
    }

    // «Вверх» — только сферы и общее, без повторов (ADR-036, решение 4): набор видимых
    // разделов или проект в цепочке — ошибка вызывающего, а не право на ссылку вбок.
    private static List<string> ValidateUpChain(IReadOnlyList<string> raw, List<string> violations)
    {
        var chain = new List<string>();
        foreach (var key in raw)
        {
            if (key != WikiSections.Common && !WikiSections.TrySphereId(key, out _))
                violations.Add($"цепочка вверх: '{key}' недопустим, вверх идут только spheres/* и {WikiSections.Common}");
            else if (chain.Contains(key))
                violations.Add($"цепочка вверх: '{key}' повторён");
            else
                chain.Add(key);
        }
        return chain;
    }

    // Форма страницы: чекбоксы, утверждение без сноски, сноска не на карточку,
    // длина цитаты, закрытый перечень origin.
    private static void CheckPageForm(
        List<(Draft Draft, WikiPage Page)> target,
        string sectionKey,
        IReadOnlyList<string> upChain,
        List<(string Section, WikiPage Page, string Path)> allSections,
        IReadOnlyDictionary<string, string>? sourceTexts,
        VerbatimScope verbatimScope,
        List<string> violations)
    {
        var reach = new List<string> { sectionKey };
        reach.AddRange(upChain);
        foreach (var (draft, page) in target)
        {
            foreach (var section in page.Sections)
                foreach (var line in section.Lines)
                {
                    if (CheckboxRx.IsMatch(line))
                        violations.Add($"{draft.Path}: страница вики не несёт задач '- [ ]'");
                    // «Заметки» — свободный текст без сносок и цитат (ADR-038).
                    if (section.Name == WikiPage.NotesSectionName && FootnoteRefRx.IsMatch(line))
                        violations.Add($"{draft.Path}: в секции «{WikiPage.NotesSectionName}» нет сносок: {line}");
                }

            if (page.Frontmatter.Type == WikiFrontmatter.TypeSource)
            {
                if (page.Frontmatter.Origin is not { } origin || !OriginValues.Contains(origin))
                    violations.Add($"{draft.Path}: origin '{page.Frontmatter.Origin}' вне закрытого перечня {string.Join(", ", OriginValues)}");
                if (page.Frontmatter.Origin == OriginSession &&
                    (page.Frontmatter.Ref is not { } sessionRef ||
                     !WikiPage.TryParseSessionRef(sessionRef, out _, out _, out _)))
                    violations.Add($"{draft.Path}: ref карточки origin: session — не 'session:{{id}}#m{{a}}-{{b}}': '{page.Frontmatter.Ref}'");
                continue; // карточка — данные: утверждений и цитат у неё нет
            }

            foreach (var section in page.Sections)
            {
                if (!WikiPage.AssertionSectionNames.Contains(section.Name))
                    continue;
                foreach (var line in section.Lines)
                {
                    var matches = FootnoteRefRx.Matches(line);
                    var last = matches.Count > 0 ? matches[matches.Count - 1] : null;
                    var endsWithFootnote = last is not null &&
                        last.Index + last.Length == line.TrimEnd().Length;
                    if (!endsWithFootnote)
                        violations.Add($"{draft.Path}: утверждение в '{section.Name}' без сноски: {line}");
                }
            }

            for (var fi = 0; fi < page.Footnotes.Count; fi++)
            {
                var footnote = page.Footnotes[fi];
                // Мера длины — общая с формой пакета (минор R-S2-3): одна функция
                // QuoteMatcher.MeasureQuoteLength, здесь остаётся только сообщение.
                var (raw, norm, outOfContract) = QuoteMatcher.MeasureQuoteLength(footnote.Quote);
                if (outOfContract)
                    violations.Add($"{draft.Path}: цитата {raw} символов ({norm} после нормализации), контракт {WikiPage.MinQuoteLength}–{WikiPage.MaxQuoteLength}");
                var card = FindPage(footnote.Target.Text, reach, allSections);
                if (card is null)
                    violations.Add($"{draft.Path}: сноска на неизвестную карточку '[[{footnote.Target.Text}]]'");
                else if (card.Value.Page.Frontmatter.Type != WikiFrontmatter.TypeSource)
                    violations.Add($"{draft.Path}: сноска на '[[{footnote.Target.Text}]]' — не карточка источника (type: source)");
                else if (sourceTexts is not null)
                {
                    // Дословность (ADR-035, §12г мера 1): цитата обязана быть в тексте
                    // источника с точностью до пробелов, кавычек и разметки (QuoteMatcher).
                    // Два вхождения в источнике допустимы — важно наличие, не единственность.
                    // Нет текста карточки (ключа нет, или значение null — так отдаёт
                    // словарь): в All — не подтвердить → нарушение (fail-closed); в
                    // GivenOnly — этой карточки здесь нет и быть не может: сноска, принятая
                    // раньше по своему источнику, не отвергает новую запись (ревью S6b-2, M2).
                    if (!sourceTexts.TryGetValue(card.Value.Path, out var sourceText) || sourceText is null)
                    {
                        if (verbatimScope == VerbatimScope.All)
                            violations.Add($"{draft.Path}: сноска s{fi + 1} на '[[{footnote.Target.Text}]]': нет текста источника карточки — дословность не подтвердить");
                    }
                    else if (!QuoteMatcher.ContainsVerbatim(sourceText, footnote.Quote))
                        violations.Add($"{draft.Path}: цитата сноски s{fi + 1} '[[{footnote.Target.Text}]]' отсутствует в тексте источника дословно");
                }
            }
        }
    }

    // Дубль заголовка/алиаса — в пределах раздела; дубль owns — в разделе и цепочке вверх
    // (ADR-035, решение 3; ADR-036, решение 4).
    private static void CheckUniqueness(
        List<(Draft Draft, WikiPage Page)> target,
        List<(string Section, WikiPage Page)> context,
        List<string> violations)
    {
        // Заголовки и алиасы — одно пространство имён: по нему резолвится ссылка.
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (draft, page) in target)
        {
            AddUnique(names, Norm(page.Title), draft.Path, "заголовок", violations);
            foreach (var alias in page.Frontmatter.Aliases)
                AddUnique(names, Norm(alias), draft.Path, "алиас", violations);
        }

        var owns = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (section, page) in context)
            foreach (var own in page.Frontmatter.Owns)
                owns.TryAdd(Norm(own), $"страница раздела '{section}' выше по цепочке");
        foreach (var (draft, page) in target)
            foreach (var own in page.Frontmatter.Owns)
                AddUnique(owns, Norm(own), draft.Path, "owns", violations);
    }

    private static void AddUnique(
        Dictionary<string, string> taken,
        string key,
        string path,
        string what,
        List<string> violations)
    {
        if (key.Length == 0)
            return;
        if (taken.TryGetValue(key, out var first))
        {
            if (first != path)
                violations.Add($"{path}: дубль {what} '{key}' — уже у {first}");
        }
        else
            taken[key] = path;
    }

    // Ссылки — только внутри раздела и вверх по цепочке (ADR-036, решение 4). Голая ссылка
    // резолвится ТОЛЬКО в текущем разделе и вверх: глобального поиска по имени нет, и имя,
    // живущее в чужом разделе, — нарушение «вбок/вниз», а не тихая подмена. Правила —
    // одни на проект: ResolveLink; здесь только формулировка нарушения по его исходу.
    private static void CheckLinks(
        List<(Draft Draft, WikiPage Page)> target,
        string sectionKey,
        IReadOnlyList<string> upChain,
        List<(string Section, WikiPage Page, string Path)> allSections,
        List<string> violations)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { sectionKey };
        allowed.UnionWith(upChain);
        foreach (var (draft, page) in target)
        {
            foreach (var link in page.OutboundLinks())
            {
                var res = ResolveLink(link.Text, sectionKey, upChain, allSections);
                switch (res.Outcome)
                {
                    case LinkOutcome.Resolved:
                        break;
                    case LinkOutcome.BadPrefix:
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' с неразобранным префиксом раздела");
                        break;
                    case LinkOutcome.Sideways when link.Text.Contains('/'):
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' в раздел '{res.Section}' — вбок или вниз, допустимы только {string.Join(", ", allowed)}");
                        break;
                    case LinkOutcome.Sideways:
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' резолвится только в чужом разделе '{res.Section}' — вбок или вниз");
                        break;
                    case LinkOutcome.PrefixedUnknown:
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' на неизвестную страницу");
                        break;
                    default:
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' не найдена в разделе и вверх по цепочке");
                        break;
                }
            }
        }
    }

    internal enum LinkOutcome
    {
        /// <summary>Ссылка найдена в разделе или вверх по цепочке; Section — где найдена.</summary>
        Resolved,
        /// <summary>Префикс ссылки — не раздел вики.</summary>
        BadPrefix,
        /// <summary>Раздел префиксной ссылки вне допустимых; или голая ссылка резолвится
        /// только в чужом разделе. Section — чужой раздел.</summary>
        Sideways,
        /// <summary>Раздел префиксной ссылки допустим, страницы в нём нет.</summary>
        PrefixedUnknown,
        /// <summary>Голая ссылка не найдена в разделе и вверх по цепочке.</summary>
        NotFound,
    }

    internal readonly record struct LinkResolution(LinkOutcome Outcome, string? Section);

    /// <summary>Резолв одной ссылки [[text]] — те же правила, что у всего гейта
    /// (ADR-036, решение 4): голая — свой раздел, потом вверх по цепочке; с префиксом —
    /// только раздел из «вверх». Снимок — разобранные страницы всех данных вызывающим
    /// разделов: для гейта это состояние пакета, для валидатора синтеза — снимок раздела
    /// и цепочки; имя из чужого раздела — не право на ссылку, а находка «вбок/вниз».
    /// Формулирует нарушение по исходу CheckLinks: второй копии правил в проекте нет.
    /// internal — им пользуется WikiPlanValidator (снятие неразрешимых ссылок в claim,
    /// очередь 1, 2А); сам гейт от этого не ослабляется.</summary>
    internal static LinkResolution ResolveLink(
        string text,
        string sectionKey,
        IReadOnlyList<string> upChain,
        IReadOnlyList<(string Section, WikiPage Page, string Path)> snapshot)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { sectionKey };
        allowed.UnionWith(upChain);
        if (text.Contains('/'))
        {
            // Ссылка с префиксом раздела — путь под pages/: префикс должен быть
            // разделом и лежать в «вверх».
            var section = WikiSections.Parse(text);
            if (section is null)
                return new(LinkOutcome.BadPrefix, null);
            if (!allowed.Contains(section))
                return new(LinkOutcome.Sideways, section);
            var title = text.Split('/')[^1];
            return FindPage(title, [section], snapshot) is not null
                ? new(LinkOutcome.Resolved, section)
                : new(LinkOutcome.PrefixedUnknown, section);
        }
        // Голая ссылка: свой раздел, потом вверх по цепочке; нигде больше.
        if (FindPage(text, [sectionKey], snapshot) is not null)
            return new(LinkOutcome.Resolved, sectionKey);
        foreach (var section in upChain)
            if (FindPage(text, [section], snapshot) is not null)
                return new(LinkOutcome.Resolved, section);
        var elsewhere = FindPage(text,
            snapshot.Select(s => s.Section).Distinct().Where(s => !allowed.Contains(s)), snapshot);
        return elsewhere is not null
            ? new(LinkOutcome.Sideways, elsewhere.Value.Section)
            : new(LinkOutcome.NotFound, null);
    }

    private static (string Section, WikiPage Page, string Path)? FindPage(
        string title,
        IEnumerable<string> sections,
        IReadOnlyList<(string Section, WikiPage Page, string Path)> allSections)
    {
        var wanted = new HashSet<string>(sections, StringComparer.Ordinal);
        var norm = Norm(title);
        foreach (var entry in allSections)
            if (wanted.Contains(entry.Section) && Norm(entry.Page.Title) == norm)
                return entry;
        return null;
    }

    // Нормализация для сравнения заголовков/алиасов/owns (ADR-036, решение 5:
    // «заголовок или aliases после нормализации»): регистр и обрамляющие пробелы.
    internal static string Norm(string text) => text.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();

    // ─── Снятие неразрешимых ссылок из текста (очередь 1, 2А; хроника — Q1-S4b) ────
    // Ссылка в тексте утверждения — та же [[…]], что читает парсер страницы (тот же
    // образец, что WikiPage.LinkRx: «]]» внутри цели не встречается). Резолв — только
    // ResolveLink выше: второй копии правил в проекте нет. Неразрешимая заменяется
    // своим текстом без скобок: алиасом после первого «|», а если его нет — заголовком
    // без префикса раздела. Уцелевшая остаётся как написана.
    // internal — пользуются: WikiPlanValidator (снятие неразрешимых ссылок в claim
    // при применении плана) и WikiChatIngestService.AppendChronicleAsync (снятие при
    // записи claim в хронику — для раздела дома хроники и его цепочки вверх, Q1-S4b).

    internal static string StripUnresolvedLinks(
        string claim, string sectionKey, IReadOnlyList<string> upChain,
        IReadOnlyList<(string Section, WikiPage Page, string Path)> snapshot, out int removed)
    {
        removed = 0;
        var sb = new StringBuilder(claim.Length);
        var pos = 0;
        foreach (Match m in WikiPage.LinkRx.Matches(claim))
        {
            var inner = m.Groups[1].Value;
            if (ResolveLink(inner, sectionKey, upChain, snapshot).Outcome == LinkOutcome.Resolved)
                continue;
            sb.Append(claim, pos, m.Index - pos).Append(PlainLinkText(inner));
            pos = m.Index + m.Length;
            removed++;
        }
        return removed == 0 ? claim : sb.Append(claim, pos, claim.Length - pos).ToString();
    }

    // Текст снятой ссылки: алиас после первого «|» (если он непустой), иначе — заголовок
    // без префикса раздела.
    private static string PlainLinkText(string inner)
    {
        var bar = inner.IndexOf('|');
        if (bar >= 0 && inner.Length > bar + 1)
            return inner[(bar + 1)..];
        var title = bar >= 0 ? inner[..bar] : inner;
        var slash = title.LastIndexOf('/');
        return slash >= 0 ? title[(slash + 1)..] : title;
    }
}
