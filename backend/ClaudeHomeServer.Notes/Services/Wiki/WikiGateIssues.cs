using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Notes.Wiki;

/// <summary>Отказ гейта/правки человека с устойчивым кодом для редактора (ADR-038, «Контракт для
/// фронта»): Code — из закрытого перечня <see cref="WikiGateIssues"/>, Page — страница, к которой
/// относится отказ, Link — текст ссылки (если отказ про ссылку), Message — по-русски, раздел назван
/// живым именем, без guid.</summary>
public sealed record WikiIssue(string Code, string Page, string? Link, string Message);

/// <summary>Единая точка разбора нарушений гейта в коды (SV1-Г): фронт строки не парсит. Разбор
/// стоит рядом с гейтом и читает его же шаблоны сообщений (<see cref="WikiGate"/>,
/// WikiHumanEditPlanner); неопознанное — wiki_other. Тест на каждый код держит шаблоны в паре.</summary>
public static class WikiGateIssues
{
    public const string LinkSideways = "wiki_link_sideways";
    public const string LinkUnknown = "wiki_link_unknown";
    public const string LinkPrefix = "wiki_link_prefix";
    public const string HeaderField = "wiki_header_field";
    public const string TitleChange = "wiki_title_change";
    public const string NotesFootnote = "wiki_notes_footnote";
    public const string Parse = "wiki_parse";
    public const string Other = "wiki_other";

    /// <summary>Закрытый перечень кодов.</summary>
    public static readonly IReadOnlyList<string> Codes =
        [LinkSideways, LinkUnknown, LinkPrefix, HeaderField, TitleChange, NotesFootnote, Parse, Other];

    private const string Elsewhere = "другой раздел";

    private static readonly Regex PathPrefixRx = new(@"^(?<page>[^:\r\n]+\.md): (?<rest>.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex SectionKeyRx = new(@"(?:projects|spheres)/[A-Za-z0-9_-]+", RegexOptions.CultureInvariant);
    private static readonly Regex SidewaysPrefixRx = new(@"^ссылка '\[\[(?<link>.+?)\]\]' в раздел '(?<section>[^']+)' — вбок или вниз", RegexOptions.CultureInvariant);
    private static readonly Regex SidewaysBareRx = new(@"^ссылка '\[\[(?<link>.+?)\]\]' резолвится только в чужом разделе '(?<section>[^']+)'", RegexOptions.CultureInvariant);
    private static readonly Regex UnknownLinkRx = new(@"^ссылка '\[\[(?<link>.+?)\]\]' (?:не найдена в разделе и вверх по цепочке|на неизвестную страницу)$", RegexOptions.CultureInvariant);
    private static readonly Regex UnknownCardRx = new(@"^сноска на неизвестную карточку '\[\[(?<link>.+?)\]\]'$", RegexOptions.CultureInvariant);
    private static readonly Regex PrefixRx = new(@"^ссылка '\[\[(?<link>.+?)\]\]' с неразобранным префиксом раздела$", RegexOptions.CultureInvariant);

    /// <summary>Разобрать нарушения в отказы с кодами. liveName — живое имя раздела по ключу
    /// (null — имени нет: подставляется «другой раздел», guid наружу не идёт); defaultPage — страница
    /// для нарушений без пути (причины планировщика правки).</summary>
    public static IReadOnlyList<WikiIssue> Classify(
        IEnumerable<string> violations, string defaultPage, Func<string, string?> liveName) =>
        violations.Select(v => ClassifyOne(v, defaultPage, liveName)).ToList();

    private static WikiIssue ClassifyOne(string violation, string defaultPage, Func<string, string?> liveName)
    {
        var page = defaultPage;
        var rest = violation;
        if (PathPrefixRx.Match(violation) is { Success: true } pm)
        {
            page = pm.Groups["page"].Value;
            rest = pm.Groups["rest"].Value;
        }

        if ((SidewaysPrefixRx.Match(rest) is { Success: true } sp ? sp : SidewaysBareRx.Match(rest)) is { Success: true } side)
        {
            var link = side.Groups["link"].Value;
            return new WikiIssue(LinkSideways, page, link,
                $"Ссылка «{Display(link)}» ведёт в раздел «{Name(side.Groups["section"].Value, liveName)}» — вбок или вниз. " +
                "Ссылаться можно на свой раздел и разделы выше.");
        }
        if ((UnknownLinkRx.Match(rest) is { Success: true } ul ? ul : UnknownCardRx.Match(rest)) is { Success: true } unknown)
        {
            var link = unknown.Groups["link"].Value;
            return new WikiIssue(LinkUnknown, page, link,
                $"Ссылка «{Scrub(Display(link), liveName)}» не найдена: такой страницы нет в разделе и выше по цепочке.");
        }
        if (PrefixRx.Match(rest) is { Success: true } prefix)
        {
            var link = prefix.Groups["link"].Value;
            return new WikiIssue(LinkPrefix, page, link,
                $"Ссылка «{Scrub(link, liveName)}» начинается с неизвестного раздела.");
        }

        var message = Scrub(rest, liveName);
        if (rest.StartsWith("поля шапки ", StringComparison.Ordinal))
            return new WikiIssue(HeaderField, page, null, message);
        if (rest.StartsWith("заголовок страницы не правится", StringComparison.Ordinal))
            return new WikiIssue(TitleChange, page, null, message);
        if (rest.StartsWith($"в секции «{WikiPage.NotesSectionName}» нет сносок", StringComparison.Ordinal))
            return new WikiIssue(NotesFootnote, page, null, message);
        if (rest.Contains("текст страницы не разобран", StringComparison.Ordinal))
            return new WikiIssue(Parse, page, null, message);
        return new WikiIssue(Other, page, null, message);
    }

    // Для ссылки с префиксом раздела показываем заголовок страницы, не путь с ключом раздела.
    private static string Display(string link) => link.Contains('/') ? link.Split('/')[^1] : link;

    private static string Name(string sectionKey, Func<string, string?> liveName) =>
        liveName(sectionKey) is { Length: > 0 } name ? name : Elsewhere;

    // Любой ключ раздела в тексте → живое имя (или «другой раздел»): guid наружу не идёт.
    private static string Scrub(string text, Func<string, string?> liveName) =>
        SectionKeyRx.Replace(text, m => Name(m.Value, liveName));
}
