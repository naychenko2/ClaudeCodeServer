using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

// Дедуп кодом до применения (ADR-035, §12г, мера 2). Чистые функции без I/O: состояние
// страницы и набор существующих страниц даёт вызывающий (у Хранителя они уже есть для
// гейта и манифеста).
//
// Исход разный, и он задан мерой 2: повтор утверждения ОТБРАСЫВАЕТСЯ молча — человек о
// нём не спрашивается; новая страница при совпадении — «возможный дубль»: её решает человек
// (строка таблицы §12г), поэтому здесь только находка со ссылкой на найденную страницу.
//
// Нормализация цитат и текста утверждений — QuoteMatcher (S6a): второй копии нет.
// Нормализация имён (заголовок/алиас/owns) — та же, чем WikiGate меряет уникальность
// (NFC, обрамляющие пробелы, регистр): дубль, который не увидел гейт, не должен увидеть
// и дедуп, и наоборот.
public static class WikiDedup
{
    /// Возможный дубль новой страницы: Path — путь найденной страницы (ссылка человеку),
    /// Field — поле новой страницы, которым пришлось совпадение, ExistingField — поле
    /// найденной. По этим двум человек видит, что именно столкнулось.
    public readonly record struct Duplicate(string Path, string Field, string ExistingField);

    private const string TitleField = "заголовок";
    private const string AliasField = "алиас";
    private const string OwnsField = "owns";

    // Секция, чьи ссылки цитату не занимают (ADR-039, решение 2): в «Историю» утверждение
    // переносят вытесненное — оно больше не владеет цитатой. Имя — из фиксированного набора
    // WikiPage.SectionNames.
    private const string HistorySectionName = "История";

    // Ссылка сноски в строке — форма парсера (WikiPage.FootnoteRefRx); здесь значимы только
    // канонические номера s1…sN, которые оставил после перенумерования разобранной модели.
    private static readonly Regex FootnoteRefRx = new(@"\[\^(s\d+)\]", RegexOptions.CultureInvariant);

    /// Существующая страница, против которой ищется дубль: путь (для ссылки человеку) и
    /// разобранная модель — Title, aliases, owns: именно они дают совпадение.
    public readonly record struct ExistingPage(string Path, WikiPage Page);

    /// Есть ли на странице это утверждение уже сейчас (мера 2, обе строки):
    /// - та же нормализованная цитата из того же источника (та же карточка в цели сноски);
    /// - нормализованный текст утверждения стоит в её секциях.
    /// True — отбросить: повтор молча, без «Входящих» и без пакета.
    ///
    /// sourceCard — цель сноски (текст ссылки на карточку источника), как она будет
    /// записана на странице: сравнение с уже стоящими сносками — по ней.
    /// page == null (страница не разобралась) — доказательства повтора нет: не отбрасываем.
    public static bool IsDuplicateStatement(WikiPage? page, string sourceCard, string statementText, string quote)
    {
        if (page is null)
            return false;
        return HasSameCitation(page, sourceCard, quote) || HasSameText(page, statementText);
    }

    private static bool HasSameCitation(WikiPage page, string sourceCard, string quote)
    {
        if (string.IsNullOrEmpty(sourceCard))
            return false;
        var normQuote = QuoteMatcher.NormalizeQuote(quote ?? "");
        if (normQuote.Length == 0)
            return false;
        foreach (var fn in page.Footnotes)
        {
            if (SameCitation(fn, sourceCard, normQuote))
                return true;
        }
        return false;
    }

    // Сравнение пары (цитата, карточка) — одна на весь дедуп: цитата через QuoteMatcher,
    // карточка — по тексту цели сноски как она написана. Второй нормализации нет (ADR-039, решение 2).
    private static bool SameCitation(WikiPage.WikiFootnote fn, string sourceCard, string normQuote) =>
        fn.Target.IsLink && fn.Target.Text == sourceCard && QuoteMatcher.NormalizeQuote(fn.Quote) == normQuote;

    // Текст утверждения сверяется с текстом секций страницы, а не с файлом целиком:
    // в сносках определений стоят цитаты источников — совпадение с ними не значит,
    // что утверждение уже есть на странице.
    private static bool HasSameText(WikiPage page, string statementText)
    {
        if (string.IsNullOrWhiteSpace(statementText))
            return false;
        var body = string.Join("\n", page.Sections.SelectMany(s => s.Lines));
        return QuoteMatcher.ContainsVerbatim(body, statementText);
    }

    /// «Одна цитата — одна страница» (ADR-039, решение 2): путь первой страницы раздела,
    /// на которой уже стоит та же нормализованная пара (цитата, карточка источника), либо null.
    /// Ключ дедупа — цитата, а не текст утверждения: пересказ каждый раз разный.
    ///
    /// Занятой считается сноска, на которую ссылается строка любой секции КРОМЕ «Истории»:
    /// сноска, на которую ссылается только «История», цитату не занимает — её вытеснили.
    /// exceptPage — страница самой операции: её сноски не считаются (повтор на той же
    /// странице ловит IsDuplicateStatement). Область — только данный список страниц:
    /// цепочки вверх и соседних разделов здесь нет (их набор даёт вызывающий).
    /// Страница без разобранной модели пропускается, исключение наружу не идёт.
    /// При нескольких владельцах — первый по порядку sectionPages.
    public static string? QuoteOwner(
        IReadOnlyList<WikiGate.Draft> sectionPages,
        string sourceCard,
        string quote,
        string? exceptPage)
    {
        if (sectionPages is null || string.IsNullOrEmpty(sourceCard))
            return null;
        var normQuote = QuoteMatcher.NormalizeQuote(quote ?? "");
        if (normQuote.Length == 0)
            return null;

        foreach (var draft in sectionPages)
        {
            if (string.IsNullOrEmpty(draft.Path) || draft.Path == exceptPage)
                continue;
            var page = WikiPage.Parse(draft.Content).Model;
            if (page is null)
                continue;
            var claimed = ClaimedFootnoteIds(page);
            for (var i = 0; i < page.Footnotes.Count; i++)
            {
                if (!SameCitation(page.Footnotes[i], sourceCard, normQuote))
                    continue;
                // В разобранной модели номера сносок канонические: сноска с индексом i — s{i+1}.
                if (!claimed.Contains($"s{i + 1}"))
                    continue;
                return draft.Path;
            }
        }
        return null;
    }

    // Номера сносок, на которые ссылается строка любой секции, кроме «Истории»: только
    // такая сноска занимает цитату (ADR-039, решение 2). Ссылки в разобранной модели
    // уже канонические (s1…sN) — их оставил после перенумерования парсер (WikiPage.Parse).
    private static HashSet<string> ClaimedFootnoteIds(WikiPage page)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in page.Sections)
        {
            if (section.Name == HistorySectionName)
                continue;
            foreach (var line in section.Lines)
                foreach (Match m in FootnoteRefRx.Matches(line))
                    ids.Add(m.Groups[1].Value);
        }
        return ids;
    }

    /// Возможный дубль новой страницы (мера 2, третья строка): совпадение заголовка,
    /// алиаса или owns новой страницы с любым из тех же полей существующих страниц —
    /// раздела и цепочки вверх (их набор даёт вызывающий: WikiSections.UpChain).
    /// Возвращает ПЕРВОЕ совпадение в перечислении: поля новой страницы по порядку
    /// (заголовок, алиасы, owns), затем страницы по данному порядку — детерминированно.
    ///
    /// existing == null или пустой список (раздел пуст) — дубля нет. Пустые имена новой
    /// страницы ничего не сравнивают: дубль не объявляется по пустоте.
    public static Duplicate? FindPageDuplicate(
        string newTitle,
        IReadOnlyList<string>? newAliases,
        IReadOnlyList<string>? newOwns,
        IReadOnlyList<ExistingPage>? existing)
    {
        var wanted = NamesOf(newTitle, newAliases, newOwns);
        if (wanted.Count == 0 || existing is null)
            return null;

        foreach (var (path, page) in existing)
        {
            if (page is null || string.IsNullOrEmpty(path))
                continue;
            foreach (var (have, haveField) in NamesOf(page.Title, page.Frontmatter.Aliases, page.Frontmatter.Owns))
            {
                foreach (var (want, wantField) in wanted)
                {
                    if (want == have)
                        return new Duplicate(path, wantField, haveField);
                }
            }
        }
        return null;
    }

    // Имена страницы для сравнения: заголовок и алиасы — одно пространство имён (по нему
    // резолвится ссылка), owns — ключ сущности (уникален в разделе и вверх по цепочке).
    private static List<(string Norm, string Field)> NamesOf(
        string? title,
        IReadOnlyList<string>? aliases,
        IReadOnlyList<string>? owns)
    {
        var names = new List<(string, string)>();
        AddName(names, title, TitleField);
        if (aliases is not null)
            foreach (var a in aliases)
                AddName(names, a, AliasField);
        if (owns is not null)
            foreach (var o in owns)
                AddName(names, o, OwnsField);
        return names;
    }

    private static void AddName(List<(string Norm, string Field)> names, string? value, string field)
    {
        if (value is null)
            return;
        var norm = NormalizeName(value);
        if (norm.Length > 0)
            names.Add((norm, field));
    }

    /// Нормализация имени для сравнения (ADR-036, решение 5: «заголовок или aliases после
    /// нормализации»): NFC, обрамляющие пробелы, регистр. Та же, что у уникальности WikiGate.
    public static string NormalizeName(string text) =>
        text.Trim().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();
}
