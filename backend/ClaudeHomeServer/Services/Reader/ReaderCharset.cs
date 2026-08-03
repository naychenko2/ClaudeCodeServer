using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeHomeServer.Services.Reader;

/// <summary>
/// Кодировка тела: сначала <c>charset</c> из Content-Type, иначе &lt;meta charset&gt;
/// в начале документа (ADR-005 — иначе рунет без заголовка отдаёт кракозябры).
/// </summary>
public static partial class ReaderCharset
{
    public static Encoding Detect(string? contentTypeCharset, byte[] head)
    {
        if (!string.IsNullOrWhiteSpace(contentTypeCharset))
        {
            try { return Encoding.GetEncoding(contentTypeCharset.Trim().Trim('"', '\'')); }
            catch (ArgumentException) { /* неизвестное имя — падаем на сниффинг ниже */ }
        }

        // <meta>-теги всегда ASCII-совместимы в начале документа — латиница безопасна для sniff'а
        var head1K = Encoding.Latin1.GetString(head, 0, Math.Min(head.Length, 1024));
        var match = MetaCharsetRegex().Match(head1K);
        if (match.Success)
        {
            try { return Encoding.GetEncoding(match.Groups[1].Value); }
            catch (ArgumentException) { /* игнор */ }
        }

        return Encoding.UTF8;
    }

    [GeneratedRegex("""charset\s*=\s*["']?([a-zA-Z0-9_\-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetRegex();
}
