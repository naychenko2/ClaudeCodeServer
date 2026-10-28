using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Гейт страницы вики (ADR-035, решение 3; ADR-036, решение 4): чистая функция
// (состояние после изменения, раздел) → нарушения. Состояние — тексты страниц
// раздела и разделов выше по цепочке (их даёт вызывающий: WikiSections.UpChain);
// гейт сам их разбирает и проверяет правила. Нарушение — строка с причиной для
// человека и для Хранителя: «на ремонт модели даётся одна попытка с перечнем нарушений».
// Дословность цитаты проверяется, когда вызывающий дал тексты источников (sourceTexts,
// захват — S6h/S6j): каждая сноска сверяется с текстом своей карточки через QuoteMatcher
// (ADR-035, §12г мера 1). При sourceTexts == null правило не выполняется — это режим
// по умолчанию до слоя захвата, а не «пройдено молча».
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

    public static readonly IReadOnlyList<string> OriginValues =
        [OriginNote, OriginFile, OriginSession, OriginUrl, OriginTask, OriginDossier, OriginMemory];

    // Задача-чекбокс на странице вики запрещена формой страницы (ADR-035, решение 2).
    private static readonly Regex CheckboxRx = new(@"^\s*[-*] \[[ xX]\]", RegexOptions.CultureInvariant);
    private static readonly Regex FootnoteRefRx = new(@"\[\^([^]\r\n]+)\]", RegexOptions.CultureInvariant);

    /// <summary>Страница состояния: путь относительно pages/ и текст файла.</summary>
    public readonly record struct Draft(string Path, string Content);

    /// <summary>Проверяет состояние после изменения перед записью в раздел sectionKey.
    /// Контракт state: страницы раздела sectionKey плюс страницы разделов цепочки upChain;
    /// путь, не принадлежащий вики, и страница вне этих разделов — нарушение (fail-closed).
    /// upChain — цепочка «вверх» от раздела (WikiSections.UpChain): по ней резолвятся
    /// ссылки и проверяется уникальность owns; гейт сам сверяет её с ADR-036, решение 4:
    /// допустимы только spheres/* и common, без повторов. Пустой список — нарушений нет.
    /// sourceTexts — тексты источников по пути карточки (ключ — Draft.Path карточки):
    /// правило дословности (ADR-035, §12г мера 1). null — правило не проверяется (захват
    /// источников — S6h/S6j); задан — каждая сноска разрешённой карточки требует её текста,
    /// цитата без дословного вхождения в тексте — нарушение.</summary>
    public static IReadOnlyList<string> Check(
        IReadOnlyList<Draft> state,
        string sectionKey,
        IReadOnlyList<string> rawUpChain,
        IReadOnlyDictionary<string, string>? sourceTexts = null)
    {
        var violations = new List<string>();
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

        CheckPageForm(target, sectionKey, upChain, allSections, sourceTexts, violations);
        CheckUniqueness(target, context, violations);
        CheckLinks(target, sectionKey, upChain, allSections, violations);
        return violations;
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
        List<string> violations)
    {
        var reach = new List<string> { sectionKey };
        reach.AddRange(upChain);
        foreach (var (draft, page) in target)
        {
            foreach (var section in page.Sections)
                foreach (var line in section.Lines)
                    if (CheckboxRx.IsMatch(line))
                        violations.Add($"{draft.Path}: страница вики не несёт задач '- [ ]'");

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
                // Минимальная длина — по нормализованной строке (той же, по которой
                // сверяется дословность): цитата из одной разметки («*******a») не
                // содержательна, даже если сырая длина в контракте. Верхняя граница —
                // по сырой: нормализация только укорачивает, верхнюю меру она не обходит.
                var normQuote = QuoteMatcher.NormalizeQuote(footnote.Quote);
                if (normQuote.Length < WikiPage.MinQuoteLength || footnote.Quote.Length > WikiPage.MaxQuoteLength)
                    violations.Add($"{draft.Path}: цитата {footnote.Quote.Length} символов ({normQuote.Length} после нормализации), контракт {WikiPage.MinQuoteLength}–{WikiPage.MaxQuoteLength}");
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
                    // словарь) → не подтвердить → нарушение (fail-closed), а не исключение.
                    if (!sourceTexts.TryGetValue(card.Value.Path, out var sourceText) || sourceText is null)
                        violations.Add($"{draft.Path}: сноска s{fi + 1} на '[[{footnote.Target.Text}]]': нет текста источника карточки — дословность не подтвердить");
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

    // Ссылки — только внутри раздела и вверх по цепочке (ADR-036, решение 4).
    // Голая ссылка резолвится ТОЛЬКО в текущем разделе и вверх: глобального поиска по
    // имени нет, и имя, живущее в чужом разделе, — нарушение «вбок/вниз», а не тихая подмена.
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
                if (link.Text.Contains('/'))
                {
                    // Ссылка с префиксом раздела — путь под pages/: префикс должен быть
                    // разделом и лежать в «вверх».
                    var section = WikiSections.Parse(link.Text);
                    var title = link.Text.Split('/')[^1];
                    if (section is null)
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' с неразобранным префиксом раздела");
                    else if (!allowed.Contains(section))
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' в раздел '{section}' — вбок или вниз, допустимы только {string.Join(", ", allowed)}");
                    else if (FindPage(title, [section], allSections) is null)
                        violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' на неизвестную страницу");
                    continue;
                }
                // Голая ссылка: свой раздел, потом вверх по цепочке; нигде больше.
                if (FindPage(link.Text, [sectionKey], allSections) is not null)
                    continue;
                if (FindPage(link.Text, upChain, allSections) is not null)
                    continue;
                var elsewhere = FindPage(link.Text,
                    allSections.Select(s => s.Section).Distinct().Where(s => !allowed.Contains(s)), allSections);
                if (elsewhere is not null)
                    violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' резолвится только в чужом разделе '{elsewhere.Value.Section}' — вбок или вниз");
                else
                    violations.Add($"{draft.Path}: ссылка '[[{link.Text}]]' не найдена в разделе и вверх по цепочке");
            }
        }
    }

    private static (string Section, WikiPage Page, string Path)? FindPage(
        string title,
        IEnumerable<string> sections,
        List<(string Section, WikiPage Page, string Path)> allSections)
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
    private static string Norm(string text) => text.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
}
