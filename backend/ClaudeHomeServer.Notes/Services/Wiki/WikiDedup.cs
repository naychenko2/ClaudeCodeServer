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
            if (fn.Target.IsLink && fn.Target.Text == sourceCard && QuoteMatcher.NormalizeQuote(fn.Quote) == normQuote)
                return true;
        }
        return false;
    }

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
