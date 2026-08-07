using System.Text;
using AngleSharp.Dom;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Обход DOM статьи по белому списку тегов -> markdown (ADR-005, раздел «Решение», шаг 7).
/// <c>script</c>, <c>style</c>, <c>iframe</c>, <c>form</c> и прочие исполняемые/интерактивные
/// теги отбрасываются вместе с содержимым — физически, не по чёрному списку атрибутов.
/// Всё, что не в белом и не в опасном списке (div, span, section, figure…), — прозрачный
/// контейнер: сам тег пропускается, но его дети обходятся дальше.
/// </summary>
public static class HtmlToMarkdownConverter
{
    // Исполняемое/интерактивное — не переживает конвертацию вместе с содержимым.
    private static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "form", "noscript", "svg", "math",
        "object", "embed", "applet", "audio", "video", "canvas", "template",
        "button", "input", "select", "textarea",
    };

    private static readonly HashSet<string> BlockWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "h1", "h2", "h3", "h4", "h5", "h6", "p", "ul", "ol", "pre", "blockquote", "table", "hr",
    };

    // Прозрачные блочные контейнеры — тег пропускается, содержимое обходится как блок дальше.
    private static readonly HashSet<string> TransparentBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "header", "footer", "nav", "aside", "main", "figure",
        "figcaption", "address", "details", "summary", "dl", "dt", "dd", "center",
    };

    // Схемы ссылок/картинок, которые уходят на провод как markdown-ссылка; всё прочее
    // (javascript:, data:, vbscript:…) разворачивается в простой текст.
    private static readonly HashSet<string> LinkSchemes = new(StringComparer.OrdinalIgnoreCase)
    { "http", "https", "mailto" };
    private static readonly HashSet<string> ImageSchemes = new(StringComparer.OrdinalIgnoreCase)
    { "http", "https" };

    public static string Convert(IElement root)
    {
        var blocks = new List<string>();
        RenderBlockChildren(root, blocks);
        return string.Join("\n\n", blocks.Where(b => !string.IsNullOrWhiteSpace(b)));
    }

    private static void RenderBlockChildren(INode container, List<string> blocks)
    {
        var pendingInline = new StringBuilder();

        void FlushInline()
        {
            var text = pendingInline.ToString().Trim();
            pendingInline.Clear();
            if (text.Length > 0) blocks.Add(text);
        }

        foreach (var child in container.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                pendingInline.Append(EscapeText(child.TextContent));
                continue;
            }
            if (child is not IElement el) continue;
            var tag = el.TagName;

            if (Dangerous.Contains(tag)) continue;

            if (BlockWhitelist.Contains(tag))
            {
                FlushInline();
                var rendered = RenderBlock(el);
                if (!string.IsNullOrWhiteSpace(rendered)) blocks.Add(rendered);
                continue;
            }

            if (TransparentBlock.Contains(tag))
            {
                FlushInline();
                var nested = new List<string>();
                RenderBlockChildren(el, nested);
                blocks.AddRange(nested);
                continue;
            }

            // li вне ul/ol и прочие незнакомые теги — как строчное содержимое текущего абзаца
            pendingInline.Append(RenderInline(el));
        }

        FlushInline();
    }

    private static string RenderBlock(IElement el)
    {
        var tag = el.TagName.ToLowerInvariant();
        switch (tag)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var level = tag[1] - '0';
                return new string('#', level) + " " + RenderInline(el).Trim();

            case "p":
                return RenderInline(el).Trim();

            case "hr":
                return "---";

            case "blockquote":
            {
                var inner = new List<string>();
                RenderBlockChildren(el, inner);
                var body = string.Join("\n\n", inner);
                return PrefixLines(body, "> ");
            }

            case "pre":
                return RenderCodeBlock(el);

            case "ul":
            case "ol":
                return RenderList(el, ordered: tag == "ol");

            case "table":
                return RenderTable(el);

            default:
                return RenderInline(el).Trim();
        }
    }

    private static string RenderCodeBlock(IElement pre)
    {
        var codeEl = pre.Children.FirstOrDefault(c => c.TagName.Equals("code", StringComparison.OrdinalIgnoreCase));
        var raw = (codeEl ?? (INode)pre).TextContent.TrimEnd('\n');
        var lang = "";
        var classAttr = codeEl?.GetAttribute("class") ?? "";
        foreach (var cls in classAttr.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (cls.StartsWith("language-", StringComparison.OrdinalIgnoreCase))
                lang = cls["language-".Length..];

        var fence = new string('`', LongestBacktickRun(raw) + 1);
        if (fence.Length < 3) fence = "```";
        return $"{fence}{lang}\n{raw}\n{fence}";
    }

    private static int LongestBacktickRun(string text)
    {
        var max = 0;
        var current = 0;
        foreach (var ch in text)
        {
            if (ch == '`') { current++; max = Math.Max(max, current); }
            else current = 0;
        }
        return max;
    }

    private static string RenderList(IElement list, bool ordered, int depth = 0)
    {
        var lines = new List<string>();
        var index = 1;
        var indent = new string(' ', depth * 2);
        foreach (var li in list.Children.Where(c => c.TagName.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            var marker = ordered ? $"{index}. " : "- ";
            index++;

            // Вложенные списки рендерим отдельно и отступаем; остальное содержимое li — как inline-абзац
            var nestedLists = li.Children
                .Where(c => c.TagName is "UL" or "OL")
                .ToList();
            var inlineText = RenderInlineExcluding(li, nestedLists).Trim();

            lines.Add(indent + marker + inlineText);
            foreach (var nested in nestedLists)
            {
                var nestedOrdered = nested.TagName.Equals("ol", StringComparison.OrdinalIgnoreCase);
                lines.Add(RenderList(nested, nestedOrdered, depth + 1));
            }
        }
        return string.Join("\n", lines);
    }

    private static string RenderTable(IElement table)
    {
        var rows = table.QuerySelectorAll("tr").ToList();
        if (rows.Count == 0) return "";

        var rendered = rows
            .Select(r => r.Children
                .Where(c => c.TagName is "TD" or "TH")
                .Select(c => RenderInline(c).Replace("\n", " ").Replace("|", "\\|").Trim())
                .ToList())
            .ToList();

        var columns = rendered.Max(r => r.Count);
        if (columns == 0) return "";

        string Row(List<string> cells)
        {
            var padded = cells.Concat(Enumerable.Repeat("", columns - cells.Count));
            return "| " + string.Join(" | ", padded) + " |";
        }

        var sb = new StringBuilder();
        sb.AppendLine(Row(rendered[0]));
        sb.AppendLine("| " + string.Join(" | ", Enumerable.Repeat("---", columns)) + " |");
        for (var i = 1; i < rendered.Count; i++)
            sb.AppendLine(Row(rendered[i]));
        return sb.ToString().TrimEnd('\n');
    }

    private static string RenderInline(INode node) => RenderInlineExcluding(node, []);

    private static string RenderInlineExcluding(INode node, IReadOnlyCollection<IElement> exclude)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                sb.Append(EscapeText(child.TextContent));
                continue;
            }
            if (child is not IElement el || exclude.Contains(el)) continue;
            var tag = el.TagName;

            if (Dangerous.Contains(tag)) continue;

            switch (tag.ToLowerInvariant())
            {
                case "br":
                    sb.Append('\n');
                    break;
                case "strong" or "b":
                    sb.Append("**").Append(RenderInline(el).Trim()).Append("**");
                    break;
                case "em" or "i":
                    sb.Append('*').Append(RenderInline(el).Trim()).Append('*');
                    break;
                case "code":
                    sb.Append(RenderInlineCode(el));
                    break;
                case "a":
                    sb.Append(RenderLink(el));
                    break;
                case "img":
                    sb.Append(RenderImage(el));
                    break;
                // блочные теги, всплывшие внутри inline-контекста (напр. p в li) — просто разворачиваем
                default:
                    sb.Append(RenderInline(el));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string RenderInlineCode(IElement code)
    {
        var text = code.TextContent;
        var run = LongestBacktickRun(text);
        var delim = new string('`', Math.Max(1, run) + (run > 0 ? 1 : 0));
        if (delim.Length == 1) delim = "`";
        var pad = text.StartsWith('`') || text.EndsWith('`') ? " " : "";
        return $"{delim}{pad}{text}{pad}{delim}";
    }

    private static string RenderLink(IElement a)
    {
        var href = a.GetAttribute("href");
        var text = RenderInline(a).Trim();
        if (string.IsNullOrWhiteSpace(text)) text = href ?? "";
        if (!IsAllowedUri(href, LinkSchemes)) return text;
        return $"[{text}]({EscapeParen(href!)})";
    }

    private static string RenderImage(IElement img)
    {
        var src = img.GetAttribute("src");
        var alt = EscapeText(img.GetAttribute("alt") ?? "");
        if (!IsAllowedUri(src, ImageSchemes)) return alt;
        return $"![{alt}]({EscapeParen(src!)})";
    }

    private static bool IsAllowedUri(string? value, HashSet<string> allowedSchemes) =>
        !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        allowedSchemes.Contains(uri.Scheme);

    private static string EscapeParen(string url) => url.Replace(" ", "%20").Replace(")", "%29");

    private static string PrefixLines(string text, string prefix)
    {
        if (text.Length == 0) return prefix.TrimEnd();
        return string.Join("\n", text.Split('\n').Select(l => prefix + l));
    }

    // Экранируем markdown-значимые символы в тексте статьи, чтобы он не превратился
    // в разметку (заголовки, списки, ссылки, code-span) сам по себе.
    private static readonly char[] EscapeChars = ['\\', '`', '*', '_', '{', '}', '[', ']', '(', ')', '#', '+', '!', '<', '>', '|'];

    private static string EscapeText(string text)
    {
        if (text.Length == 0) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (Array.IndexOf(EscapeChars, ch) >= 0) sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
