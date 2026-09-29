using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.HandsBridge.Browser.Dom;

/// <summary>
/// Текст поддерева из его <c>outerHTML</c> на нашей стороне — приближение <c>innerText</c> без
/// единой строки JS в странице. Скрипты, стили, комментарии и шаблоны выкидываются, блочные
/// элементы и <c>br</c> дают перевод строки, ячейки таблицы — табуляцию, сущности раскрываются.
/// Отличие от <c>innerText</c>: CSS не считается, поэтому текст скрытых элементов (<c>display:none</c>)
/// тоже попадает. Чистая функция; регулярки без откатов — страница не повесит разбор.
/// </summary>
public static class HtmlText
{
    /// <summary>Больше этого разметку не разбираем: ответ всё равно режется бюджетом намного раньше.</summary>
    public const int MaxHtmlChars = 2_000_000;

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex Comments = new("<!--.*?-->", Options);

    private static readonly Regex[] Hidden =
    [
        .. new[] { "script", "style", "noscript", "template", "svg", "head" }
            .Select(tag => new Regex($@"<{tag}\b[^>]*>.*?</{tag}\s*>", Options)),
    ];

    private static readonly Regex LineBreaks = new(
        @"<br\b[^>]*>|</?(p|div|li|ul|ol|tr|table|thead|tbody|tfoot|caption|h[1-6]|section|article|header|footer|nav|main|aside|" +
        @"blockquote|pre|dl|dt|dd|form|fieldset|legend|figure|figcaption|hr|address|details|summary|option|label)\b[^>]*>",
        Options);

    private static readonly Regex CellEnds = new(@"</(td|th)\s*>", Options);
    private static readonly Regex Tags = new("<[^>]*>", Options);
    private static readonly Regex Spaces = new("[  \r\f\v]+", Options);
    private static readonly Regex Tabs = new(" *\t *", Options);

    public static string ToText(string html)
    {
        if (html.Length > MaxHtmlChars)
            html = html[..MaxHtmlChars];

        html = Comments.Replace(html, " ");
        foreach (var hidden in Hidden)
            html = hidden.Replace(html, " ");
        html = LineBreaks.Replace(html, "\n");
        html = CellEnds.Replace(html, "\t");
        html = Tags.Replace(html, "");
        var text = WebUtility.HtmlDecode(html);

        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = Tabs.Replace(Spaces.Replace(raw, " "), "\t").Trim(' ', '\t');
            if (line.Length > 0)
                sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }
}
